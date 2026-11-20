namespace EquipTrack.Api.Organizations;

public enum OrganizationStatus { Onboarding, Active, Suspended, Churned, DeletionRequested }

public enum PlanTier { Starter, Professional, Enterprise }

public enum SubscriptionStatus { Trialing, Active, PastDue, Cancelled, Suspended }

/// <summary>A customer (tenant). All operational data is scoped by organisation.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>URL-safe identifier, unique, e.g. "acme-energy". Not editable after creation.</summary>
    public required string Slug { get; set; }
    /// <summary>Last work order sequence number issued (for DEL-yy-NNNNN references).</summary>
    public int LastWorkOrderNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // ── Profile ──────────────────────────────────────────────────────────────
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public string Timezone { get; set; } = "Africa/Lagos";
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Onboarding;
    public DateTimeOffset? DeletionRequestedAt { get; set; }

    // ── Document branding (TR / waybill) ─────────────────────────────────────
    public string? LogoUrl { get; set; }
    /// <summary>Wordmark and approval-column name on documents. Defaults to <see cref="Name"/>.</summary>
    public string? DocumentShortName { get; set; }
    /// <summary>Registered legal entity printed on documents.</summary>
    public string? DocumentLegalName { get; set; }
    public string AccentColor { get; set; } = "#131722";
    /// <summary>Default "from" location on waybills, e.g. ONNE BASE.</summary>
    public string? DefaultOrigin { get; set; }
    /// <summary>Logistics base named on TR approval lines, e.g. Onne Base.</summary>
    public string? BaseName { get; set; }
    /// <summary>Coordinators shown on TR approval lines (1–2 names).</summary>
    public List<string> Coordinators { get; set; } = [];

    // ── Operations ───────────────────────────────────────────────────────────
    /// <summary>Per-organisation stage SLA hours (stage display name → hours). Missing stages use the defaults.</summary>
    public Dictionary<string, double> SlaHoursOverrides { get; set; } = new();
    /// <summary>Notification preferences (key → enabled), e.g. sla_breach, new_request.</summary>
    public Dictionary<string, bool> NotificationSettings { get; set; } = new();

    // ── Subscription ─────────────────────────────────────────────────────────
    public PlanTier PlanTier { get; set; } = PlanTier.Starter;
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trialing;
    /// <summary>Seat limit; null = unlimited (enterprise contracts).</summary>
    public int? Seats { get; set; } = 10;
    /// <summary>Contract monthly price in USD; null = plan list price.</summary>
    public decimal? MonthlyPriceUsd { get; set; }
    public DateOnly? BillingCycleStart { get; set; }
    public DateOnly? BillingCycleEnd { get; set; }
    public DateOnly? TrialEndsAt { get; set; }

    // ── Single sign-on / domain auto-join ────────────────────────────────────
    /// <summary>Microsoft Entra tenant ID used by this organisation (informational; sign-in is via Neon Auth).</summary>
    public string? MicrosoftTenantId { get; set; }
    /// <summary>Email domains that belong to this organisation, e.g. "acme.com".</summary>
    public List<string> AllowedEmailDomains { get; set; } = [];
    /// <summary>When true, anyone signing in with a VERIFIED email at an allowed domain joins automatically.</summary>
    public bool AutoProvision { get; set; }
    /// <summary>Role given to auto-provisioned members.</summary>
    public string AutoProvisionRole { get; set; } = "requester";
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
    public string? Department { get; set; }

    public string? InvitedByUserId { get; set; }
    public DateTimeOffset InvitedAt { get; set; }
    /// <summary>Invitations expire; an expired invitation can be resent.</summary>
    public DateTimeOffset? InviteExpiresAt { get; set; }
    public DateTimeOffset? InviteEmailSentAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? LastActiveAt { get; set; }
}

/// <summary>
/// Administrative audit trail for an organisation (membership, settings, SSO,
/// subscription). Work order history lives in work_order_events and is merged
/// into the audit view. Append-only.
/// </summary>
public class OrgAuditEvent
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string ActorUserId { get; set; }
    public required string ActorName { get; set; }
    public required string ActorRole { get; set; }
    /// <summary>Dotted action name, e.g. member.invited, sso.updated.</summary>
    public required string Action { get; set; }
    /// <summary>user | org | sso | subscription | workorder</summary>
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string TargetLabel { get; set; }
    /// <summary>JSON object with action-specific details.</summary>
    public string Metadata { get; set; } = "{}";
}
