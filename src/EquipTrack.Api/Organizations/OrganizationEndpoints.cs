using System.Net.Mail;
using System.Text.RegularExpressions;
using EquipTrack.Api.Auth;
using EquipTrack.Api.Data;
using EquipTrack.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EquipTrack.Api.Organizations;

public sealed record CreateOrganizationRequest(string? Name, string? Slug, string? AdminEmail, string? AdminName);
public sealed record InviteMemberRequest(string? Email, string? Name, string? Role);
public sealed record UpdateMemberRequest(string? Role, string? Status);

public sealed record MemberDto(Guid Id, string Email, string Name, string Role, string Status, string? UserId,
    DateTimeOffset InvitedAt, DateTimeOffset? JoinedAt)
{
    public static MemberDto From(OrganizationMember m) =>
        new(m.Id, m.Email, m.DisplayName, m.Role, m.Status.ToString().ToLowerInvariant(), m.UserId, m.InvitedAt, m.JoinedAt);
}

public static partial class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", GetMe).RequireAuthorization();

        var admin = app.MapGroup("/api/admin").RequireAuthorization();
        admin.MapPost("/organizations", CreateOrganization);

        var org = app.MapGroup("/api/org").RequireAuthorization();
        org.MapGet("/members", ListMembers);
        org.MapPost("/members", InviteMember);
        org.MapPatch("/members/{id:guid}", UpdateMember);

        return app;
    }

    /// <summary>Who am I, which organisation, which role. The frontend uses this to route.</summary>
    internal static async Task<IResult> GetMe(HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (member, result) = await resolver.ResolveAsync(http, ct);
        var user = http.User;
        var sysadmin = resolver.IsSysadmin(user);

        if (member is null && !sysadmin && result != MemberResolution.AmbiguousOrganization)
            return CurrentMemberResolver.Problem(result);

        var userId = CurrentMemberResolver.UserId(user)!;
        var memberships = await db.OrganizationMembers
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id,
                (m, o) => new { organizationId = o.Id, organizationName = o.Name, organizationSlug = o.Slug, role = m.Role })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            userId,
            email = CurrentMemberResolver.Email(user),
            emailVerified = CurrentMemberResolver.EmailVerified(user),
            name = member?.Name,
            isSysadmin = sysadmin,
            activeOrganizationId = member?.OrganizationId,
            role = member?.Role,
            memberships,
        });
    }

    /// <summary>Platform bootstrap: create a customer organisation and invite its first admin.</summary>
    internal static async Task<IResult> CreateOrganization(
        CreateOrganizationRequest req, HttpContext http, CurrentMemberResolver resolver, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        if (!resolver.IsSysadmin(http.User)) return Results.Forbid();

        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim();
        var slug = req.Slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 200) errors["name"] = ["Required (max 200 characters)."];
        if (slug is null || !SlugPattern().IsMatch(slug)) errors["slug"] = ["Use 3-50 lowercase letters, digits or hyphens."];
        var adminEmail = NormalizeEmail(req.AdminEmail);
        if (adminEmail is null) errors["adminEmail"] = ["Enter a valid email address."];
        if (errors.Count == 0 && await db.Organizations.AnyAsync(o => o.Slug == slug, ct)) errors["slug"] = ["Already taken."];
        var adminName = string.IsNullOrWhiteSpace(req.AdminName) ? adminEmail : req.AdminName.Trim();
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var now = clock.GetUtcNow();
        var org = new Organization { Name = name!, Slug = slug!, CreatedAt = now };
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            Email = adminEmail!,
            DisplayName = adminName!,
            Role = Roles.OrgAdmin,
            InvitedByUserId = CurrentMemberResolver.UserId(http.User),
            InvitedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/organizations/{org.Id}", new { org.Id, org.Name, org.Slug });
    }

    internal static async Task<IResult> ListMembers(
        string? role, HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);
        if (!RoleSets.CanListMembers.Contains(me.Role)) return Results.Forbid();

        var query = db.OrganizationMembers.Where(m => m.OrganizationId == me.OrganizationId);
        // Non-admins only see active members (e.g. to choose an assignee).
        if (me.Role != Roles.OrgAdmin) query = query.Where(m => m.Status == MemberStatus.Active);
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(m => m.Role == role);

        var members = await query.OrderBy(m => m.DisplayName).ToListAsync(ct);
        return Results.Ok(members.Select(MemberDto.From));
    }

    internal static async Task<IResult> InviteMember(
        InviteMemberRequest req, HttpContext http, CurrentMemberResolver resolver, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);
        if (me.Role != Roles.OrgAdmin) return Results.Forbid();

        var errors = new Dictionary<string, string[]>();
        var email = NormalizeEmail(req.Email);
        if (email is null) errors["email"] = ["Enter a valid email address."];
        else if (await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == me.OrganizationId && m.Email == email, ct))
            errors["email"] = ["This person is already a member or has a pending invitation."];
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 200) errors["name"] = ["Required (max 200 characters)."];
        if (req.Role is null || !RoleSets.IsOrgRole(req.Role)) errors["role"] = ["Choose a valid role."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var member = new OrganizationMember
        {
            OrganizationId = me.OrganizationId,
            Email = email!,
            DisplayName = req.Name!.Trim(),
            Role = req.Role!,
            InvitedByUserId = me.UserId,
            InvitedAt = clock.GetUtcNow(),
        };
        db.OrganizationMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/org/members/{member.Id}", MemberDto.From(member));
    }

    internal static async Task<IResult> UpdateMember(
        Guid id, UpdateMemberRequest req, HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);
        if (me.Role != Roles.OrgAdmin) return Results.Forbid();

        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.Id == id && m.OrganizationId == me.OrganizationId, ct);
        if (member is null) return Results.NotFound();
        if (member.Id == me.MemberId) return Results.Problem(statusCode: 409, title: "You cannot change your own role or status.");

        if (req.Role is not null)
        {
            if (!RoleSets.IsOrgRole(req.Role)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Choose a valid role."] });
            member.Role = req.Role;
        }

        if (req.Status is not null)
        {
            if (req.Status == "suspended") member.Status = MemberStatus.Suspended;
            else if (req.Status == "active" && member.UserId is not null) member.Status = MemberStatus.Active;
            else return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Use 'active' (joined members only) or 'suspended'."] });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(MemberDto.From(member));
    }

    private static string? NormalizeEmail(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return trimmed is { Length: > 0 and <= 254 } && MailAddress.TryCreate(trimmed, out var parsed)
               && parsed.Address == trimmed && parsed.Host.Contains('.')
            ? trimmed
            : null;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,48})[a-z0-9]$")]
    private static partial Regex SlugPattern();
}
