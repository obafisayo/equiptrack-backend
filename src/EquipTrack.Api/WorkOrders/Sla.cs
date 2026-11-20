namespace EquipTrack.Api.WorkOrders;

public enum SlaStatus { OnTrack, Warning, Breached, NotTracked }

public sealed record StageSla(
    Stage Stage,
    TimeSpan Elapsed,
    double? SlaHours,
    double? PercentUsed,
    SlaStatus Status,
    DateTimeOffset? DueAt);

/// <summary>
/// SLA maths. Elapsed time is ALWAYS computed from stored timestamps
/// (<see cref="WorkOrder.StageStartedAt"/>), never from counters, so it cannot
/// drift or freeze and every figure can be audited against the event log.
/// Values mirror src/config/sla.ts in the frontend.
/// </summary>
public static class Sla
{
    public const double WarningThreshold = 0.75;

    public static readonly IReadOnlyDictionary<Stage, double> StageHours = new Dictionary<Stage, double>
    {
        [Stage.PendingBaseCoordinatorApproval] = 8,
        [Stage.NewRequest] = 4,
        [Stage.WarehouseAssigned] = 2,
        [Stage.Picking] = 8,
        [Stage.GiCreated] = 2,
        [Stage.TransferredToDispatch] = 1,
        [Stage.DispatchQueue] = 2,
        [Stage.DispatchAssigned] = 4,
        [Stage.PreloadQaqc] = 2,
        [Stage.Containerization] = 3,
        [Stage.PostQaqc] = 2,
        [Stage.WaybillPendingSignature] = 4,
        [Stage.WaybillDone] = 1,
        [Stage.AwaitingDeckspace] = 24,
        // Shipped and Completed have no stage SLA.
    };

    /// <summary>Delivery promise by urgency. Urgent = next boat (no fixed day count).</summary>
    public static int? UrgencyDays(Urgency urgency) => urgency switch
    {
        Urgency.Low => 7,
        Urgency.Medium => 5,
        Urgency.High => 3,
        _ => null,
    };

    public static StageSla Evaluate(WorkOrder order, DateTimeOffset now)
    {
        var elapsed = now - order.StageStartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (order.Status != WorkOrderStatus.Active || !StageHours.TryGetValue(order.Stage, out var hours))
            return new StageSla(order.Stage, elapsed, null, null, SlaStatus.NotTracked, null);

        var used = elapsed.TotalHours / hours;
        var status = used > 1 ? SlaStatus.Breached
            : used >= WarningThreshold ? SlaStatus.Warning
            : SlaStatus.OnTrack;

        return new StageSla(order.Stage, elapsed, hours, Math.Round(used * 100, 1), status,
            order.StageStartedAt.AddHours(hours));
    }

    public static DateTimeOffset? ExpectedDeliveryAt(WorkOrder order) =>
        UrgencyDays(order.Urgency) is { } days ? order.CreatedAt.AddDays(days) : null;

    /// <summary>Formats elapsed time the way the product mandates: "6h 32m", never "6.53 hours".</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var totalMinutes = (long)Math.Floor(elapsed.TotalMinutes);
        if (totalMinutes < 60) return $"{totalMinutes}m";
        var days = totalMinutes / (60 * 24);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        return days > 0 ? $"{days}d {hours}h {minutes}m" : $"{hours}h {minutes}m";
    }
}
