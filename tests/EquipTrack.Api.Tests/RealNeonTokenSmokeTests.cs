using EquipTrack.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EquipTrack.Api.Tests;

/// <summary>
/// Optional smoke test against a REAL Neon Auth deployment. Runs only when
/// NEON_SMOKE_BASE_URL and NEON_SMOKE_TOKEN are set (tokens expire in 15 minutes,
/// so this never runs in CI). Proves the validator accepts genuine Neon tokens
/// with the live JWKS, issuer and audience.
/// </summary>
public sealed class RealNeonTokenSmokeTests
{
    [Fact]
    public async Task Validator_accepts_a_genuine_neon_auth_token()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NEON_SMOKE_BASE_URL");
        var token = Environment.GetEnvironmentVariable("NEON_SMOKE_TOKEN");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token)) return;

        var options = Options.Create(new NeonAuthOptions { BaseUrl = baseUrl });
        using var http = new HttpClient();
        var jwks = new HttpJwksProvider(http, options, TimeProvider.System, NullLogger<HttpJwksProvider>.Instance);
        var validator = new NeonJwtValidator(jwks, options, TimeProvider.System);

        var result = await validator.ValidateAsync(token.Trim(), CancellationToken.None);

        Assert.True(result.Failure is null, result.Failure);
        Assert.False(string.IsNullOrEmpty(result.User!.UserId));

        // Tampering with one character of the signature must break it.
        var tampered = token.Trim()[..^2] + (token.Trim()[^2] == 'A' ? "B" : "A") + token.Trim()[^1];
        Assert.NotNull((await validator.ValidateAsync(tampered, CancellationToken.None)).Failure);
    }
}
