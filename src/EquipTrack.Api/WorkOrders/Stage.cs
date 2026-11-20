namespace EquipTrack.Api.WorkOrders;

/// <summary>
/// The 16 lifecycle stages, in order. Display names MUST match <c>LIFECYCLE</c>
/// in the frontend's <c>src/lib/lifecycle.ts</c> (a contract test enforces this).
/// </summary>
public enum Stage
{
    PendingBaseCoordinatorApproval = 0,
    NewRequest = 1,
    WarehouseAssigned = 2,
    Picking = 3,
    GiCreated = 4,
    TransferredToDispatch = 5,
    DispatchQueue = 6,
    DispatchAssigned = 7,
    PreloadQaqc = 8,
    Containerization = 9,
    PostQaqc = 10,
    WaybillPendingSignature = 11,
    WaybillDone = 12,
    AwaitingDeckspace = 13,
    Shipped = 14,
    Completed = 15,
}

public enum Department { Pending, Warehouse, Dispatch, Qaqc, Final }

public static class Stages
{
    private static readonly Dictionary<Stage, string> Names = new()
    {
        [Stage.PendingBaseCoordinatorApproval] = "Pending Base Coordinator Approval",
        [Stage.NewRequest] = "New Request",
        [Stage.WarehouseAssigned] = "Warehouse Assigned",
        [Stage.Picking] = "Picking",
        [Stage.GiCreated] = "GI Created",
        [Stage.TransferredToDispatch] = "Transferred to Dispatch",
        [Stage.DispatchQueue] = "Dispatch Queue",
        [Stage.DispatchAssigned] = "Dispatch Assigned",
        [Stage.PreloadQaqc] = "Preload QAQC",
        [Stage.Containerization] = "Containerization",
        [Stage.PostQaqc] = "Post QAQC",
        [Stage.WaybillPendingSignature] = "Waybill Pending Signature",
        [Stage.WaybillDone] = "Waybill Done",
        [Stage.AwaitingDeckspace] = "Awaiting Deckspace",
        [Stage.Shipped] = "Shipped",
        [Stage.Completed] = "Completed",
    };

    private static readonly Dictionary<string, Stage> ByName =
        Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>All stages in lifecycle order.</summary>
    public static IReadOnlyList<Stage> Ordered { get; } = Enum.GetValues<Stage>().OrderBy(s => (int)s).ToList();

    public static string Name(this Stage stage) => Names[stage];

    public static bool TryParse(string? name, out Stage stage) =>
        ByName.TryGetValue(name ?? string.Empty, out stage);

    public static Department Department(this Stage stage) => stage switch
    {
        Stage.PendingBaseCoordinatorApproval => WorkOrders.Department.Pending,
        <= Stage.TransferredToDispatch => WorkOrders.Department.Warehouse,
        <= Stage.DispatchAssigned => WorkOrders.Department.Dispatch,
        <= Stage.PostQaqc => WorkOrders.Department.Qaqc,
        _ => WorkOrders.Department.Final,
    };
}

public enum RequestType { Sap, Tr, Vendor, NonStock }

public enum Urgency { Low, Medium, High, Urgent }

public enum WorkOrderStatus { Active, Rejected, Completed }
