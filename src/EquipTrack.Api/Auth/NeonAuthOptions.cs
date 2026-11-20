namespace EquipTrack.Api.Auth;

/// <summary>
/// Neon Auth (Managed Better Auth) settings. Bound from the "NeonAuth" section.
/// Only <see cref="BaseUrl"/> is required; the JWKS URL, issuer and audience
/// are derived from it exactly as Neon documents them.
/// </summary>
public sealed class NeonAuthOptions
{
    public const string SectionName = "NeonAuth";

    /// <summary>e.g. https://ep-xxx.neonauth.c-7.eu-central-1.aws.neon.tech/equiptrack/auth</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Defaults to {BaseUrl}/.well-known/jwks.json.</summary>
    public string? JwksUrl { get; set; }

    /// <summary>Neon Auth sets iss and aud to the ORIGIN of the Auth URL (scheme + host).</summary>
    public string? Issuer { get; set; }
    public string? Audience { get; set; }

    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>How long fetched signing keys are trusted before refreshing.</summary>
    public int JwksCacheMinutes { get; set; } = 60;

    /// <summary>
    /// Verified email addresses that are platform administrators (can create
    /// organisations and their first org admin). Bootstrap only.
    /// </summary>
    public string[] SysadminEmails { get; set; } = [];

    public bool IsConfigured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);

    public string ResolvedJwksUrl => JwksUrl ?? $"{BaseUrl.TrimEnd('/')}/.well-known/jwks.json";

    private string Origin => new Uri(BaseUrl).GetLeftPart(UriPartial.Authority);
    public string ResolvedIssuer => Issuer ?? Origin;
    public string ResolvedAudience => Audience ?? Origin;
}
