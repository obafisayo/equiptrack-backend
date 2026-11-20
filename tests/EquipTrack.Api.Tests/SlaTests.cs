using EquipTrack.Api.WorkOrders;

namespace EquipTrack.Api.Tests;

public sealed class SlaTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static WorkOrder Order(Stage stage, Urgency urgency = Urgency.High) => new()
    {
        Number = "DEL-1",
        Destination = "Site",
        RequesterUserId = "u",
        RequesterName = "U",
        Stage = stage,
        StageStartedAt = Start,
        CreatedAt = Start,
        Urgency = urgency,
    };

    [Theory]
    [InlineData(0, SlaStatus.OnTrack)]
    [InlineData(2.9, SlaStatus.OnTrack)]   // New Request SLA = 4h; 75% = 3h
    [InlineData(3.0, SlaStatus.Warning)]
    [InlineData(4.0, SlaStatus.Warning)]   // exactly at SLA is not yet breached
    [InlineData(4.01, SlaStatus.Breached)]
    public void Status_follows_elapsed_time_against_stage_sla(double hours, SlaStatus expected)
    {
        var sla = Sla.Evaluate(Order(Stage.NewRequest), Start.AddHours(hours));
        Assert.Equal(expected, sla.Status);
        Assert.Equal(4, sla.SlaHours);
        Assert.Equal(Start.AddHours(4), sla.DueAt);
    }

    [Fact]
    public void Elapsed_is_computed_from_the_stage_timestamp()
    {
        var sla = Sla.Evaluate(Order(Stage.Picking), Start.AddHours(6).AddMinutes(32));
        Assert.Equal(TimeSpan.FromMinutes(6 * 60 + 32), sla.Elapsed);
        Assert.Equal("6h 32m", Sla.FormatElapsed(sla.Elapsed));
    }

    [Fact]
    public void Stages_without_an_sla_and_closed_orders_are_not_tracked()
    {
        Assert.Equal(SlaStatus.NotTracked, Sla.Evaluate(Order(Stage.Shipped), Start.AddDays(3)).Status);

        var done = Order(Stage.NewRequest);
        done.Status = WorkOrderStatus.Completed;
        Assert.Equal(SlaStatus.NotTracked, Sla.Evaluate(done, Start.AddDays(3)).Status);
    }

    [Theory]
    [InlineData(Urgency.Low, 7)]
    [InlineData(Urgency.Medium, 5)]
    [InlineData(Urgency.High, 3)]
    public void Expected_delivery_follows_urgency(Urgency urgency, int days) =>
        Assert.Equal(Start.AddDays(days), Sla.ExpectedDeliveryAt(Order(Stage.NewRequest, urgency)));

    [Fact]
    public void Urgent_has_no_fixed_delivery_date() =>
        Assert.Null(Sla.ExpectedDeliveryAt(Order(Stage.NewRequest, Urgency.Urgent)));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1h 0m")]
    [InlineData(392, "6h 32m")]
    [InlineData(1500, "1d 1h 0m")]
    public void Elapsed_format_is_hours_and_minutes(int minutes, string expected) =>
        Assert.Equal(expected, Sla.FormatElapsed(TimeSpan.FromMinutes(minutes)));
}
