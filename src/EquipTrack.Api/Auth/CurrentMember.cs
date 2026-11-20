using System.Security.Claims;
using EquipTrack.Api.Data;
using EquipTrack.Api.Domain;
using EquipTrack.Api.Email;
using EquipTrack.Api.Organizations;
using EquipTrack.Api.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EquipTrack.Api.Auth;

/// <summary>The signed-in user acting inside one organisation, with their role from the database.</summary>
public sealed record MemberContext(Guid OrganizationId, Guid MemberId, string UserId, string Name, string Email, string Role)
{
    public Actor ToActor() => new(UserId, Name, Role);
}

public enum MemberResolution { Ok, Unauthenticated, EmailNotVerified, NoMembership, AmbiguousOrganization }

/// <summary>
/// Resolves who the caller is inside EquipTrack. Identity comes from the
/// validated token; organisation and role come ONLY from organization_members.
/// </summary>
public sealed class CurrentMemberResolver(
    AppDbContext db, IOptions<NeonAuthOptions> options, TimeProvider clock, IAppMailer mailer, ILogger<CurrentMemberResolver> logger)
{
    public const string OrganizationHeader = "X-Organization-Id";

    public static string? UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
    public static string Email(ClaimsPrincipal user) => (user.FindFirstValue(ClaimTypes.Email) ?? string.Empty).Trim().ToLowerInvariant();
    public static bool EmailVerified(ClaimsPrincipal user) => user.FindFirstValue(NeonAuthenticationHandler.EmailVerifiedClaim) == "true";

    public bool IsSysadmin(ClaimsPrincipal user) =>
        EmailVerified(user)
        && options.Value.SysadminEmails.Any(e => string.Equals(e.Trim(), Email(user), StringComparison.OrdinalIgnoreCase));

    public async Task<(MemberContext? Member, MemberResolution Result)> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        var user = http.User;
        if (UserId(user) is not { } userId) return (null, MemberResolution.Unauthenticated);

        await ClaimInvitationsAsync(user, userId, ct);

        var memberships = await db.OrganizationMembers
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active)
            .ToListAsync(ct);

        if (memberships.Count == 0 && await AutoProvisionAsync(user, userId, ct) is { } joined)
            memberships.Add(joined);

        if (memberships.Count == 0)
            return (null, EmailVerified(user) ? MemberResolution.NoMembership : MemberResolution.EmailNotVerified);

        OrganizationMember member;
        if (memberships.Count == 1)
        {
            member = memberships[0];
        }
        else if (Guid.TryParse(http.Request.Headers[OrganizationHeader], out var orgId)
                 && memberships.FirstOrDefault(m => m.OrganizationId == orgId) is { } chosen)
        {
            member = chosen;
        }
        else
        {
            return (null, MemberResolution.AmbiguousOrganization);
        }

        // Track activity (throttled to one write per 5 minutes per member).
        var now = clock.GetUtcNow();
        if (member.LastActiveAt is null || now - member.LastActiveAt > TimeSpan.FromMinutes(5))
        {
            member.LastActiveAt = now;
            await db.SaveChangesAsync(ct);
        }

        return (new MemberContext(member.OrganizationId, member.Id, userId, member.DisplayName, member.Email, member.Role),
            MemberResolution.Ok);
    }

    /// <summary>
    /// Binds pending, unexpired invitations to this user. Only with a VERIFIED email —
    /// otherwise anyone could sign up with someone else's address and take their role.
    /// </summary>
    private async Task ClaimInvitationsAsync(ClaimsPrincipal user, string userId, CancellationToken ct)
    {
        if (!EmailVerified(user)) return;
        var email = Email(user);
        if (email.Length == 0) return;

        var now = clock.GetUtcNow();
        var invitations = await db.OrganizationMembers
            .Where(m => m.Email == email && m.UserId == null && m.Status == MemberStatus.Invited)
            .ToListAsync(ct);
        invitations = invitations.Where(i => i.InviteExpiresAt is null || i.InviteExpiresAt > now).ToList();
        if (invitations.Count == 0) return;

        foreach (var inv in invitations)
        {
            inv.UserId = userId;
            inv.Status = MemberStatus.Active;
            inv.JoinedAt = now;
            Audit.Record(db, inv.OrganizationId, userId, inv.DisplayName, inv.Role, "member.joined", "user",
                inv.Id.ToString(), $"{inv.DisplayName} <{inv.Email}>", now);
        }
        await db.SaveChangesAsync(ct);

        foreach (var inv in invitations) await NotifyAdminsOfJoinAsync(inv, ct);
    }

    /// <summary>
    /// Domain auto-join: a verified email at a domain that exactly one organisation has
    /// claimed (with AutoProvision on) joins that organisation with its default role.
    /// Public email providers can never be claimed.
    /// </summary>
    private async Task<OrganizationMember?> AutoProvisionAsync(ClaimsPrincipal user, string userId, CancellationToken ct)
    {
        if (!EmailVerified(user)) return null;
        var email = Email(user);
        var at = email.LastIndexOf('@');
        if (at < 1) return null;
        var domain = email[(at + 1)..];
        if (OrgSettingsEndpoints.PublicEmailDomains.Contains(domain)) return null;

        var candidates = await db.Organizations.Where(o => o.AutoProvision).ToListAsync(ct);
        var matches = candidates.Where(o => o.AllowedEmailDomains.Contains(domain)).ToList();
        if (matches.Count != 1) return null;
        var org = matches[0];

        // Never re-add someone the admin suspended or removed from this organisation.
        if (await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == org.Id && m.Email == email, ct)) return null;
        if (org.Seats is { } seats && await OrgSettingsEndpoints.SeatsUsedAsync(db, org.Id, ct) >= seats)
        {
            logger.LogWarning("Auto-join for {Email} to {Org} skipped: seat limit reached", email, org.Slug);
            return null;
        }

        var now = clock.GetUtcNow();
        var name = user.FindFirstValue(ClaimTypes.Name) is { Length: > 0 } n ? n : email;
        var member = new OrganizationMember
        {
            OrganizationId = org.Id,
            Email = email,
            UserId = userId,
            DisplayName = name.Length > 200 ? name[..200] : name,
            Role = org.AutoProvisionRole,
            Status = MemberStatus.Active,
            InvitedByUserId = "auto-provision",
            InvitedAt = now,
            JoinedAt = now,
        };
        db.OrganizationMembers.Add(member);
        Audit.Record(db, org.Id, userId, member.DisplayName, member.Role, "member.auto_provisioned", "user",
            member.Id.ToString(), $"{member.DisplayName} <{email}>", now, new { domain, role = member.Role });
        await db.SaveChangesAsync(ct);
        await NotifyAdminsOfJoinAsync(member, ct);
        return member;
    }

    /// <summary>"Invitation accepted" notification to the organisation's admins, if enabled.</summary>
    private async Task NotifyAdminsOfJoinAsync(OrganizationMember joined, CancellationToken ct)
    {
        var org = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == joined.OrganizationId, ct);
        if (!org.NotificationSettings.GetValueOrDefault("invite_accept",
                OrgSettingsEndpoints.NotificationDefaults["invite_accept"])) return;

        var admins = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == org.Id && m.Role == Roles.OrgAdmin && m.Status == MemberStatus.Active && m.Id != joined.Id)
            .ToListAsync(ct);
        foreach (var admin in admins)
        {
            var who = System.Net.WebUtility.HtmlEncode($"{joined.DisplayName} ({joined.Email})");
            var role = System.Net.WebUtility.HtmlEncode(RoleLabels.For(joined.Role));
            await mailer.TrySendAsync(new BrevoEmail(admin.Email, admin.DisplayName,
                $"{joined.DisplayName} joined {org.Name} on EquipTrack",
                $"<p>{who} has joined <strong>{System.Net.WebUtility.HtmlEncode(org.Name)}</strong> as <strong>{role}</strong>.</p>",
                $"{joined.DisplayName} ({joined.Email}) has joined {org.Name} as {RoleLabels.For(joined.Role)}."), ct);
        }
    }

    public static IResult Problem(MemberResolution result) => result switch
    {
        MemberResolution.Unauthenticated => Results.Unauthorized(),
        MemberResolution.EmailNotVerified => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Verify your email address to continue."),
        MemberResolution.NoMembership => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Your account has not been added to an organisation yet. Ask your administrator for an invitation."),
        MemberResolution.AmbiguousOrganization => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest, title: $"You belong to several organisations. Send the {OrganizationHeader} header."),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}

internal static class RoleSets
{
    /// <summary>Roles that may be held inside an organisation (assignable by invitation).</summary>
    public static bool IsOrgRole(string role) => Roles.IsValid(role) && role != Roles.Sysadmin;

    /// <summary>Roles that see every work order in their organisation (oversight).</summary>
    public static readonly string[] SeeAllOrders =
    [
        Roles.OrgAdmin, Roles.WarehouseSupervisor, Roles.DispatchSupervisor, Roles.Qaqc,
        Roles.Executive, Roles.Logistics, Roles.Safety, Roles.Inventory,
    ];

    /// <summary>Roles that may list members (to pick assignees).</summary>
    public static readonly string[] CanListMembers =
    [
        Roles.OrgAdmin, Roles.WarehouseSupervisor, Roles.DispatchSupervisor, Roles.Executive, Roles.Qaqc,
    ];
}
