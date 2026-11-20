using System.Security.Claims;
using EquipTrack.Api.Data;
using EquipTrack.Api.Domain;
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
public sealed class CurrentMemberResolver(AppDbContext db, IOptions<NeonAuthOptions> options, TimeProvider clock)
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

        return (new MemberContext(member.OrganizationId, member.Id, userId, member.DisplayName, member.Email, member.Role),
            MemberResolution.Ok);
    }

    /// <summary>
    /// Binds pending invitations to this user. Only with a VERIFIED email —
    /// otherwise anyone could sign up with someone else's address and take their role.
    /// </summary>
    private async Task ClaimInvitationsAsync(ClaimsPrincipal user, string userId, CancellationToken ct)
    {
        if (!EmailVerified(user)) return;
        var email = Email(user);
        if (email.Length == 0) return;

        var invitations = await db.OrganizationMembers
            .Where(m => m.Email == email && m.UserId == null && m.Status == MemberStatus.Invited)
            .ToListAsync(ct);
        if (invitations.Count == 0) return;

        var now = clock.GetUtcNow();
        foreach (var inv in invitations)
        {
            inv.UserId = userId;
            inv.Status = MemberStatus.Active;
            inv.JoinedAt = now;
        }
        await db.SaveChangesAsync(ct);
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
