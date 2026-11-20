using System.Text.Json;
using System.Text.RegularExpressions;
using EquipTrack.Api.Auth;
using EquipTrack.Api.Data;
using EquipTrack.Api.Domain;
using EquipTrack.Api.Email;
using EquipTrack.Api.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace EquipTrack.Api.Organizations;

// ── Requests ────────────────────────────────────────────────────────────────

public sealed record UpdateProfileRequest(string? Name, string? Industry, string? Country, string? Timezone, string? LogoUrl);
public sealed record UpdateBrandingRequest(string? ShortName, string? LegalName, string? AccentColor, string? DefaultOrigin, string? BaseName, List<string>? Coordinators);
public sealed record UpdateSlaRequest(Dictionary<string, double>? Hours);
public sealed record UpdateNotificationsRequest(Dictionary<string, bool>? Settings);
public sealed record UpdateSsoRequest(string? TenantId, List<string>? AllowedDomains, bool AutoProvision, string? DefaultRole);
public sealed record PlanChangeRequest(string? Tier, string? Message);
public sealed record DeletionRequest(string? ConfirmSlug);

// ── Responses (shapes match the frontend's types.ts) ────────────────────────

public sealed record SubscriptionDto(string Tier, string Status, int? Seats, int SeatsUsed, decimal? MonthlyPrice,
    DateOnly? BillingCycleStart, DateOnly? BillingCycleEnd, DateOnly? TrialEndsAt, IReadOnlyList<string> Features);

public sealed record SsoDto(string Provider, string? TenantId, string Status, IReadOnlyList<string> AllowedDomains, bool AutoProvision, string DefaultRole);

public sealed record BrandingDto(string ShortName, string LegalName, string? LogoUrl, string AccentColor, string? DefaultOrigin, string? BaseName, IReadOnlyList<string> Coordinators);

public sealed record OrganizationDto(
    Guid Id, string Name, string Slug, string? Industry, string? Country, string Timezone, string Status,
    DateTimeOffset CreatedAt, int MemberCount, int ActiveWorkOrders,
    BrandingDto Branding, IReadOnlyDictionary<string, double> SlaHours, IReadOnlyDictionary<string, double> SlaDefaults,
    IReadOnlyDictionary<string, bool> Notifications, SubscriptionDto Subscription, SsoDto Sso);

public sealed record AuditEventDto(string Id, Guid OrgId, string ActorId, string ActorName, string ActorRole, string Action,
    string TargetType, string TargetId, string TargetLabel, JsonElement Metadata, DateTimeOffset CreatedAt);

public sealed record PlanDto(string Tier, string Name, decimal? MonthlyPrice, int? Seats, IReadOnlyList<string> Features);

public static partial class OrgSettingsEndpoints
{
    /// <summary>Notification keys the organisation can toggle (match the Settings → Notifications tab).</summary>
    public static readonly IReadOnlyDictionary<string, bool> NotificationDefaults = new Dictionary<string, bool>
    {
        ["sla_breach"] = true,
        ["new_request"] = true,
        ["stage_complete"] = false,
        ["invite_accept"] = true,
        ["login_alert"] = false,
        ["billing_due"] = true,
    };

    public static IEndpointRouteBuilder MapOrgSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plans", () => Results.Ok(Plans.All.Select(p => new PlanDto(p.Tier.ToApi(), p.Name, p.MonthlyPriceUsd, p.Seats, p.Features))));

