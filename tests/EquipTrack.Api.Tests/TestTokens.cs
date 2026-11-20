using System.Text;
using System.Text.Json;
using EquipTrack.Api.Auth;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EquipTrack.Api.Tests;

/// <summary>
/// Issues Neon-Auth-shaped EdDSA tokens with a test key pair, and serves the
/// matching public key as a fake JWKS. Lets tests exercise the real validator.
/// </summary>
public sealed class TestTokens : IJwksProvider
{
    public const string BaseUrl = "https://auth.test.local/equiptrack/auth";
    public const string Origin = "https://auth.test.local";
    public const string KeyId = "test-key-1";

    private readonly Ed25519PrivateKeyParameters _privateKey;
    public byte[] PublicKey { get; }

    public TestTokens()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        PublicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
    }

    public Task<byte[]?> GetEd25519KeyAsync(string kid, CancellationToken ct) =>
        Task.FromResult(kid == KeyId ? PublicKey : null);

    public string Issue(
        string sub,
        string email,
        bool emailVerified = true,
        string? name = null,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null,
        string alg = "EdDSA",
        string kid = KeyId,
        string iss = Origin,
        object? aud = null,
        bool banned = false,
        Action<Dictionary<string, object?>>? tweak = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object?> { ["alg"] = alg, ["kid"] = kid, ["typ"] = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = sub,
            ["id"] = sub,
            ["email"] = email,
            ["emailVerified"] = emailVerified,
            ["name"] = name ?? email,
            ["role"] = "authenticated",
            ["banned"] = banned,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = (issuedAt + (lifetime ?? TimeSpan.FromMinutes(15))).ToUnixTimeSeconds(),
            ["iss"] = iss,
            ["aud"] = aud ?? Origin,
        };
        tweak?.Invoke(payload);

        var signingInput = $"{B64(JsonSerializer.SerializeToUtf8Bytes(header))}.{B64(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        var bytes = Encoding.ASCII.GetBytes(signingInput);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return $"{signingInput}.{B64(signer.GenerateSignature())}";
    }

    public static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
