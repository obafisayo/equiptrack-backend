namespace EquipTrack.Api.Organizations;

public sealed record PlanDefinition(PlanTier Tier, string Name, decimal? MonthlyPriceUsd, int? Seats, IReadOnlyList<string> Features);

/// <summary>
/// Plan catalogue. Prices and seat limits MUST match src/config/pricing.ts in the
/// frontend (a contract test enforces this). Feature keys match PlanFeature in
/// the frontend's types.ts.
/// </summary>
public static class Plans
{
    public static readonly IReadOnlyList<PlanDefinition> All =
    [
        new(PlanTier.Starter, "Starter", 249m, 10, ["audit_log"]),
        new(PlanTier.Professional, "Professional", 699m, 50,
            ["audit_log", "advanced_analytics", "custom_sla", "bulk_import", "priority_support"]),
        new(PlanTier.Enterprise, "Enterprise", null, null,
            ["audit_log", "advanced_analytics", "custom_sla", "bulk_import", "priority_support",
             "sso_microsoft", "api_access", "webhooks", "white_label"]),
    ];

    public static PlanDefinition For(PlanTier tier) => All.First(p => p.Tier == tier);

    public static string ToApi(this PlanTier tier) => tier.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out PlanTier tier) =>
        Enum.TryParse(value, ignoreCase: true, out tier) && Enum.IsDefined(tier);

    public static string ToApi(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.PastDue => "past_due",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string ToApi(this OrganizationStatus status) => status switch
    {
        OrganizationStatus.DeletionRequested => "deletion_requested",
        _ => status.ToString().ToLowerInvariant(),
    };
}
