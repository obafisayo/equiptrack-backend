using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EquipTrack.Api.Domain;
using EquipTrack.Api.Organizations;
using Microsoft.EntityFrameworkCore;

namespace EquipTrack.Api.Tests;

/// <summary>Org admin sections: settings, branding, SLA, notifications, SSO/auto-join, billing, audit, invitations.</summary>
public sealed class OrgAdminApiTests : IDisposable
{
    private readonly ApiFactory _api = new();
    public void Dispose() => _api.Dispose();

    private sealed record Person(string UserId, string Email, string Name, string Role, Guid MemberId);

    private async Task<(Guid OrgId, Dictionary<string, Person> People)> SeedAsync(string slug = "acme", Action<Organization>? configure = null)
    {
        var people = new Dictionary<string, Person>();
        var orgId = await _api.WithDbAsync(async db =>
        {
            var org = new Organization { Name = "Acme Energy", Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
            configure?.Invoke(org);
            db.Organizations.Add(org);
            foreach (var role in new[] { Roles.OrgAdmin, Roles.Requester, Roles.WarehouseSupervisor, Roles.Executive })
            {
                var m = new OrganizationMember
                {
                    OrganizationId = org.Id, Email = $"{role}@{slug}.example", DisplayName = $"{slug} {role}", Role = role,
                    UserId = $"{slug}-{role}", Status = MemberStatus.Active, InvitedAt = DateTimeOffset.UtcNow, JoinedAt = DateTimeOffset.UtcNow,
                };
                db.OrganizationMembers.Add(m);
                people[role] = new Person(m.UserId!, m.Email, m.DisplayName, role, m.Id);
            }
            await db.SaveChangesAsync();
            return org.Id;
        });
        return (orgId, people);
    }

    private HttpClient As(Person p) => _api.ClientFor(p.UserId, p.Email, name: p.Name);

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await call;
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    // ── Organisation & settings ─────────────────────────────────────────────

    [Fact]
    public async Task Any_member_reads_the_organisation_but_only_admins_change_it()
    {
        var (_, p) = await SeedAsync();
        var org = await Ok(As(p[Roles.Requester]).GetAsync("/api/org"));
        Assert.Equal("Acme Energy", org.GetProperty("name").GetString());
        Assert.Equal("starter", org.GetProperty("subscription").GetProperty("tier").GetString());
        Assert.Equal(4, org.GetProperty("subscription").GetProperty("seatsUsed").GetInt32());
        Assert.Equal("Acme Energy", org.GetProperty("branding").GetProperty("shortName").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await As(p[Roles.Requester]).PatchAsJsonAsync("/api/org/profile", new { name = "X" })).StatusCode);

        var updated = await Ok(As(p[Roles.OrgAdmin]).PatchAsJsonAsync("/api/org/profile",
            new { name = "Acme Energy Nigeria", industry = "Oil & Gas", country = "Nigeria", timezone = "Africa/Lagos" }));
        Assert.Equal("Acme Energy Nigeria", updated.GetProperty("name").GetString());
        Assert.Equal("acme", updated.GetProperty("slug").GetString()); // slug never changes

        Assert.Equal(HttpStatusCode.BadRequest, (await As(p[Roles.OrgAdmin]).PatchAsJsonAsync("/api/org/profile", new { timezone = "Lagos time" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(p[Roles.OrgAdmin]).PatchAsJsonAsync("/api/org/profile", new { logoUrl = "http://insecure.example/logo.png" })).StatusCode);
    }

    [Fact]
    public async Task Branding_is_validated_and_returned_for_documents()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync("/api/org/branding", new { accentColor = "red" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync("/api/org/branding", new { coordinators = new[] { "a", "b", "c" } })).StatusCode);

        var org = await Ok(admin.PatchAsJsonAsync("/api/org/branding", new
        {
            shortName = "Acme", legalName = "Acme Energy Nigeria Ltd.", accentColor = "#E2001A",
            defaultOrigin = "ONNE BASE", baseName = "Onne Base", coordinators = new[] { "Kenneth Omireh" },
        }));
        var b = org.GetProperty("branding");
        Assert.Equal("Acme Energy Nigeria Ltd.", b.GetProperty("legalName").GetString());
        Assert.Equal("Kenneth Omireh", b.GetProperty("coordinators")[0].GetString());
    }

    [Fact]
    public async Task Sla_overrides_change_how_work_orders_are_timed()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/org/sla", new { hours = new Dictionary<string, double> { ["Shipped"] = 5 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/org/sla", new { hours = new Dictionary<string, double> { ["New Request"] = 0 } })).StatusCode);

        var org = await Ok(admin.PutAsJsonAsync("/api/org/sla", new { hours = new Dictionary<string, double> { ["New Request"] = 6, ["Picking"] = 8 } }));
        Assert.Equal(6, org.GetProperty("slaHours").GetProperty("New Request").GetDouble());
        Assert.Equal(4, org.GetProperty("slaDefaults").GetProperty("New Request").GetDouble());

        var created = await Ok(As(p[Roles.Requester]).PostAsJsonAsync("/api/work-orders", new
        {
            requestType = "SAP", urgency = "High", destination = "Site",
            items = new[] { new { description = "Pipe", quantity = 1, unit = "EA" } },
        }), HttpStatusCode.Created);
        Assert.Equal(6, created.GetProperty("summary").GetProperty("sla").GetProperty("slaHours").GetDouble());
    }

    [Fact]
    public async Task Notification_settings_accept_known_keys_only()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/org/notifications", new { settings = new Dictionary<string, bool> { ["made_up"] = true } })).StatusCode);
        var org = await Ok(admin.PutAsJsonAsync("/api/org/notifications", new { settings = new Dictionary<string, bool> { ["sla_breach"] = false } }));
        Assert.False(org.GetProperty("notifications").GetProperty("sla_breach").GetBoolean());
        Assert.True(org.GetProperty("notifications").GetProperty("new_request").GetBoolean()); // default kept
    }

    // ── SSO / auto-join ─────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_join_adds_verified_users_from_the_organisations_domain()
    {
        var (orgId, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/org/sso",
            new { allowedDomains = new[] { "gmail.com" }, autoProvision = true, defaultRole = Roles.Requester })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/org/sso",
            new { allowedDomains = new[] { "acme.com" }, autoProvision = true, defaultRole = Roles.OrgAdmin })).StatusCode);

        var org = await Ok(admin.PutAsJsonAsync("/api/org/sso",
            new { tenantId = Guid.NewGuid().ToString(), allowedDomains = new[] { "@Acme.com" }, autoProvision = true, defaultRole = Roles.WarehousePersonnel }));
        Assert.Equal("acme.com", org.GetProperty("sso").GetProperty("allowedDomains")[0].GetString());
        Assert.Equal("verified", org.GetProperty("sso").GetProperty("status").GetString());

        // Unverified email at the domain: no access.
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.ClientFor("new-1", "ada@acme.com", emailVerified: false).GetAsync("/api/me")).StatusCode);

        // Verified: joins with the default role, admin is notified, audit recorded.
        var me = await Ok(_api.ClientFor("new-1", "ada@acme.com", name: "Ada Obi").GetAsync("/api/me"));
        Assert.Equal(Roles.WarehousePersonnel, me.GetProperty("role").GetString());
        Assert.Contains(_api.Brevo.Emails, e => e.ToEmail == p[Roles.OrgAdmin].Email && e.Subject.Contains("Ada Obi joined"));
        Assert.True(await _api.WithDbAsync(db => db.OrgAuditEvents.AnyAsync(a => a.OrganizationId == orgId && a.Action == "member.auto_provisioned")));

        // A suspended auto-joined member is not silently re-added.
        var memberId = await _api.WithDbAsync(db => db.OrganizationMembers.Where(m => m.Email == "ada@acme.com").Select(m => m.Id).SingleAsync());
        await Ok(admin.PatchAsJsonAsync($"/api/org/members/{memberId}", new { status = "suspended" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.ClientFor("new-1", "ada@acme.com").GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_domain_can_auto_join_only_one_organisation()
    {
        var (_, acme) = await SeedAsync("acme");
        var (_, other) = await SeedAsync("other");
        await Ok(As(acme[Roles.OrgAdmin]).PutAsJsonAsync("/api/org/sso", new { allowedDomains = new[] { "shared.com" }, autoProvision = true, defaultRole = Roles.Requester }));
        Assert.Equal(HttpStatusCode.Conflict, (await As(other[Roles.OrgAdmin]).PutAsJsonAsync("/api/org/sso",
            new { allowedDomains = new[] { "shared.com" }, autoProvision = true, defaultRole = Roles.Requester })).StatusCode);
    }

    // ── Invitations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Invitation_is_emailed_expires_and_can_be_resent_or_revoked()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);

        var inv = await Ok(admin.PostAsJsonAsync("/api/org/members",
            new { email = "new@acme.example", name = "New Person", role = Roles.Requester, department = "Drilling" }), HttpStatusCode.Created);
        Assert.NotNull(inv.GetProperty("inviteEmailSentAt").GetString());
        Assert.Equal("Drilling", inv.GetProperty("department").GetString());
        var email = Assert.Single(_api.Brevo.Emails, e => e.ToEmail == "new@acme.example");
        Assert.Contains("https://app.equiptrack.test/auth/sign-up?email=new%40acme.example", email.TextContent);

        var id = inv.GetProperty("id").GetGuid();

        // Expire it: it can no longer be claimed…
        await _api.WithDbAsync(async db =>
        {
            var m = await db.OrganizationMembers.SingleAsync(x => x.Id == id);
            m.InviteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            return await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.ClientFor("new-user", "new@acme.example").GetAsync("/api/me")).StatusCode);

        // …until it is resent (renewed).
        var resent = await Ok(admin.PostAsync($"/api/org/members/{id}/resend", null));
        Assert.True(resent.GetProperty("emailSent").GetBoolean());
        Assert.False(resent.GetProperty("member").GetProperty("inviteExpired").GetBoolean());

        // Revoke a different pending invitation.
        var other = await Ok(admin.PostAsJsonAsync("/api/org/members", new { email = "x@acme.example", name = "X", role = Roles.Requester }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/org/members/{other.GetProperty("id").GetGuid()}")).StatusCode);
        // Joined members cannot be "revoked" (suspend them instead).
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/org/members/{p[Roles.Requester].MemberId}")).StatusCode);
    }

    [Fact]
    public async Task Seat_limit_blocks_new_invitations()
    {
        var (_, p) = await SeedAsync(configure: o => o.Seats = 5);
        var admin = As(p[Roles.OrgAdmin]);
        await Ok(admin.PostAsJsonAsync("/api/org/members", new { email = "a@acme.example", name = "A", role = Roles.Requester }), HttpStatusCode.Created);
        var r = await admin.PostAsJsonAsync("/api/org/members", new { email = "b@acme.example", name = "B", role = Roles.Requester });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("seats", await r.Content.ReadAsStringAsync());
    }

    // ── Billing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_change_requests_notify_the_team_and_respect_seats()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);

        var plans = await Ok(_api.CreateClient().GetAsync("/api/plans"));
        Assert.Equal(3, plans.GetArrayLength());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/org/subscription/change-request", new { tier = "starter" })).StatusCode);
        var accepted = await Ok(admin.PostAsJsonAsync("/api/org/subscription/change-request",
            new { tier = "professional", message = "We are adding a second yard." }), HttpStatusCode.Accepted);
        Assert.True(accepted.GetProperty("teamNotified").GetBoolean());
        Assert.Contains(_api.Brevo.Emails, e => e.ToEmail == "team@equiptrack.test" && e.Subject.Contains("Professional"));
        Assert.Equal(HttpStatusCode.Forbidden, (await As(p[Roles.Executive]).PostAsJsonAsync("/api/org/subscription/change-request", new { tier = "professional" })).StatusCode);
    }

    [Fact]
    public async Task Deletion_request_needs_the_slug_typed_exactly()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/org/deletion-request", new { confirmSlug = "ACME" })).StatusCode);
        var r = await Ok(admin.PostAsJsonAsync("/api/org/deletion-request", new { confirmSlug = "acme" }), HttpStatusCode.Accepted);
        Assert.Equal("deletion_requested", r.GetProperty("status").GetString());
    }

    // ── Audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_log_merges_admin_and_work_order_events()
    {
        var (_, p) = await SeedAsync();
        var admin = As(p[Roles.OrgAdmin]);
        await Ok(admin.PostAsJsonAsync("/api/org/members", new { email = "z@acme.example", name = "Zed", role = Roles.Requester }), HttpStatusCode.Created);
        await Ok(admin.PatchAsJsonAsync($"/api/org/members/{p[Roles.Requester].MemberId}", new { role = Roles.Executive }));
        await Ok(As(p[Roles.WarehouseSupervisor]).GetAsync("/api/org")); // reading is not audited

        var events = await Ok(admin.GetAsync("/api/org/audit"));
        var actions = events.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("member.invited", actions);
        Assert.Contains("member.role_changed", actions);

        var onlyUsers = await Ok(admin.GetAsync("/api/org/audit?targetType=user"));
        Assert.All(onlyUsers.EnumerateArray(), e => Assert.Equal("user", e.GetProperty("targetType").GetString()));

        Assert.Equal(HttpStatusCode.Forbidden, (await As(p[Roles.WarehouseSupervisor]).GetAsync("/api/org/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(p[Roles.Executive]).GetAsync("/api/org/audit")).StatusCode);
    }
}
