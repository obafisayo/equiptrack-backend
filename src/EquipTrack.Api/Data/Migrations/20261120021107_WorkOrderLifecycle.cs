using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EquipTrack.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Urgency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Destination = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Entity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequiredDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TrDepartment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequesterUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequesterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StageStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssigneeUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContainerRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WaybillNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WaybillApproved = table.Column<bool>(type: "boolean", nullable: false),
                    VesselName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_order_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FromStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ToStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AssigneeUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_order_events_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_order_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PartNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Plant = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BinLocation = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_order_items_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_events_ActorUserId_OccurredAt",
                table: "work_order_events",
                columns: new[] { "ActorUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_events_WorkOrderId_OccurredAt",
                table: "work_order_events",
                columns: new[] { "WorkOrderId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_items_WorkOrderId",
                table: "work_order_items",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_OrganizationId_AssigneeUserId",
                table: "work_orders",
                columns: new[] { "OrganizationId", "AssigneeUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_OrganizationId_Number",
                table: "work_orders",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_OrganizationId_RequesterUserId",
                table: "work_orders",
                columns: new[] { "OrganizationId", "RequesterUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_OrganizationId_Status_Stage",
                table: "work_orders",
                columns: new[] { "OrganizationId", "Status", "Stage" });

            // The event log is the audit trail: enforce append-only in the database itself,
            // so neither application bugs nor ad-hoc SQL can rewrite history.
            migrationBuilder.Sql("""
                CREATE FUNCTION work_order_events_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'work_order_events is append-only (% blocked)', TG_OP;
                END;
                $$;

                CREATE TRIGGER work_order_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON work_order_events
                    FOR EACH ROW EXECUTE FUNCTION work_order_events_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS work_order_events_no_update_delete ON work_order_events;
                DROP FUNCTION IF EXISTS work_order_events_append_only();
                """);

            migrationBuilder.DropTable(
                name: "work_order_events");

            migrationBuilder.DropTable(
                name: "work_order_items");

            migrationBuilder.DropTable(
                name: "work_orders");
        }
    }
}
