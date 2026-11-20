using System.Globalization;
using System.Text.RegularExpressions;
using EquipTrack.Api.Domain;
using EquipTrack.Api.WorkOrders;

namespace EquipTrack.Api.Tests;

/// <summary>
/// Guards against the frontend and backend drifting apart (the bug that once
/// produced both `wh_sup` and `wh_supervisor`). Compares the backend's roles,
/// lifecycle stages and SLA hours with the frontend's source files.
///
/// The frontend lives in a separate repository (equiptrack-solution), located via:
///   1. the <c>FRONTEND_REPO_PATH</c> environment variable, or
///   2. a sibling checkout at <c>../equiptrack-solution</c>.
/// If neither exists the tests pass with a notice, unless
/// <c>REQUIRE_ROLE_CONTRACT=true</c> (set in CI), in which case they fail.
/// </summary>
public sealed partial class FrontendContractTests
{
    [Fact]
    public void Backend_roles_match_frontend_canonical_roles()
    {
        if (ReadFrontendFile("src/lib/domain/roles.ts") is not { } source) return;

        var block = RolesArray().Match(source);
        Assert.True(block.Success, "Could not find `export const ROLES = [...] as const` in roles.ts");

        var frontend = QuotedValue().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(frontend, Roles.All);
    }

    [Fact]
    public void Backend_stages_match_frontend_lifecycle_in_order()
    {
        if (ReadFrontendFile("src/lib/lifecycle.ts") is not { } source) return;

        var block = LifecycleArray().Match(source);
        Assert.True(block.Success, "Could not find `export const LIFECYCLE = [...] as const` in lifecycle.ts");

        var frontend = AnyQuoted().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(frontend, Stages.Ordered.Select(s => s.Name()));
    }

    [Fact]
    public void Backend_stage_sla_hours_match_frontend_config()
    {
        if (ReadFrontendFile("src/config/sla.ts") is not { } source) return;

        var block = SlaBlock().Match(source);
        Assert.True(block.Success, "Could not find STAGE_SLA_HOURS in sla.ts");

        var frontend = SlaEntry().Matches(block.Groups[1].Value).ToDictionary(
            m => m.Groups[1].Value,
            m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        var backend = Sla.StageHours.ToDictionary(kv => kv.Key.Name(), kv => kv.Value);

        Assert.Equal(frontend.OrderBy(kv => kv.Key), backend.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Backend_plans_match_frontend_pricing()
    {
        if (ReadFrontendFile("src/config/pricing.ts") is not { } source) return;

        var frontend = PlanBlock().Matches(source).ToDictionary(
            m => m.Groups[1].Value,
            m => (Price: m.Groups[2].Value == "null" ? (decimal?)null : decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                  Seats: m.Groups[3].Value == "null" ? (int?)null : int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
        Assert.Equal(3, frontend.Count);

        foreach (var plan in EquipTrack.Api.Organizations.Plans.All)
        {
            var key = plan.Tier.ToString().ToLowerInvariant();
            Assert.True(frontend.ContainsKey(key), $"pricing.ts has no '{key}' plan");
            Assert.Equal(frontend[key].Price, plan.MonthlyPriceUsd);
            Assert.Equal(frontend[key].Seats, plan.Seats);
        }
    }

    private static string? ReadFrontendFile(string relativePath)
    {
        var root = FindFrontendRoot();
        var path = root is null ? null : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (path is not null && File.Exists(path)) return File.ReadAllText(path);

        var required = string.Equals(Environment.GetEnvironmentVariable("REQUIRE_ROLE_CONTRACT"), "true",
            StringComparison.OrdinalIgnoreCase);
        Assert.False(required, $"Frontend file {relativePath} not found. Set FRONTEND_REPO_PATH to an equiptrack-solution checkout.");
        return null;
    }

    private static string? FindFrontendRoot()
    {
        var candidates = new List<string>();

        var fromEnv = Environment.GetEnvironmentVariable("FRONTEND_REPO_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

        // Walk up from the test binaries to the backend repo root, then look for a sibling checkout.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EquipTrack.sln")))
            dir = dir.Parent;
        if (dir?.Parent is not null) candidates.Add(Path.Combine(dir.Parent.FullName, "equiptrack-solution"));

        return candidates.FirstOrDefault(Directory.Exists);
    }

    [GeneratedRegex(@"export const ROLES = \[(.*?)\] as const", RegexOptions.Singleline)]
    private static partial Regex RolesArray();

    [GeneratedRegex(@"'([a-z_]+)'")]
    private static partial Regex QuotedValue();

    [GeneratedRegex(@"export const LIFECYCLE = \[(.*?)\] as const", RegexOptions.Singleline)]
    private static partial Regex LifecycleArray();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex AnyQuoted();

    [GeneratedRegex(@"export const STAGE_SLA_HOURS[^{]*\{(.*?)\}", RegexOptions.Singleline)]
    private static partial Regex SlaBlock();

    [GeneratedRegex(@"tier:\s*'(\w+)'[\s\S]*?monthlyPriceUsd:\s*(null|\d+)[\s\S]*?seats:\s*(null|\d+)")]
    private static partial Regex PlanBlock();

    [GeneratedRegex(@"'([^']+)'\s*:\s*([0-9.]+)")]
    private static partial Regex SlaEntry();
}
