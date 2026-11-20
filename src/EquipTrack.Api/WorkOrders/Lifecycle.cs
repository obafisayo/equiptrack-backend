using EquipTrack.Api.Domain;

namespace EquipTrack.Api.WorkOrders;

/// <summary>Who is performing an action. Role is a canonical code from <see cref="Roles"/>.</summary>
public sealed record Actor(string UserId, string Name, string Role);

/// <summary>A person being assigned work. Role is checked against the department.</summary>
public sealed record Assignee(string UserId, string Name, string Role);

public enum WorkflowErrorKind
{
    /// <summary>The actor's role (or assignment) does not permit this action. → 403</summary>
    Forbidden,
    /// <summary>The order is not in a state where this action applies. → 409</summary>
    InvalidState,
    /// <summary>The command itself is malformed (missing reason, wrong assignee role). → 400</summary>
    Invalid,
}

public sealed record WorkflowError(WorkflowErrorKind Kind, string Message);

public sealed class WorkflowResult
{
    public WorkOrderEvent? Event { get; private init; }
    public WorkflowError? Error { get; private init; }
    public bool Succeeded => Error is null;

    public static WorkflowResult Ok(WorkOrderEvent e) => new() { Event = e };
    public static WorkflowResult Fail(WorkflowErrorKind kind, string message) => new() { Error = new(kind, message) };
}

// ── Commands ────────────────────────────────────────────────────────────────

public abstract record WorkflowCommand(WorkflowAction Action);

public sealed record ApproveRequest() : WorkflowCommand(WorkflowAction.ApproveRequest);
public sealed record RejectRequest(string Reason) : WorkflowCommand(WorkflowAction.RejectRequest);
public sealed record AssignWarehouse(Assignee To) : WorkflowCommand(WorkflowAction.AssignWarehouse);
public sealed record StartPicking() : WorkflowCommand(WorkflowAction.StartPicking);
public sealed record CreateGoodsIssue() : WorkflowCommand(WorkflowAction.CreateGoodsIssue);
public sealed record TransferToDispatch() : WorkflowCommand(WorkflowAction.TransferToDispatch);
public sealed record AcknowledgeHandover() : WorkflowCommand(WorkflowAction.AcknowledgeHandover);
public sealed record AssignDispatch(Assignee To) : WorkflowCommand(WorkflowAction.AssignDispatch);
public sealed record SendToPreloadQaqc() : WorkflowCommand(WorkflowAction.SendToPreloadQaqc);
public sealed record Containerize(string ContainerRef) : WorkflowCommand(WorkflowAction.Containerize);
public sealed record SubmitForPostQaqc() : WorkflowCommand(WorkflowAction.SubmitForPostQaqc);
public sealed record PassPostQaqc() : WorkflowCommand(WorkflowAction.PassPostQaqc);
public sealed record FailPostQaqc(string Reason) : WorkflowCommand(WorkflowAction.FailPostQaqc);
public sealed record GenerateWaybill(string WaybillNumber) : WorkflowCommand(WorkflowAction.GenerateWaybill);
public sealed record ApproveWaybill() : WorkflowCommand(WorkflowAction.ApproveWaybill);
public sealed record RejectWaybill(string Reason) : WorkflowCommand(WorkflowAction.RejectWaybill);
public sealed record RequestDeckspace() : WorkflowCommand(WorkflowAction.RequestDeckspace);
public sealed record AllocateVessel(string VesselName) : WorkflowCommand(WorkflowAction.AllocateVessel);
public sealed record MarkShipped() : WorkflowCommand(WorkflowAction.MarkShipped);
public sealed record ConfirmReceipt() : WorkflowCommand(WorkflowAction.ConfirmReceipt);
public sealed record Reassign(Assignee To) : WorkflowCommand(WorkflowAction.Reassign);
public sealed record Reverse(string Reason) : WorkflowCommand(WorkflowAction.Reverse);

public sealed record CreateWorkOrder(
    RequestType RequestType,
    Urgency Urgency,
    string Destination,
    DateOnly? RequiredDate,
    string? Entity,
    string? Notes,
    string? TrDepartment,
    IReadOnlyList<WorkOrderItem> Items);

/// <summary>
/// The explicit lifecycle state machine. Pure logic: no database, no clock —
/// callers pass <c>now</c>. Every permitted action is listed in <see cref="Rules"/>;
/// anything not listed is rejected.
/// </summary>
public static class Lifecycle
{
    public const int MaxTextLength = 500;

