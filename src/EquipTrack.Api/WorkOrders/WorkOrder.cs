namespace EquipTrack.Api.WorkOrders;

/// <summary>
/// A work order (equipment request). Current stage, assignee and stage start time
/// are denormalised here for fast queries; the authoritative history is the
/// append-only <see cref="Events"/> log. State changes go through
/// <see cref="Lifecycle"/> only — never set <see cref="Stage"/> directly.
/// </summary>
public class WorkOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant. Every query must be scoped by organisation.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Human reference, e.g. DEL-26-00042. Unique per organisation.</summary>
    public required string Number { get; set; }

    public RequestType RequestType { get; set; }
    public Urgency Urgency { get; set; }
    public required string Destination { get; set; }
    public string? Entity { get; set; }
    public string? Notes { get; set; }
    public DateOnly? RequiredDate { get; set; }
    public string? TrDepartment { get; set; }

    public required string RequesterUserId { get; set; }
    public required string RequesterName { get; set; }

    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Active;
    public Stage Stage { get; set; }
    /// <summary>When the order entered <see cref="Stage"/>. Elapsed time is computed from this.</summary>
    public DateTimeOffset StageStartedAt { get; set; }

    /// <summary>The named person currently accountable, when the stage has one.</summary>
    public string? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }

    public string? ContainerRef { get; set; }
    public string? WaybillNumber { get; set; }
    public bool WaybillApproved { get; set; }
    public string? VesselName { get; set; }
    public string? RejectionReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token (mapped to Postgres xmin).</summary>
    public uint Version { get; set; }

    public List<WorkOrderItem> Items { get; set; } = [];
    public List<WorkOrderEvent> Events { get; set; } = [];
}

public class WorkOrderItem
{
    public int Id { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public required string Unit { get; set; }
    public string? PartNumber { get; set; }
    public string? Plant { get; set; }
    public string? BinLocation { get; set; }
}

/// <summary>Everything that can happen to a work order. Each produces exactly one event.</summary>
public enum WorkflowAction
{
    Created,
    ApproveRequest,
    RejectRequest,
    AssignWarehouse,
    StartPicking,
    CreateGoodsIssue,
    TransferToDispatch,
    AssignDispatch,
    SendToPreloadQaqc,
    Containerize,
    SubmitForPostQaqc,
    PassPostQaqc,
    FailPostQaqc,
    GenerateWaybill,
    ApproveWaybill,
    RejectWaybill,
    RequestDeckspace,
    AllocateVessel,
    MarkShipped,
    ConfirmReceipt,
    Reassign,
    Reverse,
}

/// <summary>
/// Immutable audit record. Rows are only ever inserted — never updated or deleted —
/// so the history doubles as the compliance audit trail.
/// </summary>
public class WorkOrderEvent
{
    public long Id { get; set; }
    public Guid WorkOrderId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public WorkflowAction Action { get; set; }
    public Stage? FromStage { get; set; }
    public Stage? ToStage { get; set; }

    public required string ActorUserId { get; set; }
    public required string ActorName { get; set; }
    public required string ActorRole { get; set; }

    /// <summary>Accountable person after this event (null = nobody named yet).</summary>
    public string? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }

    /// <summary>Reason (rejections/reversals) or reference (waybill no., vessel, container).</summary>
    public string? Note { get; set; }
}
