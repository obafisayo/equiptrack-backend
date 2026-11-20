using Microsoft.Extensions.Options;

namespace EquipTrack.Api.Email;

/// <summary>Public app settings used in emails (links back to the frontend).</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Frontend origin, e.g. https://equiptracks.netlify.app</summary>
    public string PublicUrl { get; set; } = "http://localhost:3000";
}

public interface IAppMailer
{
    /// <summary>Sends now; never throws. Returns false when not sent (not configured or provider error).</summary>
    Task<bool> TrySendAsync(BrevoEmail email, CancellationToken ct);

    bool IsConfigured { get; }
    string TeamNotificationEmail { get; }
}

public sealed class AppMailer(IBrevoClient brevo, IOptions<BrevoOptions> options, ILogger<AppMailer> logger) : IAppMailer
{
    public bool IsConfigured => options.Value.IsConfigured;
    public string TeamNotificationEmail => options.Value.TeamNotificationEmail;

    public async Task<bool> TrySendAsync(BrevoEmail email, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Email not sent ({Subject}): Brevo is not configured.", email.Subject);
            return false;
        }
        try
        {
            await brevo.SendEmailAsync(email, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email not sent ({Subject}) to {To}", email.Subject, email.ToEmail);
            return false;
        }
    }
}
