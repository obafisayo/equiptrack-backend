using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EquipTrack.Api.Auth;

/// <summary>ASP.NET Core authentication scheme for "Authorization: Bearer &lt;Neon Auth JWT&gt;".</summary>
public sealed class NeonAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NeonJwtValidator validator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NeonAuth";

    public const string EmailVerifiedClaim = "email_verified";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var result = await validator.ValidateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (result.User is not { } user)
        {
            Logger.LogInformation("Rejected bearer token: {Reason}", result.Failure);
            return AuthenticateResult.Fail(result.Failure ?? "Invalid token.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Email, user.Email),
            new(EmailVerifiedClaim, user.EmailVerified ? "true" : "false"),
        };
        if (!string.IsNullOrWhiteSpace(user.Name)) claims.Add(new Claim(ClaimTypes.Name, user.Name));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
