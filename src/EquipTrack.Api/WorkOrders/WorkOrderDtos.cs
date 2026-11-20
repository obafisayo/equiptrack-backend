using EquipTrack.Api.Domain;

namespace EquipTrack.Api.WorkOrders;

// ── Requests ────────────────────────────────────────────────────────────────

public sealed record CreateWorkOrderItemRequest(string? Description, decimal Quantity, string? Unit, string? PartNumber, string? Plant, string? BinLocation);

public sealed record CreateWorkOrderRequest(
    string? RequestType,
    string? Urgency,
    string? Destination,
    DateOnly? RequiredDate,
    string? Entity,
    string? Notes,
    string? TrDepartment,
    List<CreateWorkOrderItemRequest>? Items);

/// <summary>
/// One endpoint for every lifecycle action. Only the fields the action needs are read.
/// <see cref="ExpectedStage"/> guards against acting on a stale screen.
/// </summary>
public sealed record WorkOrderActionRequest(
    string? Action,
    string? ExpectedStage,
    string? Reason,
    Guid? AssigneeMemberId,
    string? ContainerRef,
    string? WaybillNumber,
    string? VesselName);

// ── Responses ───────────────────────────────────────────────────────────────

public sealed record PersonDto(string UserId, string Name);

public sealed record SlaDto(string Status, string Elapsed, double ElapsedHours, double? SlaHours, double? PercentUsed, DateTimeOffset? DueAt);

public sealed record WorkOrderSummaryDto(
    Guid Id,
    string Number,
    string RequestType,
    string Urgency,
    string Destination,
    string Status,
    string Stage,
    string Department,
    DateTimeOffset StageStartedAt,
    SlaDto Sla,
    PersonDto? Assignee,
    string? AwaitingRole,
    string PendingActionBy,
    PersonDto Requester,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpectedDeliveryAt);

public sealed record WorkOrderItemDto(string Description, decimal Quantity, string Unit, string? PartNumber, string? Plant, string? BinLocation);

public sealed record WorkOrderEventDto(
    long Id, DateTimeOffset OccurredAt, string Action, string? FromStage, string? ToStage,
    PersonDto Actor, string ActorRole, PersonDto? Assignee, string? Note);

public sealed record WorkOrderDetailDto(
    WorkOrderSummaryDto Summary,
    string? Entity,
    string? Notes,
    DateOnly? RequiredDate,
    string? TrDepartment,
    string? ContainerRef,
    string? WaybillNumber,
    bool WaybillApproved,
    string? VesselName,
    string? RejectionReason,
    IReadOnlyList<WorkOrderItemDto> Items,
    IReadOnlyList<WorkOrderEventDto> History,
    IReadOnlyList<string> AvailableActions);

public static class WorkOrderMapping
{
    public static string ToApi(this RequestType t) => t switch
    {
        RequestType.Sap => "SAP",
        RequestType.Tr => "TR",
        RequestType.Vendor => "VENDOR",
        _ => "NON_STOCK",
    };

    public static bool TryParseRequestType(string? value, out RequestType type)
    {
        type = value switch
        {
            "SAP" => RequestType.Sap,
            "TR" => RequestType.Tr,
            "VENDOR" => RequestType.Vendor,
            "NON_STOCK" => RequestType.NonStock,
            _ => (RequestType)(-1),
        };
        return (int)type >= 0;
    }

    public static WorkOrderSummaryDto ToSummary(this WorkOrder o, DateTimeOffset now, IReadOnlyDictionary<Stage, double>? stageHours = null)
    {
        var sla = Sla.Evaluate(o, now, stageHours ?? Sla.StageHours);
        var awaiting = Lifecycle.AwaitingRole(o);
        var assignee = o.AssigneeUserId is null ? null : new PersonDto(o.AssigneeUserId, o.AssigneeName ?? "");

        // Named accountability: a person whenever the awaited role is the assignee's;
        // otherwise the role whose turn it is (never "delayed in warehouse").
        var assigneeActs = assignee is not null && awaiting is Roles.WarehousePersonnel or Roles.DispatchPersonnel;
        var pendingBy = o.Status != WorkOrderStatus.Active ? "-"
            : assigneeActs ? assignee!.Name
            : awaiting is null ? "-"
            : RoleLabel(awaiting);

        return new WorkOrderSummaryDto(
            o.Id, o.Number, o.RequestType.ToApi(), o.Urgency.ToString(), o.Destination,
            o.Status.ToString().ToLowerInvariant(), o.Stage.Name(), o.Stage.Department().ToString().ToLowerInvariant(),
            o.StageStartedAt,
            new SlaDto(sla.Status.ToString(), Sla.FormatElapsed(sla.Elapsed), Math.Round(sla.Elapsed.TotalHours, 2),
                sla.SlaHours, sla.PercentUsed, sla.DueAt),
            assignee, awaiting, pendingBy,
            new PersonDto(o.RequesterUserId, o.RequesterName),
            o.CreatedAt, Sla.ExpectedDeliveryAt(o));
    }

    public static WorkOrderDetailDto ToDetail(this WorkOrder o, DateTimeOffset now, IReadOnlyList<string> availableActions,
        IReadOnlyDictionary<Stage, double>? stageHours = null) => new(
        o.ToSummary(now, stageHours), o.Entity, o.Notes, o.RequiredDate, o.TrDepartment, o.ContainerRef,
        o.WaybillNumber, o.WaybillApproved, o.VesselName, o.RejectionReason,
        o.Items.Select(i => new WorkOrderItemDto(i.Description, i.Quantity, i.Unit, i.PartNumber, i.Plant, i.BinLocation)).ToList(),
        o.Events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Select(e => new WorkOrderEventDto(
            e.Id, e.OccurredAt, e.Action.ToString(), e.FromStage?.Name(), e.ToStage?.Name(),
            new PersonDto(e.ActorUserId, e.ActorName), e.ActorRole,
            e.AssigneeUserId is null ? null : new PersonDto(e.AssigneeUserId, e.AssigneeName ?? ""),
            e.Note)).ToList(),
        availableActions);

    private static string RoleLabel(string role) => role switch
    {
        Roles.WarehouseSupervisor => "Warehouse Supervisor",
        Roles.WarehousePersonnel => "Warehouse Personnel",
        Roles.DispatchSupervisor => "Dispatch Supervisor",
        Roles.DispatchPersonnel => "Dispatch Personnel",
        Roles.Qaqc => "QAQC Officer",
        Roles.Executive => "Executive",
        Roles.Logistics => "Vessel Coordinator",
        Roles.Requester => "Requester",
        _ => role,
    };
}
