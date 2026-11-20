using System.Text;
using EquipTrack.Api.Auth;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EquipTrack.Api.Tests;

public sealed class NeonJwtValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private readonly TestTokens _tokens = new();
    private readonly NeonJwtValidator _validator;

    public NeonJwtValidatorTests()
    {
        _validator = new NeonJwtValidator(_tokens,
            Options.Create(new NeonAuthOptions { BaseUrl = TestTokens.BaseUrl, ClockSkewSeconds = 60 }),
            new FakeTimeProvider(Now));
    }

    private Task<TokenValidationResult> Validate(string token) => _validator.ValidateAsync(token, CancellationToken.None);

    [Fact]
    public async Task Valid_token_yields_identity()
    {
        var result = await Validate(_tokens.Issue("user-1", "ada@acme.example", name: "Ada Obi", now: Now));
        Assert.Null(result.Failure);
        Assert.Equal("user-1", result.User!.UserId);
        Assert.Equal("ada@acme.example", result.User.Email);
        Assert.True(result.User.EmailVerified);
        Assert.Equal("Ada Obi", result.User.Name);
    }

    [Fact]
    public async Task Issuer_and_audience_default_to_the_auth_url_origin()
    {
        var opts = new NeonAuthOptions { BaseUrl = TestTokens.BaseUrl };
        Assert.Equal(TestTokens.Origin, opts.ResolvedIssuer);
        Assert.Equal(TestTokens.Origin, opts.ResolvedAudience);
        Assert.Equal(TestTokens.BaseUrl + "/.well-known/jwks.json", opts.ResolvedJwksUrl);
        Assert.Null((await Validate(_tokens.Issue("u", "a@b.co", now: Now, aud: new[] { "other", TestTokens.Origin }))).Failure);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256")]
    public async Task Other_algorithms_are_rejected(string alg) =>
        Assert.Equal("Unsupported algorithm.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, alg: alg))).Failure);

    [Fact]
    public async Task Tampered_payload_fails_signature()
    {
        var parts = _tokens.Issue("user-1", "ada@acme.example", now: Now).Split('.');
        var forged = TestTokens.B64(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace("user-1", "admin-1")));
        Assert.Equal("Invalid signature.", (await Validate($"{parts[0]}.{forged}.{parts[2]}")).Failure);
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_rejected()
    {
        var attacker = new TestTokens(); // same kid, different private key
        Assert.Equal("Invalid signature.", (await Validate(attacker.Issue("u", "a@b.co", now: Now))).Failure);
    }

    [Fact]
    public async Task Unknown_key_id_is_rejected() =>
        Assert.Equal("Unknown signing key.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, kid: "rotated-away"))).Failure);

    [Fact]
    public async Task Expired_token_is_rejected_beyond_clock_skew()
    {
        // Expired 30 seconds ago: tolerated by the 60-second clock skew.
        var token = _tokens.Issue("u", "a@b.co", now: Now.AddSeconds(-930), lifetime: TimeSpan.FromMinutes(15));
        Assert.Null((await Validate(token)).Failure);
        var older = _tokens.Issue("u", "a@b.co", now: Now.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(15));
        Assert.Equal("Token expired.", (await Validate(older)).Failure);
    }

    [Fact]
    public async Task Not_yet_valid_token_is_rejected() =>
        Assert.Equal("Token not yet valid.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now,
            tweak: p => p["nbf"] = Now.AddMinutes(10).ToUnixTimeSeconds()))).Failure);

    [Fact]
    public async Task Wrong_issuer_or_audience_is_rejected()
    {
        Assert.Equal("Wrong issuer.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, iss: "https://evil.example"))).Failure);
        Assert.Equal("Wrong audience.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, aud: "https://evil.example"))).Failure);
    }

    [Fact]
    public async Task Missing_expiry_or_subject_is_rejected()
    {
        Assert.Equal("Missing expiry.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, tweak: p => p.Remove("exp")))).Failure);
        Assert.Equal("Missing subject.", (await Validate(_tokens.Issue("", "a@b.co", now: Now))).Failure);
    }

    [Fact]
    public async Task Banned_user_is_rejected() =>
        Assert.Equal("User is banned.", (await Validate(_tokens.Issue("u", "a@b.co", now: Now, banned: true))).Failure);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("!!.@@.##")]
    public async Task Garbage_is_rejected(string token) =>
        Assert.NotNull((await Validate(token)).Failure);

    private static string Pad(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