        var org = app.MapGroup("/api/org").RequireAuthorization();
        org.MapGet("/", GetOrganization);
        org.MapPatch("/profile", UpdateProfile);
        org.MapPatch("/branding", UpdateBranding);
        org.MapPut("/sla", UpdateSla);
        org.MapPut("/notifications", UpdateNotifications);
        org.MapPut("/sso", UpdateSso);
        org.MapPost("/subscription/change-request", RequestPlanChange);
        org.MapPost("/deletion-request", RequestDeletion);
        org.MapGet("/audit", GetAudit);
        return app;
    }

    // ── Read ────────────────────────────────────────────────────────────────

    internal static async Task<IResult> GetOrganization(HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);
        var org = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == me.OrganizationId, ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<OrganizationDto> ToDtoAsync(AppDbContext db, Organization org, CancellationToken ct)
    {
        var memberCount = await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org.Id && m.Status == MemberStatus.Active, ct);
        var seatsUsed = await SeatsUsedAsync(db, org.Id, ct);
        var activeOrders = await db.WorkOrders.CountAsync(w => w.OrganizationId == org.Id && w.Status == WorkOrderStatus.Active, ct);
        var plan = Plans.For(org.PlanTier);

        return new OrganizationDto(
            org.Id, org.Name, org.Slug, org.Industry, org.Country, org.Timezone, org.Status.ToApi(), org.CreatedAt,
            memberCount, activeOrders,
            new BrandingDto(org.DocumentShortName ?? org.Name, org.DocumentLegalName ?? org.Name, org.LogoUrl, org.AccentColor,
                org.DefaultOrigin, org.BaseName, org.Coordinators),
            Sla.EffectiveHours(org.SlaHoursOverrides).ToDictionary(kv => kv.Key.Name(), kv => kv.Value),
            Sla.StageHours.ToDictionary(kv => kv.Key.Name(), kv => kv.Value),
            NotificationDefaults.ToDictionary(kv => kv.Key, kv => org.NotificationSettings.GetValueOrDefault(kv.Key, kv.Value)),
            new SubscriptionDto(org.PlanTier.ToApi(), org.SubscriptionStatus.ToApi(), org.Seats, seatsUsed,
                org.MonthlyPriceUsd ?? plan.MonthlyPriceUsd, org.BillingCycleStart, org.BillingCycleEnd, org.TrialEndsAt, plan.Features),
            new SsoDto("microsoft", org.MicrosoftTenantId, org.MicrosoftTenantId is null ? "pending" : "verified",
                org.AllowedEmailDomains, org.AutoProvision, org.AutoProvisionRole));
    }

    /// <summary>Seats consumed = active + pending (unexpired) invitations.</summary>
    internal static Task<int> SeatsUsedAsync(AppDbContext db, Guid orgId, CancellationToken ct) =>
        db.OrganizationMembers.CountAsync(m => m.OrganizationId == orgId
            && (m.Status == MemberStatus.Active || m.Status == MemberStatus.Invited), ct);

    // ── Updates (org_admin only) ────────────────────────────────────────────

    internal static async Task<IResult> UpdateProfile(UpdateProfileRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;

        var errors = new Dictionary<string, string[]>();
        if (req.Name is not null && (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 200)) errors["name"] = ["1-200 characters."];
        if (req.Industry is { Length: > 100 }) errors["industry"] = ["Max 100 characters."];
        if (req.Country is { Length: > 100 }) errors["country"] = ["Max 100 characters."];
        if (req.Timezone is not null && !IsValidTimezone(req.Timezone)) errors["timezone"] = ["Unknown timezone, e.g. Africa/Lagos."];
        if (req.LogoUrl is { Length: > 0 } && !IsHttpsUrl(req.LogoUrl)) errors["logoUrl"] = ["Use an https:// image URL."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var before = new { org!.Name, org.Industry, org.Country, org.Timezone, org.LogoUrl };
        if (req.Name is not null) org.Name = req.Name.Trim();
        if (req.Industry is not null) org.Industry = Blank(req.Industry);
        if (req.Country is not null) org.Country = Blank(req.Country);
        if (req.Timezone is not null) org.Timezone = req.Timezone.Trim();
        if (req.LogoUrl is not null) org.LogoUrl = Blank(req.LogoUrl);

        Audit.Record(db, org.Id, me!, "org.profile_updated", "org", org.Id.ToString(), org.Name, clock.GetUtcNow(),
            new { before, after = new { org.Name, org.Industry, org.Country, org.Timezone, org.LogoUrl } });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<IResult> UpdateBranding(UpdateBrandingRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;

        var errors = new Dictionary<string, string[]>();
        if (req.ShortName is { Length: > 100 }) errors["shortName"] = ["Max 100 characters."];
        if (req.LegalName is { Length: > 200 }) errors["legalName"] = ["Max 200 characters."];
        if (req.AccentColor is not null && !HexColor().IsMatch(req.AccentColor)) errors["accentColor"] = ["Use a hex colour like #131722."];
        if (req.DefaultOrigin is { Length: > 100 }) errors["defaultOrigin"] = ["Max 100 characters."];
        if (req.BaseName is { Length: > 100 }) errors["baseName"] = ["Max 100 characters."];
        if (req.Coordinators is { } c && (c.Count > 2 || c.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100)))
            errors["coordinators"] = ["Up to 2 names, max 100 characters each."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (req.ShortName is not null) org!.DocumentShortName = Blank(req.ShortName);
        if (req.LegalName is not null) org!.DocumentLegalName = Blank(req.LegalName);
        if (req.AccentColor is not null) org!.AccentColor = req.AccentColor;
        if (req.DefaultOrigin is not null) org!.DefaultOrigin = Blank(req.DefaultOrigin);
        if (req.BaseName is not null) org!.BaseName = Blank(req.BaseName);
        if (req.Coordinators is not null) org!.Coordinators = req.Coordinators.Select(n => n.Trim()).ToList();

        Audit.Record(db, org!.Id, me!, "org.branding_updated", "org", org.Id.ToString(), org.Name, clock.GetUtcNow(), req);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<IResult> UpdateSla(UpdateSlaRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;
        if (req.Hours is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Required."] });

        var overrides = new Dictionary<string, double>();
        foreach (var (name, hours) in req.Hours)
        {
            if (!Stages.TryParse(name, out var stage) || !Sla.StageHours.ContainsKey(stage))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = [$"'{name}' is not a stage with an SLA."] });
            if (hours is < 0.25 or > 720 || double.IsNaN(hours))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = [$"{name}: use 0.25 to 720 hours."] });
            // Store only real overrides, so future default changes still apply elsewhere.
            if (Math.Abs(Sla.StageHours[stage] - hours) > 0.0001) overrides[stage.Name()] = Math.Round(hours, 2);
        }

        var before = org!.SlaHoursOverrides;
        org.SlaHoursOverrides = overrides;
        Audit.Record(db, org.Id, me!, "org.sla_updated", "org", org.Id.ToString(), org.Name, clock.GetUtcNow(), new { before, after = overrides });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<IResult> UpdateNotifications(UpdateNotificationsRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;
        if (req.Settings is null || req.Settings.Keys.Any(k => !NotificationDefaults.ContainsKey(k)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [$"Known keys: {string.Join(", ", NotificationDefaults.Keys)}."] });

        org!.NotificationSettings = NotificationDefaults.ToDictionary(kv => kv.Key, kv => req.Settings.GetValueOrDefault(kv.Key, kv.Value));
        Audit.Record(db, org.Id, me!, "org.notifications_updated", "org", org.Id.ToString(), org.Name, clock.GetUtcNow(), org.NotificationSettings);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<IResult> UpdateSso(UpdateSsoRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;

        var errors = new Dictionary<string, string[]>();
        var domains = (req.AllowedDomains ?? []).Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        if (domains.Count > 10 || domains.Any(d => !DomainPattern().IsMatch(d))) errors["allowedDomains"] = ["Up to 10 valid domains, e.g. acme.com."];
        if (domains.Any(PublicEmailDomains.Contains))
            errors["allowedDomains"] = ["Public email domains (gmail.com, outlook.com, …) cannot be used for auto-join."];
        if (req.TenantId is { Length: > 0 } t && !Guid.TryParse(t, out _) && !DomainPattern().IsMatch(t))
            errors["tenantId"] = ["Use the Directory (tenant) ID GUID or a domain like acme.onmicrosoft.com."];
        var role = req.DefaultRole ?? Roles.Requester;
        if (!RoleSets.IsOrgRole(role) || role == Roles.OrgAdmin) errors["defaultRole"] = ["Choose a non-admin role."];
        if (req.AutoProvision && domains.Count == 0) errors["allowedDomains"] = ["Add at least one domain to enable auto-join."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        // A domain can auto-join only one organisation.
        var taken = await db.Organizations.AsNoTracking()
            .Where(o => o.Id != org!.Id && o.AutoProvision)
            .Select(o => new { o.Name, o.AllowedEmailDomains })
            .ToListAsync(ct);
        var clash = taken.FirstOrDefault(o => o.AllowedEmailDomains.Intersect(domains).Any());
        if (req.AutoProvision && clash is not null)
            return Results.Problem(statusCode: 409, title: "One of these domains already auto-joins another organisation.");

        org!.MicrosoftTenantId = Blank(req.TenantId);
        org.AllowedEmailDomains = domains;
        org.AutoProvision = req.AutoProvision;
        org.AutoProvisionRole = role;
        Audit.Record(db, org.Id, me!, "sso.updated", "sso", org.Id.ToString(), org.Name, clock.GetUtcNow(),
            new { tenantId = org.MicrosoftTenantId, domains, req.AutoProvision, defaultRole = role });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, org, ct));
    }

    internal static async Task<IResult> RequestPlanChange(PlanChangeRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, IAppMailer mailer, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;
        if (!Plans.TryParse(req.Tier, out var tier))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["tier"] = ["Use starter, professional or enterprise."] });
        if (tier == org!.PlanTier) return Results.Problem(statusCode: 409, title: "You are already on this plan.");
        if (req.Message is { Length: > 1000 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["Max 1000 characters."] });

        // Downgrades must fit the seats already in use.
        var target = Plans.For(tier);
        var used = await SeatsUsedAsync(db, org.Id, ct);
        if (target.Seats is { } limit && used > limit)
            return Results.Problem(statusCode: 409, title: $"{target.Name} allows {limit} seats; you are using {used}. Remove members or invitations first.");

        Audit.Record(db, org.Id, me!, "subscription.change_requested", "subscription", org.Id.ToString(), org.Name, clock.GetUtcNow(),
            new { from = org.PlanTier.ToApi(), to = tier.ToApi(), message = req.Message });
        await db.SaveChangesAsync(ct);

        var emailed = !string.IsNullOrWhiteSpace(mailer.TeamNotificationEmail) && await mailer.TrySendAsync(
            InviteEmails.PlanChangeRequest(mailer.TeamNotificationEmail, org.Name, org.Slug, Plans.For(org.PlanTier).Name,
                target.Name, me!.Name, me.Email, req.Message), ct);

        return Results.Accepted(value: new { requested = tier.ToApi(), teamNotified = emailed });
    }

    internal static async Task<IResult> RequestDeletion(DeletionRequest req, HttpContext http, CurrentMemberResolver resolver,
        AppDbContext db, IAppMailer mailer, TimeProvider clock, CancellationToken ct)
    {
        var (me, org, denied) = await AdminOrgAsync(http, resolver, db, ct);
        if (denied is not null) return denied;
        if (!string.Equals(req.ConfirmSlug?.Trim(), org!.Slug, StringComparison.Ordinal))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmSlug"] = [$"Type '{org.Slug}' to confirm."] });

        // Deletion is a request (data export / retention obligations), handled by the platform team.
        var now = clock.GetUtcNow();
        org.Status = OrganizationStatus.DeletionRequested;
        org.DeletionRequestedAt = now;
        Audit.Record(db, org.Id, me!, "org.deletion_requested", "org", org.Id.ToString(), org.Name, now);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(mailer.TeamNotificationEmail))
            await mailer.TrySendAsync(new BrevoEmail(mailer.TeamNotificationEmail, "EquipTrack team",
                $"Deletion requested: {org.Name}",
                $"<p>{System.Net.WebUtility.HtmlEncode(me!.Name)} ({System.Net.WebUtility.HtmlEncode(me.Email)}) requested deletion of <strong>{System.Net.WebUtility.HtmlEncode(org.Name)}</strong> ({org.Slug}).</p>",
                $"{me.Name} ({me.Email}) requested deletion of {org.Name} ({org.Slug})."), ct);

        return Results.Accepted(value: new { status = org.Status.ToApi() });
    }

    // ── Audit log ───────────────────────────────────────────────────────────

    /// <summary>Admin events + work order events for the organisation, newest first.</summary>
    internal static async Task<IResult> GetAudit(int? limit, DateTimeOffset? before, string? targetType,
        HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);
        if (me.Role is not (Roles.OrgAdmin or Roles.Executive)) return Results.Forbid();

        var take = Math.Clamp(limit ?? 100, 1, 500);
        var cutoff = before ?? DateTimeOffset.MaxValue;
        var events = new List<AuditEventDto>();

        if (targetType is null or not "workorder")
        {
            var q = db.OrgAuditEvents.AsNoTracking().Where(e => e.OrganizationId == me.OrganizationId && e.OccurredAt < cutoff);
            if (targetType is not null) q = q.Where(e => e.TargetType == targetType);
            var rows = await q.OrderByDescending(e => e.OccurredAt).Take(take).ToListAsync(ct);
            events.AddRange(rows.Select(e => new AuditEventDto($"a-{e.Id}", e.OrganizationId, e.ActorUserId, e.ActorName, e.ActorRole,
                e.Action, e.TargetType, e.TargetId, e.TargetLabel, ParseJson(e.Metadata), e.OccurredAt)));
        }

        if (targetType is null or "workorder")
        {
            var rows = await db.WorkOrderEvents.AsNoTracking()
                .Join(db.WorkOrders.Where(w => w.OrganizationId == me.OrganizationId), e => e.WorkOrderId, w => w.Id,
                    (e, w) => new { e, w.Number })
                .Where(x => x.e.OccurredAt < cutoff)
                .OrderByDescending(x => x.e.OccurredAt).Take(take).ToListAsync(ct);
            events.AddRange(rows.Select(x => new AuditEventDto($"w-{x.e.Id}", me.OrganizationId, x.e.ActorUserId, x.e.ActorName, x.e.ActorRole,
                $"workorder.{x.e.Action}", "workorder", x.e.WorkOrderId.ToString(), x.Number,
                JsonSerializer.SerializeToElement(new
                {
                    fromStage = x.e.FromStage?.Name(),
                    toStage = x.e.ToStage?.Name(),
                    assignee = x.e.AssigneeName,
                    note = x.e.Note,
                }), x.e.OccurredAt)));
        }

        return Results.Ok(events.OrderByDescending(e => e.CreatedAt).Take(take));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static async Task<(MemberContext? Me, Organization? Org, IResult? Denied)> AdminOrgAsync(
        HttpContext http, CurrentMemberResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return (null, null, CurrentMemberResolver.Problem(result));
        if (me.Role != Roles.OrgAdmin) return (me, null, Results.Forbid());
        var org = await db.Organizations.FirstAsync(o => o.Id == me.OrganizationId, ct);
        return (me, org, null);
    }

    private static JsonElement ParseJson(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsHttpsUrl(string value) =>
        value.Length <= 500 && Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>IANA timezone name format (e.g. Africa/Lagos, America/Argentina/Buenos_Aires) or UTC.
    /// Format-checked only: the API runs with invariant globalisation, so OS lookups are not reliable.</summary>
    private static bool IsValidTimezone(string tz) => tz.Trim() == "UTC" || TimezonePattern().IsMatch(tz.Trim());

    [GeneratedRegex(@"^[A-Z][A-Za-z_]+(/[A-Z][A-Za-z0-9_+\-]+){1,2}$")]
    private static partial Regex TimezonePattern();

    internal static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "outlook.com", "hotmail.com", "live.com", "icloud.com",
        "aol.com", "proton.me", "protonmail.com", "ymail.com", "msn.com",
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
}
