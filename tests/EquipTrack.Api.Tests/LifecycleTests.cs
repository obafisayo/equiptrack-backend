using EquipTrack.Api.Domain;
using EquipTrack.Api.WorkOrders;

namespace EquipTrack.Api.Tests;

public sealed class LifecycleTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private DateTimeOffset _now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly Actor Requester = new("u-req", "Amara Chukwu", Roles.Requester);
    private static readonly Actor OtherRequester = new("u-req2", "Bola Ade", Roles.Requester);
    private static readonly Actor WhSup = new("u-whs", "Yinka Adeyemi", Roles.WarehouseSupervisor);
    private static readonly Actor WhPer = new("u-whp", "Emeka Okonkwo", Roles.WarehousePersonnel);
    private static readonly Actor OtherWhPer = new("u-whp2", "Ngozi Eze", Roles.WarehousePersonnel);
    private static readonly Actor DspSup = new("u-dss", "Chika Obi", Roles.DispatchSupervisor);
    private static readonly Actor DspPer = new("u-dsp", "Tunde Bello", Roles.DispatchPersonnel);
    private static readonly Actor Qaqc = new("u-qa", "Femi Emmanuel", Roles.Qaqc);
    private static readonly Actor Exec = new("u-ex", "Director Ops", Roles.Executive);
    private static readonly Actor Logistics = new("u-log", "Danjuma Yusuf", Roles.Logistics);

    private static Assignee As(Actor a) => new(a.UserId, a.Name, a.Role);

    private WorkOrder NewOrder(RequestType type = RequestType.Sap, Urgency urgency = Urgency.High)
    {
        var (order, error) = Lifecycle.Create(
            new CreateWorkOrder(type, urgency, "Offshore Site A", null, "FOPS", null, null,
                [new WorkOrderItem { Description = "Drill pipe 5in", Quantity = 12, Unit = "EA" }]),
            Requester, Org, "DEL-26-00001", _now);
        Assert.Null(error);
        return order!;
    }

    private WorkOrderEvent Do(WorkOrder order, WorkflowCommand cmd, Actor actor, double advanceHours = 1)
    {
        _now = _now.AddHours(advanceHours);
        var result = Lifecycle.Apply(order, cmd, actor, _now);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Event!;
    }

    private WorkflowError Refused(WorkOrder order, WorkflowCommand cmd, Actor actor)
    {
        var result = Lifecycle.Apply(order, cmd, actor, _now);
        Assert.False(result.Succeeded);
        return result.Error!;
    }

    [Fact]
    public void Full_sap_journey_reaches_completed_with_a_complete_audit_trail()
    {
        var o = NewOrder();
        Assert.Equal(Stage.NewRequest, o.Stage);

        Do(o, new AssignWarehouse(As(WhPer)), WhSup);
        Do(o, new StartPicking(), WhPer);
        Do(o, new CreateGoodsIssue(), WhPer);
        Do(o, new TransferToDispatch(), WhPer);
        Assert.Equal(Stage.TransferredToDispatch, o.Stage);
        Assert.Equal(WhPer.UserId, o.AssigneeUserId); // warehouse stays accountable until receipt
        Do(o, new AcknowledgeHandover(), DspSup);
        Assert.Equal(Stage.DispatchQueue, o.Stage);
        Assert.Null(o.AssigneeUserId);

        Do(o, new AssignDispatch(As(DspPer)), DspSup);
        Do(o, new SendToPreloadQaqc(), DspPer);
        Do(o, new Containerize("CCU-20-0042"), DspPer);
        Do(o, new SubmitForPostQaqc(), DspPer);
        Do(o, new PassPostQaqc(), Qaqc);
        Do(o, new GenerateWaybill("WB-26-2001"), DspPer);
        Do(o, new ApproveWaybill(), Exec);
        Do(o, new RequestDeckspace(), DspPer);
        Do(o, new AllocateVessel("MV Sea Falcon"), Logistics);
        Do(o, new MarkShipped(), DspPer);
        Do(o, new ConfirmReceipt(), Requester);

        Assert.Equal(Stage.Completed, o.Stage);
        Assert.Equal(WorkOrderStatus.Completed, o.Status);
        Assert.Equal("CCU-20-0042", o.ContainerRef);
        Assert.True(o.WaybillApproved);

        // 1 Created + 16 actions, each with a named actor and timestamp.
        Assert.Equal(17, o.Events.Count);
        Assert.All(o.Events, e => Assert.False(string.IsNullOrEmpty(e.ActorName)));
        Assert.Equal(o.Events.OrderBy(e => e.OccurredAt).Select(e => e.Action), o.Events.Select(e => e.Action));
    }

    [Theory]
    [InlineData(RequestType.Sap, Stage.NewRequest)]
    [InlineData(RequestType.Tr, Stage.PendingBaseCoordinatorApproval)]
    [InlineData(RequestType.Vendor, Stage.DispatchQueue)]
    [InlineData(RequestType.NonStock, Stage.DispatchQueue)]
    public void Initial_stage_depends_on_request_type(RequestType type, Stage expected) =>
        Assert.Equal(expected, NewOrder(type).Stage);

    [Fact]
    public void Only_requesters_can_create_orders()
    {
        var (order, error) = Lifecycle.Create(
            new CreateWorkOrder(RequestType.Sap, Urgency.Low, "Site", null, null, null, null,
                [new WorkOrderItem { Description = "x", Quantity = 1, Unit = "EA" }]),
            WhSup, Org, "DEL-26-00002", _now);
        Assert.Null(order);
        Assert.Equal(WorkflowErrorKind.Forbidden, error!.Kind);
    }

    [Fact]
    public void Create_rejects_orders_without_valid_items()
    {
        var (_, error) = Lifecycle.Create(
            new CreateWorkOrder(RequestType.Sap, Urgency.Low, "Site", null, null, null, null,
                [new WorkOrderItem { Description = "x", Quantity = 0, Unit = "EA" }]),
            Requester, Org, "DEL-26-00003", _now);
        Assert.Equal(WorkflowErrorKind.Invalid, error!.Kind);
    }

    [Fact]
    public void Tr_request_can_be_approved_or_rejected_by_executive_only()
    {
        var approved = NewOrder(RequestType.Tr);
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(approved, new ApproveRequest(), WhSup).Kind);
        Do(approved, new ApproveRequest(), Exec);
        Assert.Equal(Stage.NewRequest, approved.Stage);

        var rejected = NewOrder(RequestType.Tr);
        Assert.Equal(WorkflowErrorKind.Invalid, Refused(rejected, new RejectRequest(" "), Exec).Kind);
        Do(rejected, new RejectRequest("Not budgeted"), Exec);
        Assert.Equal(WorkOrderStatus.Rejected, rejected.Status);
        Assert.Equal("Not budgeted", rejected.RejectionReason);

        // A rejected order is closed to every action.
        Assert.Equal(WorkflowErrorKind.InvalidState, Refused(rejected, new ApproveRequest(), Exec).Kind);
    }

    [Fact]
    public void Handover_waits_for_dispatch_and_can_be_reversed_by_warehouse()
    {
        var o = ToStage(Stage.TransferredToDispatch);
        Assert.Equal(Roles.DispatchSupervisor, Lifecycle.AwaitingRole(o));
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new AcknowledgeHandover(), WhPer).Kind);

        // Wrong goods sent: warehouse supervisor pulls it back, original picker still owns it.
        Do(o, new Reverse("Wrong bin"), WhSup);
        Assert.Equal(Stage.GiCreated, o.Stage);
        Assert.Equal(WhPer.UserId, o.AssigneeUserId);

        Do(o, new TransferToDispatch(), WhPer);
        Do(o, new AcknowledgeHandover(), DspPer); // any dispatch person may confirm receipt
        Assert.Equal(Stage.DispatchQueue, o.Stage);
    }

    [Fact]
    public void Wrong_stage_is_refused_as_invalid_state()
    {
        var o = NewOrder();
        var error = Refused(o, new CreateGoodsIssue(), WhSup);
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("New Request", error.Message);
    }

    [Fact]
    public void Roles_outside_the_rule_are_forbidden()
    {
        var o = NewOrder();
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new AssignWarehouse(As(WhPer)), DspSup).Kind);
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new AssignWarehouse(As(WhPer)), WhPer).Kind);
    }

    [Fact]
    public void Personnel_can_only_act_on_orders_assigned_to_them()
    {
        var o = NewOrder();
        Do(o, new AssignWarehouse(As(WhPer)), WhSup);

        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new StartPicking(), OtherWhPer).Kind);
        Do(o, new StartPicking(), WhPer);
        // Supervisors may act on behalf of their team.
        Do(o, new CreateGoodsIssue(), WhSup);
    }

    [Fact]
    public void Assignee_must_belong_to_the_right_department()
    {
        var o = NewOrder();
        var error = Refused(o, new AssignWarehouse(As(DspPer)), WhSup);
        Assert.Equal(WorkflowErrorKind.Invalid, error.Kind);
    }

    [Fact]
    public void Failed_post_qaqc_returns_to_containerization_with_reason()
    {
        var o = ToStage(Stage.PostQaqc);
        Assert.Equal(WorkflowErrorKind.Invalid, Refused(o, new FailPostQaqc(""), Qaqc).Kind);
        var e = Do(o, new FailPostQaqc("Lashing not certified"), Qaqc);

        Assert.Equal(Stage.Containerization, o.Stage);
        Assert.Equal(Stage.PostQaqc, e.FromStage);
        Assert.Equal("Lashing not certified", e.Note);
    }

    [Fact]
    public void Waybill_must_exist_before_approval_and_rejection_clears_it()
    {
        var o = ToStage(Stage.WaybillPendingSignature);
        Assert.Equal(WorkflowErrorKind.InvalidState, Refused(o, new ApproveWaybill(), Exec).Kind);

        Do(o, new GenerateWaybill("WB-1"), DspPer);
        Assert.Equal(Roles.Executive, Lifecycle.AwaitingRole(o));
        Assert.Equal(WorkflowErrorKind.InvalidState, Refused(o, new GenerateWaybill("WB-2"), DspPer).Kind);

        Do(o, new RejectWaybill("Wrong consignee"), Exec);
        Assert.Null(o.WaybillNumber);
        Assert.Equal(Stage.WaybillPendingSignature, o.Stage);
        Assert.Equal(Roles.DispatchPersonnel, Lifecycle.AwaitingRole(o));
    }

    [Fact]
    public void Cannot_ship_without_a_vessel()
    {
        var o = ToStage(Stage.AwaitingDeckspace);
        Assert.Equal(Roles.Logistics, Lifecycle.AwaitingRole(o));
        Assert.Equal(WorkflowErrorKind.InvalidState, Refused(o, new MarkShipped(), DspPer).Kind);
        Do(o, new AllocateVessel("MV Bonny"), Logistics);
        Do(o, new MarkShipped(), DspPer);
        Assert.Equal(Stage.Shipped, o.Stage);
    }

    [Fact]
    public void Only_the_original_requester_confirms_receipt()
    {
        var o = ToStage(Stage.Shipped);
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new ConfirmReceipt(), OtherRequester).Kind);
        Do(o, new ConfirmReceipt(), Requester);
    }

    [Fact]
    public void Reassign_changes_the_named_owner_without_resetting_the_stage_clock()
    {
        var o = NewOrder();
        Do(o, new AssignWarehouse(As(WhPer)), WhSup);
        var stageStart = o.StageStartedAt;

        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new Reassign(As(OtherWhPer)), DspSup).Kind);
        var e = Do(o, new Reassign(As(OtherWhPer)), WhSup);

        Assert.Equal(OtherWhPer.UserId, o.AssigneeUserId);
        Assert.Equal(OtherWhPer.Name, e.AssigneeName);
        Assert.Equal(stageStart, o.StageStartedAt); // SLA clock keeps running
    }

    [Fact]
    public void Reverse_moves_back_one_stage_and_clears_owner_when_returning_to_a_queue()
    {
        var o = NewOrder();
        Do(o, new AssignWarehouse(As(WhPer)), WhSup);

        Assert.Equal(WorkflowErrorKind.Invalid, Refused(o, new Reverse(""), WhSup).Kind);
        Assert.Equal(WorkflowErrorKind.Forbidden, Refused(o, new Reverse("Wrong person"), DspSup).Kind);

        var e = Do(o, new Reverse("Wrong person"), WhSup);
        Assert.Equal(Stage.NewRequest, o.Stage);
        Assert.Null(o.AssigneeUserId);
        Assert.Equal(Stage.WarehouseAssigned, e.FromStage);
        Assert.Equal(Stage.NewRequest, e.ToStage);
    }

    [Fact]
    public void Stage_clock_restarts_only_when_the_stage_changes()
    {
        var o = ToStage(Stage.AwaitingDeckspace);
        var entered = o.StageStartedAt;
        Do(o, new AllocateVessel("MV Bonny"), Logistics, advanceHours: 3);
        Assert.Equal(entered, o.StageStartedAt);

        Do(o, new MarkShipped(), DspPer, advanceHours: 1);
        Assert.Equal(_now, o.StageStartedAt);
    }

    [Fact]
    public void Every_rule_names_at_least_one_role_and_one_source_stage()
    {
        Assert.All(Lifecycle.Rules, kv =>
        {
            Assert.NotEmpty(kv.Value.Roles);
            Assert.NotEmpty(kv.Value.From);
            Assert.All(kv.Value.Roles, r => Assert.True(Roles.IsValid(r), $"{kv.Key}: unknown role {r}"));
        });
        // Every action except Created is governed by a rule.
        Assert.Equal(
            Enum.GetValues<WorkflowAction>().Where(a => a != WorkflowAction.Created).OrderBy(a => a),
            Lifecycle.Rules.Keys.OrderBy(a => a));
    }

    /// <summary>Drives a fresh SAP order to the requested stage via the real happy path.</summary>
    private WorkOrder ToStage(Stage target)
    {
        var o = NewOrder();
        var steps = new (WorkflowCommand Cmd, Actor Actor)[]
        {
            (new AssignWarehouse(As(WhPer)), WhSup),
            (new StartPicking(), WhPer),
            (new CreateGoodsIssue(), WhPer),
            (new TransferToDispatch(), WhPer),
            (new AcknowledgeHandover(), DspSup),
            (new AssignDispatch(As(DspPer)), DspSup),
            (new SendToPreloadQaqc(), DspPer),
            (new Containerize("CCU-1"), DspPer),
            (new SubmitForPostQaqc(), DspPer),
            (new PassPostQaqc(), Qaqc),
            (new GenerateWaybill("WB-0"), DspPer),
            (new ApproveWaybill(), Exec),
            (new RequestDeckspace(), DspPer),
            (new AllocateVessel("MV Init"), Logistics),
            (new MarkShipped(), DspPer),
        };
        foreach (var (cmd, actor) in steps)
        {
            if (o.Stage == target) break;
            Do(o, cmd, actor);
        }
        Assert.Equal(target, o.Stage);
        return o;
    }
}
