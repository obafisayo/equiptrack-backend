using System.Text.RegularExpressions;
using EquipTrack.Api.Domain;

namespace EquipTrack.Api.Tests;

/// <summary>
/// Guards against the frontend and backend role models drifting apart
/// (the bug that produced both `wh_sup` and `wh_supervisor`).
///
/// The frontend lives in a separate repository (equiptrack-solution). Its
/// canonical list is <c>src/lib/domain/roles.ts</c>, located via:
///   1. the <c>FRONTEND_REPO_PATH</c> environment variable, or
///   2. a sibling checkout at <c>../equiptrack-solution</c>.
/// If neither exists the test passes with a notice, unless
/// <c>REQUIRE_ROLE_CONTRACT=true</c> (set in CI), in which case it fails.
/// </summary>
public sealed partial class RoleContractTests
{
    [Fact]
    public void Backend_roles_match_frontend_canonical_roles()
    {
        var rolesTs = FindFrontendRolesFile();
        if (rolesTs is null)
        {
            var required = string.Equals(Environment.GetEnvironmentVariable("REQUIRE_ROLE_CONTRACT"), "true",
                StringComparison.OrdinalIgnoreCase);
            Assert.False(required,
                "Frontend roles.ts not found. Set FRONTEND_REPO_PATH to an equiptrack-solution checkout.");
            return;
        }

        var source = File.ReadAllText(rolesTs);
        var block = RolesArray().Match(source);
        Assert.True(block.Success, $"Could not find `export const ROLES = [...] as const` in {rolesTs}");

        var frontend = QuotedValue().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(frontend, Roles.All);
    }

    private static string? FindFrontendRolesFile()
    {
        var candidates = new List<string>();

        var fromEnv = Environment.GetEnvironmentVariable("FRONTEND_REPO_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

        // Walk up from the test binaries to the backend repo root, then look for a sibling checkout.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EquipTrack.sln")))
            dir = dir.Parent;
        if (dir?.Parent is not null) candidates.Add(Path.Combine(dir.Parent.FullName, "equiptrack-solution"));

        return candidates
            .Select(root => Path.Combine(root, "src", "lib", "domain", "roles.ts"))
            .FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"export const ROLES = \[(.*?)\] as const", RegexOptions.Singleline)]
    private static partial Regex RolesArray();

    [GeneratedRegex(@"'([a-z_]+)'")]
    private static partial Regex QuotedValue();
}
