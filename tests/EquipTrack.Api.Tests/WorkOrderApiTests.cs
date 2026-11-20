using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EquipTrack.Api.Domain;
using EquipTrack.Api.Organizations;
using Microsoft.EntityFrameworkCore;

namespace EquipTrack.Api.Tests;

/// <summary>End-to-end through HTTP: auth, membership, tenant isolation and the lifecycle API.</summary>
public sealed class WorkOrderApiTests : IDisposable
{
    private readonly ApiFactory _api = new();

    public void Dispose() => _api.Dispose();

    private sealed record Person(string UserId, string Email, string Name, string Role, Guid MemberId);

    private async Task<(Guid OrgId, Dictionary<string, Person> People)> SeedOrgAsync(string slug)
    {
        var people = new Dictionary<string, Person>();
        var orgId = await _api.WithDbAsync(async db =>
        {
            var org = new Organization { Name = slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
            db.Organizations.Add(org);
            foreach (var role in new[]
                     {
                         Roles.OrgAdmin, Roles.Requester, Roles.WarehouseSupervisor, Roles.WarehousePersonnel,
                         Roles.DispatchSupervisor, Roles.DispatchPersonnel, Roles.Qaqc, Roles.Executive, Roles.Logistics,
                     })
            {
                var userId = $"{slug}-{role}";
                var member = new OrganizationMember
                {
                    OrganizationId = org.Id,
                    Email = $"{role}@{slug}.example",
                    DisplayName = $"{slug} {role}",
                    Role = role,
                    UserId = userId,
                    Status = MemberStatus.Active,
                    InvitedAt = DateTimeOffset.UtcNow,
                    JoinedAt = DateTimeOffset.UtcNow,
                };
                db.OrganizationMembers.Add(member);
                people[role] = new Person(userId, member.Email, member.DisplayName, role, member.Id);
            }
            await db.SaveChangesAsync();
            return org.Id;
        });
        return (orgId, people);
    }

    private HttpClient As(Person p) => _api.ClientFor(p.UserId, p.Email, name: p.Name);

    private static object NewOrder(string type = "SAP") => new
    {
        requestType = type,
        urgency = "High",
        destination = "Offshore Site A",
        items = new[] { new { description = "Drill pipe 5in", quantity = 12, unit = "EA" } },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await call;
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await Json(r);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, Guid id, object body) =>
        c.PostAsJsonAsync($"/api/work-orders/{id}/actions", body);

    // ── Authentication ──────────────────────────────────────────────────────

    [Fact]
    public async Task Requests_without_a_valid_token_are_unauthorized()
    {
        var anon = _api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/work-orders")).StatusCode);

        anon.DefaultRequestHeaders.Authorization = new("Bearer", new TestTokens().Issue("x", "x@y.example"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Signed_in_user_without_membership_is_told_to_ask_for_an_invitation()
    {
        var r = await _api.ClientFor("stranger", "stranger@nowhere.example").GetAsync("/api/work-orders");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("invitation", await r.Content.ReadAsStringAsync());
    }

    // ── Invitations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Invitation_is_claimed_only_with_a_verified_email()
    {
        var (_, people) = await SeedOrgAsync("acme");
        await Ok(As(people[Roles.OrgAdmin]).PostAsJsonAsync("/api/org/members",
            new { email = "New.Hire@Acme.example", name = "New Hire", role = Roles.WarehousePersonnel }), HttpStatusCode.Created);

        // Same address, not verified yet → no access, invitation untouched.
        var unverified = await _api.ClientFor("new-user", "new.hire@acme.example", emailVerified: false).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        Assert.Contains("Verify", await unverified.Content.ReadAsStringAsync());

        var me = await Ok(_api.ClientFor("new-user", "new.hire@acme.example").GetAsync("/api/me"));
        Assert.Equal(Roles.WarehousePersonnel, me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Only_org_admins_can_invite_and_never_as_sysadmin()
    {
        var (_, people) = await SeedOrgAsync("acme");
        Assert.Equal(HttpStatusCode.Forbidden, (await As(people[Roles.WarehouseSupervisor]).PostAsJsonAsync("/api/org/members",
            new { email = "x@acme.example", name = "X", role = Roles.Requester })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(people[Roles.OrgAdmin]).PostAsJsonAsync("/api/org/members",
            new { email = "x@acme.example", name = "X", role = Roles.Sysadmin })).StatusCode);
    }

    [Fact]
    public async Task Sysadmin_bootstraps_an_organisation_and_its_admin()
    {
        var sys = _api.ClientFor("platform-1", ApiFactory.SysadminEmail);
        await Ok(sys.PostAsJsonAsync("/api/admin/organizations",
            new { name = "Acme Energy", slug = "acme-energy", adminEmail = "boss@acme.example", adminName = "Boss" }), HttpStatusCode.Created);

        var me = await Ok(_api.ClientFor("boss-1", "boss@acme.example").GetAsync("/api/me"));
        Assert.Equal(Roles.OrgAdmin, me.GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.ClientFor("boss-1", "boss@acme.example")
            .PostAsJsonAsync("/api/admin/organizations", new { name = "X", slug = "xxx", adminEmail = "a@b.example" })).StatusCode);
    }

    // ── Work orders ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Requester_creates_order_with_sequential_reference_and_audit_history()
    {
        var (_, people) = await SeedOrgAsync("acme");
        var req = As(people[Roles.Requester]);

        var first = await Ok(req.PostAsJsonAsync("/api/work-orders", NewOrder()), HttpStatusCode.Created);
        var second = await Ok(req.PostAsJsonAsync("/api/work-orders", NewOrder("TR")), HttpStatusCode.Created);

        var s1 = first.GetProperty("summary");
        Assert.Matches(@"^DEL-\d{2}-00001$", s1.GetProperty("number").GetString());
        Assert.Matches(@"^DEL-\d{2}-00002$", second.GetProperty("summary").GetProperty("number").GetString());
        Assert.Equal("New Request", s1.GetProperty("stage").GetString());
        Assert.Equal("Pending Base Coordinator Approval", second.GetProperty("summary").GetProperty("stage").GetString());
        Assert.Equal("Warehouse Supervisor", s1.GetProperty("pendingActionBy").GetString());
        Assert.Equal("Created", first.GetProperty("history")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Non_requesters_cannot_create_orders() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await As((await SeedOrgAsync("acme")).People[Roles.WarehouseSupervisor])
            .PostAsJsonAsync("/api/work-orders", NewOrder())).StatusCode);

    [Fact]
    public async Task Warehouse_flow_enforces_roles_and_assignment()
    {
        var (_, p) = await SeedOrgAsync("acme");
        var created = await Ok(As(p[Roles.Requester]).PostAsJsonAsync("/api/work-orders", NewOrder()), HttpStatusCode.Created);
        var id = created.GetProperty("summary").GetProperty("id").GetGuid();

        // Personnel cannot see an order before it is assigned to them.
        Assert.Equal(HttpStatusCode.NotFound, (await As(p[Roles.WarehousePersonnel]).GetAsync($"/api/work-orders/{id}")).StatusCode);

        // Assigning a dispatch person to warehouse work is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(As(p[Roles.WarehouseSupervisor]), id,
            new { action = "AssignWarehouse", assigneeMemberId = p[Roles.DispatchPersonnel].MemberId })).StatusCode);

        var assigned = await Ok(Act(As(p[Roles.WarehouseSupervisor]), id,
            new { action = "AssignWarehouse", assigneeMemberId = p[Roles.WarehousePersonnel].MemberId }));
        Assert.Equal(p[Roles.WarehousePersonnel].Name, assigned.GetProperty("summary").GetProperty("pendingActionBy").GetString());

        var wh = As(p[Roles.WarehousePersonnel]);
        var detail = await Ok(wh.GetAsync($"/api/work-orders/{id}"));
        Assert.Contains("StartPicking", detail.GetProperty("availableActions").EnumerateArray().Select(a => a.GetString()));

        // Stale screen: acting on an outdated stage is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await Act(wh, id, new { action = "StartPicking", expectedStage = "New Request" })).StatusCode);

        await Ok(Act(wh, id, new { action = "StartPicking", expectedStage = "Warehouse Assigned" }));
        await Ok(Act(wh, id, new { action = "CreateGoodsIssue" }));
        var transferred = await Ok(Act(wh, id, new { action = "TransferToDispatch" }));
        Assert.Equal("Transferred to Dispatch", transferred.GetProperty("summary").GetProperty("stage").GetString());
        Assert.Equal("Dispatch Supervisor", transferred.GetProperty("summary").GetProperty("pendingActionBy").GetString());
        var received = await Ok(Act(As(p[Roles.DispatchSupervisor]), id, new { action = "AcknowledgeHandover" }));
        Assert.Equal("Dispatch Queue", received.GetProperty("summary").GetProperty("stage").GetString());

        // Wrong stage for the action → 409; wrong role → 403.
        Assert.Equal(HttpStatusCode.Conflict, (await Act(As(p[Roles.WarehouseSupervisor]), id, new { action = "CreateGoodsIssue" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(As(p[Roles.Qaqc]), id,
            new { action = "AssignDispatch", assigneeMemberId = p[Roles.DispatchPersonnel].MemberId })).StatusCode);

        var history = (await Ok(As(p[Roles.Executive]).GetAsync($"/api/work-orders/{id}"))).GetProperty("history");
        Assert.Equal(new[] { "Created", "AssignWarehouse", "StartPicking", "CreateGoodsIssue", "TransferToDispatch", "AcknowledgeHandover" },
            history.EnumerateArray().Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(p[Roles.WarehousePersonnel].Name, history[2].GetProperty("actor").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Organisations_are_isolated()
    {
        var (_, acme) = await SeedOrgAsync("acme");
        var (_, globex) = await SeedOrgAsync("globex");
        var created = await Ok(As(acme[Roles.Requester]).PostAsJsonAsync("/api/work-orders", NewOrder()), HttpStatusCode.Created);
        var id = created.GetProperty("summary").GetProperty("id").GetGuid();

        // Another organisation's executive cannot see or act on it.
        Assert.Equal(HttpStatusCode.NotFound, (await As(globex[Roles.Executive]).GetAsync($"/api/work-orders/{id}")).StatusCode);
        Assert.Empty((await Ok(As(globex[Roles.Executive]).GetAsync("/api/work-orders"))).EnumerateArray());

        // And cannot assign their own staff to it, nor have acme assign globex staff.
        Assert.Equal(HttpStatusCode.NotFound, (await Act(As(globex[Roles.WarehouseSupervisor]), id,
            new { action = "AssignWarehouse", assigneeMemberId = globex[Roles.WarehousePersonnel].MemberId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(As(acme[Roles.WarehouseSupervisor]), id,
            new { action = "AssignWarehouse", assigneeMemberId = globex[Roles.WarehousePersonnel].MemberId })).StatusCode);
    }

    [Fact]
    public async Task Requesters_only_see_their_own_orders()
    {
        var (_, p) = await SeedOrgAsync("acme");
        await Ok(As(p[Roles.Requester]).PostAsJsonAsync("/api/work-orders", NewOrder()), HttpStatusCode.Created);

        await _api.WithDbAsync(async db =>
        {
            var orgId = await db.Organizations.Select(o => o.Id).SingleAsync();
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = orgId, Email = "other@acme.example", DisplayName = "Other", Role = Roles.Requester,
                UserId = "other-req", Status = MemberStatus.Active, InvitedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        var other = _api.ClientFor("other-req", "other@acme.example");
        Assert.Empty((await Ok(other.GetAsync("/api/work-orders"))).EnumerateArray());
        Assert.Single((await Ok(As(p[Roles.Requester]).GetAsync("/api/work-orders"))).EnumerateArray());
        Assert.Single((await Ok(As(p[Roles.Executive]).GetAsync("/api/work-orders"))).EnumerateArray());
    }

    [Fact]
    public async Task Suspended_members_lose_access()
    {
        var (_, p) = await SeedOrgAsync("acme");
        var target = p[Roles.Requester];
        await Ok(As(p[Roles.OrgAdmin]).PatchAsJsonAsync($"/api/org/members/{target.MemberId}", new { status = "suspended" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await As(target).GetAsync("/api/work-orders")).StatusCode);
    }
}
