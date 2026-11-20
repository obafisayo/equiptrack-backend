using System.Data;
using EquipTrack.Api.Auth;
using EquipTrack.Api.Data;
using EquipTrack.Api.Domain;
using EquipTrack.Api.Organizations;
using Microsoft.EntityFrameworkCore;

namespace EquipTrack.Api.WorkOrders;

public static class WorkOrderEndpoints
{
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapWorkOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders").RequireAuthorization();
        group.MapPost("/", Create);
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/actions", Act);
        return app;
    }

    // ── Create ──────────────────────────────────────────────────────────────

    internal static async Task<IResult> Create(
        CreateWorkOrderRequest req, HttpContext http, CurrentMemberResolver resolver, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);

        var errors = new Dictionary<string, string[]>();
        if (!WorkOrderMapping.TryParseRequestType(req.RequestType, out var requestType))
            errors["requestType"] = ["Use SAP, TR, VENDOR or NON_STOCK."];
        if (!Enum.TryParse<Urgency>(req.Urgency, ignoreCase: false, out var urgency) || !Enum.IsDefined(urgency))
            errors["urgency"] = ["Use Low, Medium, High or Urgent."];
        if (req.Items is null or { Count: 0 or > 100 }) errors["items"] = ["Provide between 1 and 100 items."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var items = req.Items!.Select(i => new WorkOrderItem
        {
            Description = i.Description?.Trim() ?? string.Empty,
            Quantity = i.Quantity,
            Unit = i.Unit?.Trim() ?? string.Empty,
            PartNumber = Trunc(i.PartNumber, 50),
            Plant = Trunc(i.Plant, 50),
            BinLocation = Trunc(i.BinLocation, 50),
        }).ToList();
        if (items.Any(i => i.Description.Length > 500 || i.Unit.Length > 20))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Item description max 500, unit max 20 characters."] });

        var now = clock.GetUtcNow();
        var number = await NextNumberAsync(db, me.OrganizationId, now, ct);
        var (order, error) = Lifecycle.Create(
            new CreateWorkOrder(requestType, urgency, req.Destination ?? string.Empty, req.RequiredDate,
                Trunc(req.Entity, 100), req.Notes, Trunc(req.TrDepartment, 100), items),
            me.ToActor(), me.OrganizationId, number, now);
        if (error is not null) return ToProblem(error);

        db.WorkOrders.Add(order!);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/work-orders/{order!.Id}", order.ToDetail(now, Available(order, me)));
    }

    // ── List ────────────────────────────────────────────────────────────────

    /// <param name="status">active (default) | completed | rejected | all</param>
    /// <param name="stage">Optional exact stage name, e.g. "Picking".</param>
    /// <param name="mine">true → only orders assigned to me (or requested by me, for requesters).</param>
    internal static async Task<IResult> List(
        string? status, string? stage, bool? mine, int? limit,
        HttpContext http, CurrentMemberResolver resolver, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);

        var query = VisibleTo(db.WorkOrders.AsNoTracking(), me);

        switch (status ?? "active")
        {
            case "active": query = query.Where(o => o.Status == WorkOrderStatus.Active); break;
            case "completed": query = query.Where(o => o.Status == WorkOrderStatus.Completed); break;
            case "rejected": query = query.Where(o => o.Status == WorkOrderStatus.Rejected); break;
            case "all": break;
            default: return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Use active, completed, rejected or all."] });
        }

        if (!string.IsNullOrEmpty(stage))
        {
            if (!Stages.TryParse(stage, out var s))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["stage"] = ["Unknown stage."] });
            query = query.Where(o => o.Stage == s);
        }

        if (mine == true)
            query = me.Role == Roles.Requester
                ? query.Where(o => o.RequesterUserId == me.UserId)
                : query.Where(o => o.AssigneeUserId == me.UserId);

        var take = Math.Clamp(limit ?? 100, 1, MaxPageSize);
        var orders = await query.OrderByDescending(o => o.CreatedAt).Take(take).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return Results.Ok(orders.Select(o => o.ToSummary(now)));
    }

    // ── Detail ──────────────────────────────────────────────────────────────

    internal static async Task<IResult> Get(
        Guid id, HttpContext http, CurrentMemberResolver resolver, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);

        var order = await VisibleTo(db.WorkOrders.AsNoTracking(), me)
            .Include(o => o.Items).Include(o => o.Events)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        // 404 (not 403) for orders outside the caller's view: do not reveal they exist.
        return order is null ? Results.NotFound() : Results.Ok(order.ToDetail(clock.GetUtcNow(), Available(order, me)));
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    internal static async Task<IResult> Act(
        Guid id, WorkOrderActionRequest req, HttpContext http, CurrentMemberResolver resolver, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var (me, result) = await resolver.ResolveAsync(http, ct);
        if (me is null) return CurrentMemberResolver.Problem(result);

        if (!Enum.TryParse<WorkflowAction>(req.Action, ignoreCase: true, out var action)
            || !Enum.IsDefined(action) || action == WorkflowAction.Created)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["Unknown action."] });

        var order = await VisibleTo(db.WorkOrders, me)
            .Include(o => o.Items).Include(o => o.Events)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return Results.NotFound();

        // Stale-screen guard: the user acted on what they saw.
        if (req.ExpectedStage is not null && req.ExpectedStage != order.Stage.Name())
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"This order has moved to '{order.Stage.Name()}'. Refresh and try again.");

        WorkflowCommand? command = action switch
        {
            WorkflowAction.ApproveRequest => new ApproveRequest(),
            WorkflowAction.RejectRequest => new RejectRequest(req.Reason ?? ""),
            WorkflowAction.StartPicking => new StartPicking(),
            WorkflowAction.CreateGoodsIssue => new CreateGoodsIssue(),
            WorkflowAction.TransferToDispatch => new TransferToDispatch(),
            WorkflowAction.SendToPreloadQaqc => new SendToPreloadQaqc(),
            WorkflowAction.Containerize => new Containerize(req.ContainerRef ?? ""),
            WorkflowAction.SubmitForPostQaqc => new SubmitForPostQaqc(),
            WorkflowAction.PassPostQaqc => new PassPostQaqc(),
            WorkflowAction.FailPostQaqc => new FailPostQaqc(req.Reason ?? ""),
            WorkflowAction.GenerateWaybill => new GenerateWaybill(req.WaybillNumber ?? ""),
            WorkflowAction.ApproveWaybill => new ApproveWaybill(),
            WorkflowAction.RejectWaybill => new RejectWaybill(req.Reason ?? ""),
            WorkflowAction.RequestDeckspace => new RequestDeckspace(),
            WorkflowAction.AllocateVessel => new AllocateVessel(req.VesselName ?? ""),
            WorkflowAction.MarkShipped => new MarkShipped(),
            WorkflowAction.ConfirmReceipt => new ConfirmReceipt(),
            WorkflowAction.Reverse => new Reverse(req.Reason ?? ""),
            _ => null, // assignment actions below
        };

        if (command is null)
        {
            // Assignee comes from the members table — role is the stored one, not client-supplied.
            var assignee = req.AssigneeMemberId is { } memberId
                ? await db.OrganizationMembers.AsNoTracking().FirstOrDefaultAsync(m =>
                    m.Id == memberId && m.OrganizationId == me.OrganizationId
                    && m.Status == MemberStatus.Active && m.UserId != null, ct)
                : null;
            if (assignee is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["assigneeMemberId"] = ["Choose an active member of your organisation."] });

            var to = new Assignee(assignee.UserId!, assignee.DisplayName, assignee.Role);
            command = action switch
            {
                WorkflowAction.AssignWarehouse => new AssignWarehouse(to),
                WorkflowAction.AssignDispatch => new AssignDispatch(to),
                _ => new Reassign(to),
            };
        }

        var now = clock.GetUtcNow();
        var outcome = Lifecycle.Apply(order, command, me.ToActor(), now);
        if (!outcome.Succeeded) return ToProblem(outcome.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Someone else updated this order at the same time. Refresh and try again.");
        }

        return Results.Ok(order.ToDetail(now, Available(order, me)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Row-level visibility: tenant isolation + what each role may see.</summary>
    internal static IQueryable<WorkOrder> VisibleTo(IQueryable<WorkOrder> query, MemberContext me)
    {
        query = query.Where(o => o.OrganizationId == me.OrganizationId);

        if (RoleSets.SeeAllOrders.Contains(me.Role)) return query;

        return me.Role switch
        {
            Roles.Requester => query.Where(o => o.RequesterUserId == me.UserId),
            Roles.WarehousePersonnel or Roles.DispatchPersonnel => query.Where(o => o.AssigneeUserId == me.UserId),
            _ => query.Where(_ => false),
        };
    }

    private static IReadOnlyList<string> Available(WorkOrder order, MemberContext me) =>
        Lifecycle.AvailableActions(order, me.ToActor()).Select(a => a.ToString()).ToList();

    private static IResult ToProblem(WorkflowError error) => error.Kind switch
    {
        WorkflowErrorKind.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: error.Message),
        WorkflowErrorKind.InvalidState => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: error.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message),
    };

    private static string? Trunc(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    /// <summary>
    /// Atomically issues the next reference for an organisation (DEL-yy-NNNNN).
    /// On Postgres a single UPDATE … RETURNING makes concurrent creates safe.
    /// </summary>
    private static async Task<string> NextNumberAsync(AppDbContext db, Guid organizationId, DateTimeOffset now, CancellationToken ct)
    {
        int seq;
        if (db.Database.IsNpgsql())
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE organizations SET "LastWorkOrderNumber" = "LastWorkOrderNumber" + 1
                WHERE "Id" = @id RETURNING "LastWorkOrderNumber"
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = organizationId;
            cmd.Parameters.Add(p);
            seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        else
        {
            var org = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);
            seq = ++org.LastWorkOrderNumber;
        }
        return $"DEL-{now:yy}-{seq:D5}";
    }
}
