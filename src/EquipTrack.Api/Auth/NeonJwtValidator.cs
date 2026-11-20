using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace EquipTrack.Api.Auth;

/// <summary>Identity proven by a valid Neon Auth token. Says WHO, never what they may do.</summary>
public sealed record AuthenticatedUser(string UserId, string Email, bool EmailVerified, string? Name);

public sealed record TokenValidationResult(AuthenticatedUser? User, string? Failure)
{
    public static TokenValidationResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// Verifies Neon Auth JWTs: EdDSA (Ed25519) signature against the published JWKS,
/// plus iss, aud, exp and nbf. Microsoft.IdentityModel does not support EdDSA,
/// so the checks are done explicitly here (and unit-tested). Fails closed.
/// </summary>
public sealed class NeonJwtValidator(IJwksProvider jwks, IOptions<NeonAuthOptions> options, TimeProvider clock)
{
    private const int MaxTokenLength = 8 * 1024;

    public async Task<TokenValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (token.Length is 0 or > MaxTokenLength) return TokenValidationResult.Fail("Malformed token.");

        var parts = token.Split('.');
        if (parts.Length != 3) return TokenValidationResult.Fail("Malformed token.");

        if (Base64Url.Decode(parts[0]) is not { } headerBytes
            || Base64Url.Decode(parts[1]) is not { } payloadBytes
            || Base64Url.Decode(parts[2]) is not { Length: 64 } signature)
            return TokenValidationResult.Fail("Malformed token.");

        JsonElement header, payload;
        try
        {
            header = JsonDocument.Parse(headerBytes).RootElement;
            payload = JsonDocument.Parse(payloadBytes).RootElement;
        }
        catch (JsonException)
        {
            return TokenValidationResult.Fail("Malformed token.");
        }

        if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object)
            return TokenValidationResult.Fail("Malformed token.");

        // Only EdDSA is accepted: blocks "alg":"none" and algorithm-confusion attacks.
        if (header.GetStringOrNull("alg") != "EdDSA") return TokenValidationResult.Fail("Unsupported algorithm.");
        if (header.GetStringOrNull("kid") is not { Length: > 0 } kid) return TokenValidationResult.Fail("Missing key id.");

        if (await jwks.GetEd25519KeyAsync(kid, ct) is not { } publicKey)
            return TokenValidationResult.Fail("Unknown signing key.");

        if (!VerifyEd25519(publicKey, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature))
            return TokenValidationResult.Fail("Invalid signature.");

        var opts = options.Value;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var skew = opts.ClockSkewSeconds;

        if (!TryGetLong(payload, "exp", out var exp)) return TokenValidationResult.Fail("Missing expiry.");
        if (now - skew >= exp) return TokenValidationResult.Fail("Token expired.");
        if (TryGetLong(payload, "nbf", out var nbf) && now + skew < nbf) return TokenValidationResult.Fail("Token not yet valid.");

        if (!string.Equals(payload.GetStringOrNull("iss"), opts.ResolvedIssuer, StringComparison.Ordinal))
            return TokenValidationResult.Fail("Wrong issuer.");
        if (!AudienceMatches(payload, opts.ResolvedAudience)) return TokenValidationResult.Fail("Wrong audience.");

        if (payload.GetStringOrNull("sub") is not { Length: > 0 } sub) return TokenValidationResult.Fail("Missing subject.");
        if (payload.TryGetProperty("banned", out var banned) && banned.ValueKind == JsonValueKind.True)
            return TokenValidationResult.Fail("User is banned.");

        var email = payload.GetStringOrNull("email") ?? string.Empty;
        var verified = payload.TryGetProperty("emailVerified", out var ev) && ev.ValueKind == JsonValueKind.True;

        return new TokenValidationResult(new AuthenticatedUser(sub, email, verified, payload.GetStringOrNull("name")), null);
    }

    private static bool VerifyEd25519(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetLong(JsonElement e, string name, out long value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }

    private static bool AudienceMatches(JsonElement payload, string expected)
    {
        if (!payload.TryGetProperty("aud", out var aud)) return false;
        return aud.ValueKind switch
        {
            JsonValueKind.String => string.Equals(aud.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Array => aud.EnumerateArray().Any(a =>
                a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), expected, StringComparison.Ordinal)),
            _ => false,
        };
    }
}
