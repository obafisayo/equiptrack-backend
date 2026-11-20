using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace EquipTrack.Api.Auth;

/// <summary>Supplies the Ed25519 public keys (by key id) that Neon Auth signs tokens with.</summary>
public interface IJwksProvider
{
    /// <returns>The 32-byte Ed25519 public key, or null if no such key exists.</returns>
    Task<byte[]?> GetEd25519KeyAsync(string kid, CancellationToken ct);
}

/// <summary>
/// Fetches and caches the JWKS. Refreshes on expiry, or early when a token
/// names an unknown key id (key rotation) — at most once per <see cref="MinRefreshInterval"/>
/// so forged kids cannot be used to hammer the auth server.
/// </summary>
public sealed class HttpJwksProvider(
    HttpClient http,
    IOptions<NeonAuthOptions> options,
    TimeProvider clock,
    ILogger<HttpJwksProvider> logger) : IJwksProvider
{
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<byte[]?> GetEd25519KeyAsync(string kid, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ttl = TimeSpan.FromMinutes(Math.Max(1, options.Value.JwksCacheMinutes));

        if (now - _fetchedAt < ttl && _keys.TryGetValue(kid, out var cached)) return cached;
        if (now - _fetchedAt < MinRefreshInterval) return _keys.GetValueOrDefault(kid);

        await _lock.WaitAsync(ct);
        try
        {
            // Another request may have refreshed while we waited.
            if (clock.GetUtcNow() - _fetchedAt >= MinRefreshInterval) await RefreshAsync(ct);
            return _keys.GetValueOrDefault(kid);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JwksDocument>(options.Value.ResolvedJwksUrl, ct)
                      ?? throw new InvalidOperationException("Empty JWKS response.");

            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var k in doc.Keys ?? [])
            {
                if (k is { Kty: "OKP", Crv: "Ed25519", Kid: { } id, X: { } x } && (k.Alg is null or "EdDSA"))
                {
                    var bytes = Base64Url.Decode(x);
                    if (bytes is { Length: 32 }) keys[id] = bytes;
                }
            }

            _keys = keys;
            _fetchedAt = clock.GetUtcNow();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep serving previously cached keys; back off before retrying.
            _fetchedAt = clock.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, options.Value.JwksCacheMinutes)) + MinRefreshInterval;
            logger.LogError(ex, "Failed to refresh Neon Auth JWKS from {Url}", options.Value.ResolvedJwksUrl);
        }
    }

    private sealed class JwksDocument
    {
        [JsonPropertyName("keys")] public List<Jwk>? Keys { get; set; }
    }

    private sealed class Jwk
    {
        [JsonPropertyName("kty")] public string? Kty { get; set; }
        [JsonPropertyName("crv")] public string? Crv { get; set; }
        [JsonPropertyName("alg")] public string? Alg { get; set; }
        [JsonPropertyName("kid")] public string? Kid { get; set; }
        [JsonPropertyName("x")] public string? X { get; set; }
    }
}

internal static class Base64Url
{
    public static byte[]? Decode(string value)
    {
        try
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
