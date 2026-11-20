using System.Net;

namespace EquipTrack.Api.Email;

/// <summary>Invitation email: names the inviter, the organisation and the role.</summary>
internal static class InviteEmails
{
    public static BrevoEmail Invitation(
        string toEmail, string toName, string organizationName, string roleLabel, string inviterName,
        string signUpUrl, DateTimeOffset expiresAt)
    {
        var subject = $"{inviterName} invited you to {organizationName} on EquipTrack";
        var expires = expiresAt.ToString("d MMMM yyyy");

        var text =
            $"Hi {toName},\n\n" +
            $"{inviterName} has invited you to join {organizationName} on EquipTrack as {roleLabel}.\n\n" +
            $"Create your account with this email address ({toEmail}):\n{signUpUrl}\n\n" +
            $"After you verify your email, you will have access straight away. This invitation expires on {expires}.\n\n" +
            "If you were not expecting this, you can ignore this email.\n\n" +
            "The EquipTrack team";

        var html =
            $"<p>Hi {E(toName)},</p>" +
            $"<p><strong>{E(inviterName)}</strong> has invited you to join <strong>{E(organizationName)}</strong> on EquipTrack as <strong>{E(roleLabel)}</strong>.</p>" +
            $"<p><a href=\"{E(signUpUrl)}\">Create your account</a> with this email address ({E(toEmail)}).</p>" +
            $"<p>After you verify your email, you will have access straight away. This invitation expires on {E(expires)}.</p>" +
            "<p>If you were not expecting this, you can ignore this email.</p>" +
            "<p>The EquipTrack team</p>";

        return new BrevoEmail(toEmail, toName, subject, html, text);
    }

    public static BrevoEmail PlanChangeRequest(
        string teamEmail, string organizationName, string organizationSlug, string currentPlan, string requestedPlan,
        string requesterName, string requesterEmail, string? message)
    {
        var subject = $"Plan change request: {organizationName} → {requestedPlan}";
        var text =
            $"Organisation: {organizationName} ({organizationSlug})\n" +
            $"Current plan: {currentPlan}\nRequested plan: {requestedPlan}\n" +
            $"Requested by: {requesterName} <{requesterEmail}>\n\n{message ?? "-"}";
        var html =
            $"<p><strong>Organisation:</strong> {E(organizationName)} ({E(organizationSlug)})</p>" +
            $"<p><strong>Current plan:</strong> {E(currentPlan)}<br/><strong>Requested plan:</strong> {E(requestedPlan)}</p>" +
            $"<p><strong>Requested by:</strong> {E(requesterName)} &lt;{E(requesterEmail)}&gt;</p>" +
            $"<p>{E(message ?? "-")}</p>";
        return new BrevoEmail(teamEmail, "EquipTrack team", subject, html, text);
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
