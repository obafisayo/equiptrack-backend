namespace EquipTrack.Api.Organizations;

/// <summary>A customer (tenant). All operational data is scoped by organisation.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>URL-safe identifier, unique, e.g. "acme-energy".</summary>
    public required string Slug { get; set; }
    /// <summary>Last work order sequence number issued (for DEL-yy-NNNNN references).</summary>
    public int LastWorkOrderNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum MemberStatus { Invited, Active, Suspended }

/// <summary>
/// Links a Neon Auth user to an organisation with ONE canonical role.
/// This table — not the token — is the source of truth for authorisation.
/// Created as an invitation (by email); bound to a user id the first time
/// that person signs in with the same, VERIFIED email address.
/// </summary>
public class OrganizationMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Lower-cased email the invitation was sent to.</summary>
    public required string Email { get; set; }
    /// <summary>Neon Auth user id (JWT sub); null until the invitation is claimed.</summary>
    public string? UserId { get; set; }

    public required string DisplayName { get; set; }
    /// <summary>Canonical role code (see Domain/Roles.cs). Never "sysadmin".</summary>
    public required string Role { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.Invited;

    public string? InvitedByUserId { get; set; }
    public DateTimeOffset InvitedAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
}