    /// <param name="From">Stages the action may start from.</param>
    /// <param name="To">Resulting stage, or null when the action does not move the stage.</param>
    /// <param name="Roles">Roles allowed to perform it.</param>
    /// <param name="PersonnelMustBeAssignee">wh_per / dsp_per may act only on orders assigned to them.</param>
    public sealed record Rule(Stage[] From, Stage? To, string[] Roles, bool PersonnelMustBeAssignee = false);

    private static readonly Stage[] WarehouseAssignedStages = [Stage.WarehouseAssigned, Stage.Picking, Stage.GiCreated];

    private static readonly Stage[] DispatchAssignedStages =
    [
        Stage.DispatchAssigned, Stage.PreloadQaqc, Stage.Containerization, Stage.PostQaqc,
        Stage.WaybillPendingSignature, Stage.WaybillDone, Stage.AwaitingDeckspace,
    ];

    public static readonly IReadOnlyDictionary<WorkflowAction, Rule> Rules = new Dictionary<WorkflowAction, Rule>
    {
        // Base Coordinator approval (TR requests).
        [WorkflowAction.ApproveRequest] = new([Stage.PendingBaseCoordinatorApproval], Stage.NewRequest, [Roles.Executive]),
        [WorkflowAction.RejectRequest] = new([Stage.PendingBaseCoordinatorApproval], null, [Roles.Executive]),

        // Warehouse.
        [WorkflowAction.AssignWarehouse] = new([Stage.NewRequest], Stage.WarehouseAssigned, [Roles.WarehouseSupervisor]),
        [WorkflowAction.StartPicking] = new([Stage.WarehouseAssigned], Stage.Picking,
            [Roles.WarehousePersonnel, Roles.WarehouseSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.CreateGoodsIssue] = new([Stage.Picking], Stage.GiCreated,
            [Roles.WarehousePersonnel, Roles.WarehouseSupervisor], PersonnelMustBeAssignee: true),
        // Physical handover: warehouse marks the goods transferred (and stays accountable),
        // then dispatch confirms receipt. The gap between the two is the handover delay.
        [WorkflowAction.TransferToDispatch] = new([Stage.GiCreated], Stage.TransferredToDispatch,
            [Roles.WarehousePersonnel, Roles.WarehouseSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.AcknowledgeHandover] = new([Stage.TransferredToDispatch], Stage.DispatchQueue,
            [Roles.DispatchSupervisor, Roles.DispatchPersonnel]),

        // Dispatch + QAQC.
        [WorkflowAction.AssignDispatch] = new([Stage.DispatchQueue], Stage.DispatchAssigned, [Roles.DispatchSupervisor]),
        [WorkflowAction.SendToPreloadQaqc] = new([Stage.DispatchAssigned], Stage.PreloadQaqc,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.Containerize] = new([Stage.PreloadQaqc], Stage.Containerization,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.SubmitForPostQaqc] = new([Stage.Containerization], Stage.PostQaqc,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.PassPostQaqc] = new([Stage.PostQaqc], Stage.WaybillPendingSignature, [Roles.Qaqc]),
        [WorkflowAction.FailPostQaqc] = new([Stage.PostQaqc], Stage.Containerization, [Roles.Qaqc]),

        // Waybill + shipping.
        [WorkflowAction.GenerateWaybill] = new([Stage.WaybillPendingSignature], null,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.ApproveWaybill] = new([Stage.WaybillPendingSignature], Stage.WaybillDone, [Roles.Executive]),
        [WorkflowAction.RejectWaybill] = new([Stage.WaybillPendingSignature], null, [Roles.Executive]),
        [WorkflowAction.RequestDeckspace] = new([Stage.WaybillDone], Stage.AwaitingDeckspace,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.AllocateVessel] = new([Stage.AwaitingDeckspace], null, [Roles.Logistics, Roles.DispatchSupervisor]),
        [WorkflowAction.MarkShipped] = new([Stage.AwaitingDeckspace], Stage.Shipped,
            [Roles.DispatchPersonnel, Roles.DispatchSupervisor], PersonnelMustBeAssignee: true),
        [WorkflowAction.ConfirmReceipt] = new([Stage.Shipped], Stage.Completed, [Roles.Requester]),

        // Supervisor corrections. Target stage is resolved per case below.
        [WorkflowAction.Reassign] = new([.. WarehouseAssignedStages, .. DispatchAssignedStages], null,
            [Roles.WarehouseSupervisor, Roles.DispatchSupervisor]),
        [WorkflowAction.Reverse] = new([.. ReversalTargets().Keys], null,
            [Roles.WarehouseSupervisor, Roles.DispatchSupervisor]),
    };

    /// <summary>Mirrors STAGE_REVERSAL in the frontend's lifecycle.ts.</summary>
    public static IReadOnlyDictionary<Stage, Stage> ReversalTargets() => new Dictionary<Stage, Stage>
    {
        [Stage.WarehouseAssigned] = Stage.NewRequest,
        [Stage.Picking] = Stage.WarehouseAssigned,
        [Stage.GiCreated] = Stage.Picking,
        [Stage.TransferredToDispatch] = Stage.GiCreated,
        [Stage.DispatchAssigned] = Stage.DispatchQueue,
        [Stage.PreloadQaqc] = Stage.DispatchAssigned,
        [Stage.Containerization] = Stage.PreloadQaqc,
        [Stage.PostQaqc] = Stage.Containerization,
        [Stage.WaybillPendingSignature] = Stage.PostQaqc,
    };

    /// <summary>Stage a new request starts in, by request type (matches the frontend's initialStage).</summary>
    public static Stage InitialStage(RequestType type) => type switch
    {
        RequestType.Tr => Stage.PendingBaseCoordinatorApproval,
        RequestType.Vendor or RequestType.NonStock => Stage.DispatchQueue,
        _ => Stage.NewRequest,
    };

    /// <summary>The role whose turn it is, for "Pending action by …" and SLA alerts.</summary>
    public static string? AwaitingRole(WorkOrder order) => order.Status != WorkOrderStatus.Active ? null : order.Stage switch
    {
        Stage.PendingBaseCoordinatorApproval => Roles.Executive,
        Stage.NewRequest => Roles.WarehouseSupervisor,
        Stage.WarehouseAssigned or Stage.Picking or Stage.GiCreated => Roles.WarehousePersonnel,
        Stage.TransferredToDispatch => Roles.DispatchSupervisor,
        Stage.DispatchQueue => Roles.DispatchSupervisor,
        Stage.PostQaqc => Roles.Qaqc,
        Stage.WaybillPendingSignature => order.WaybillNumber is null ? Roles.DispatchPersonnel : Roles.Executive,
        Stage.AwaitingDeckspace => order.VesselName is null ? Roles.Logistics : Roles.DispatchPersonnel,
        Stage.Shipped => Roles.Requester,
        Stage.Completed => null,
        _ => Roles.DispatchPersonnel,
    };

    /// <summary>
    /// Actions this actor could attempt right now (role, stage and assignment checks).
    /// Used to show only relevant buttons; <see cref="Apply"/> still re-checks everything.
    /// </summary>
    public static IReadOnlyList<WorkflowAction> AvailableActions(WorkOrder order, Actor actor)
    {
        if (order.Status != WorkOrderStatus.Active) return [];
        return Rules
            .Where(kv => kv.Value.Roles.Contains(actor.Role)
                         && kv.Value.From.Contains(order.Stage)
                         && !(kv.Value.PersonnelMustBeAssignee && IsPersonnel(actor.Role) && order.AssigneeUserId != actor.UserId))
            .Select(kv => kv.Key)
            .Where(a => a switch
            {
                WorkflowAction.ConfirmReceipt => order.RequesterUserId == actor.UserId,
                WorkflowAction.GenerateWaybill => order.WaybillNumber is null,
                WorkflowAction.ApproveWaybill or WorkflowAction.RejectWaybill => order.WaybillNumber is not null,
                WorkflowAction.MarkShipped => order.VesselName is not null,
                WorkflowAction.Reassign => actor.Role == (WarehouseAssignedStages.Contains(order.Stage)
                    ? Roles.WarehouseSupervisor : Roles.DispatchSupervisor),
                WorkflowAction.Reverse => actor.Role == (order.Stage.Department() == Department.Warehouse
                    ? Roles.WarehouseSupervisor : Roles.DispatchSupervisor),
                _ => true,
            })
            .OrderBy(a => a)
            .ToList();
    }

    // ── Create ──────────────────────────────────────────────────────────────

    public static (WorkOrder? Order, WorkflowError? Error) Create(
        CreateWorkOrder cmd, Actor actor, Guid organizationId, string number, DateTimeOffset now)
    {
        if (actor.Role != Roles.Requester)
            return (null, new(WorkflowErrorKind.Forbidden, "Only requesters can submit work orders."));
        if (string.IsNullOrWhiteSpace(cmd.Destination) || cmd.Destination.Length > 200)
            return (null, new(WorkflowErrorKind.Invalid, "Destination is required (max 200 characters)."));
        if (cmd.Items.Count == 0)
            return (null, new(WorkflowErrorKind.Invalid, "At least one item is required."));
        if (cmd.Items.Any(i => string.IsNullOrWhiteSpace(i.Description) || i.Quantity <= 0 || string.IsNullOrWhiteSpace(i.Unit)))
            return (null, new(WorkflowErrorKind.Invalid, "Every item needs a description, unit and a quantity above zero."));
        if (cmd.Notes is { Length: > 2000 })
            return (null, new(WorkflowErrorKind.Invalid, "Notes must be at most 2000 characters."));

        var stage = InitialStage(cmd.RequestType);
        var order = new WorkOrder
        {
            OrganizationId = organizationId,
            Number = number,
            RequestType = cmd.RequestType,
            Urgency = cmd.Urgency,
            Destination = cmd.Destination.Trim(),
            RequiredDate = cmd.RequiredDate,
            Entity = cmd.Entity?.Trim(),
            Notes = cmd.Notes?.Trim(),
            TrDepartment = cmd.TrDepartment?.Trim(),
            RequesterUserId = actor.UserId,
            RequesterName = actor.Name,
            Stage = stage,
            StageStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Items = cmd.Items.ToList(),
        };
        order.Events.Add(NewEvent(order, WorkflowAction.Created, actor, null, stage, now, null));
        return (order, null);
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    public static WorkflowResult Apply(WorkOrder order, WorkflowCommand cmd, Actor actor, DateTimeOffset now)
    {
        if (!Rules.TryGetValue(cmd.Action, out var rule))
            return WorkflowResult.Fail(WorkflowErrorKind.Invalid, $"Unknown action {cmd.Action}.");

        if (order.Status != WorkOrderStatus.Active)
            return WorkflowResult.Fail(WorkflowErrorKind.InvalidState, $"Order {order.Number} is {order.Status.ToString().ToLowerInvariant()}.");

        if (!rule.Roles.Contains(actor.Role))
            return WorkflowResult.Fail(WorkflowErrorKind.Forbidden, $"Role '{actor.Role}' cannot perform {cmd.Action}.");

        if (!rule.From.Contains(order.Stage))
            return WorkflowResult.Fail(WorkflowErrorKind.InvalidState,
                $"{cmd.Action} is not allowed while the order is at '{order.Stage.Name()}'.");

        if (rule.PersonnelMustBeAssignee && IsPersonnel(actor.Role) && order.AssigneeUserId != actor.UserId)
            return WorkflowResult.Fail(WorkflowErrorKind.Forbidden, "This order is not assigned to you.");

        var from = order.Stage;
        var to = rule.To;
        string? note = null;

        switch (cmd)
        {
            case RejectRequest r:
                if (Text(r.Reason) is not { } reason) return ReasonRequired();
                order.Status = WorkOrderStatus.Rejected;
                order.RejectionReason = note = reason;
                break;

            case AssignWarehouse a:
                if (CheckAssignee(a.To, Roles.WarehousePersonnel) is { } e1) return e1;
                SetAssignee(order, a.To);
                break;

            case AssignDispatch a:
                if (CheckAssignee(a.To, Roles.DispatchPersonnel) is { } e2) return e2;
                SetAssignee(order, a.To);
                break;

            case AcknowledgeHandover:
                ClearAssignee(order); // warehouse is released; dispatch supervisor assigns from the queue
                break;

            case Containerize c:
                if (Text(c.ContainerRef, 100) is not { } container)
                    return WorkflowResult.Fail(WorkflowErrorKind.Invalid, "A container reference is required.");
                order.ContainerRef = note = container;
                break;

            case FailPostQaqc f:
                if (Text(f.Reason) is not { } failReason) return ReasonRequired();
                note = failReason;
                break;

            case GenerateWaybill g:
                if (order.WaybillNumber is not null)
                    return WorkflowResult.Fail(WorkflowErrorKind.InvalidState, "A waybill already exists and is awaiting approval.");
                if (Text(g.WaybillNumber, 50) is not { } waybill)
                    return WorkflowResult.Fail(WorkflowErrorKind.Invalid, "A waybill number is required.");
                order.WaybillNumber = note = waybill;
                order.WaybillApproved = false;
                break;

            case ApproveWaybill:
                if (order.WaybillNumber is null)
                    return WorkflowResult.Fail(WorkflowErrorKind.InvalidState, "No waybill has been generated yet.");
                order.WaybillApproved = true;
                note = order.WaybillNumber;
                break;

            case RejectWaybill rw:
                if (order.WaybillNumber is null)
                    return WorkflowResult.Fail(WorkflowErrorKind.InvalidState, "No waybill has been generated yet.");
                if (Text(rw.Reason) is not { } wbReason) return ReasonRequired();
                order.WaybillNumber = null;
                order.WaybillApproved = false;
                note = wbReason;
                break;

            case AllocateVessel v:
                if (Text(v.VesselName, 100) is not { } vessel)
                    return WorkflowResult.Fail(WorkflowErrorKind.Invalid, "A vessel name is required.");
                order.VesselName = note = vessel;
                break;

            case MarkShipped:
                if (order.VesselName is null)
                    return WorkflowResult.Fail(WorkflowErrorKind.InvalidState, "Allocate a vessel before marking the order shipped.");
                note = order.VesselName;
                break;

            case ConfirmReceipt:
                if (order.RequesterUserId != actor.UserId)
                    return WorkflowResult.Fail(WorkflowErrorKind.Forbidden, "Only the original requester can confirm receipt.");
                order.Status = WorkOrderStatus.Completed;
                break;

            case Reassign ra:
            {
                var warehouse = WarehouseAssignedStages.Contains(order.Stage);
                var (supervisor, personnel) = warehouse
                    ? (Roles.WarehouseSupervisor, Roles.WarehousePersonnel)
                    : (Roles.DispatchSupervisor, Roles.DispatchPersonnel);
                if (actor.Role != supervisor)
                    return WorkflowResult.Fail(WorkflowErrorKind.Forbidden, $"Only a {supervisor} can reassign at this stage.");
                if (CheckAssignee(ra.To, personnel) is { } e3) return e3;
                SetAssignee(order, ra.To);
                break;
            }

            case Reverse rv:
            {
                if (Text(rv.Reason) is not { } revReason) return ReasonRequired();
                var supervisor = order.Stage.Department() == Department.Warehouse ? Roles.WarehouseSupervisor : Roles.DispatchSupervisor;
                if (actor.Role != supervisor)
                    return WorkflowResult.Fail(WorkflowErrorKind.Forbidden, $"Only a {supervisor} can reverse this stage.");
                to = ReversalTargets()[order.Stage];
                if (to is Stage.NewRequest or Stage.DispatchQueue) ClearAssignee(order);
                if (order.Stage == Stage.WaybillPendingSignature)
                {
                    order.WaybillNumber = null;
                    order.WaybillApproved = false;
                }
                note = revReason;
                break;
            }
        }

        if (to is { } next && next != order.Stage)
        {
            order.Stage = next;
            order.StageStartedAt = now; // elapsed time restarts only when the stage really changes
        }
        order.UpdatedAt = now;

        var evt = NewEvent(order, cmd.Action, actor, from, to, now, note);
        order.Events.Add(evt);
        return WorkflowResult.Ok(evt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsPersonnel(string role) =>
        role is Roles.WarehousePersonnel or Roles.DispatchPersonnel;

    private static WorkflowResult? CheckAssignee(Assignee to, string requiredRole)
    {
        if (string.IsNullOrWhiteSpace(to.UserId) || string.IsNullOrWhiteSpace(to.Name))
            return WorkflowResult.Fail(WorkflowErrorKind.Invalid, "An assignee is required.");
        if (to.Role != requiredRole)
            return WorkflowResult.Fail(WorkflowErrorKind.Invalid, $"Assignee must be {requiredRole}, not {to.Role}.");
        return null;
    }

    private static void SetAssignee(WorkOrder order, Assignee to)
    {
        order.AssigneeUserId = to.UserId;
        order.AssigneeName = to.Name.Trim();
    }

    private static void ClearAssignee(WorkOrder order)
    {
        order.AssigneeUserId = null;
        order.AssigneeName = null;
    }

    private static string? Text(string? value, int max = MaxTextLength) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > max ? null : value.Trim();

    private static WorkflowResult ReasonRequired() =>
        WorkflowResult.Fail(WorkflowErrorKind.Invalid, $"A reason is required (max {MaxTextLength} characters).");

    private static WorkOrderEvent NewEvent(
        WorkOrder order, WorkflowAction action, Actor actor, Stage? from, Stage? to, DateTimeOffset now, string? note) => new()
    {
        WorkOrderId = order.Id,
        OccurredAt = now,
        Action = action,
        FromStage = from,
        ToStage = to,
        ActorUserId = actor.UserId,
        ActorName = actor.Name,
        ActorRole = actor.Role,
        AssigneeUserId = order.AssigneeUserId,
        AssigneeName = order.AssigneeName,
        Note = note,
    };
}
