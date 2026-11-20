using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EquipTrack.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrgAdminSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                table: "organizations",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "#131722");

            migrationBuilder.AddColumn<string>(
                name: "AllowedEmailDomains",
                table: "organizations",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "AutoProvision",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoProvisionRole",
                table: "organizations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "requester");

            migrationBuilder.AddColumn<string>(
                name: "BaseName",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BillingCycleEnd",
                table: "organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BillingCycleStart",
                table: "organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Coordinators",
                table: "organizations",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultOrigin",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionRequestedAt",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentLegalName",
                table: "organizations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentShortName",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Industry",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MicrosoftTenantId",
                table: "organizations",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyPriceUsd",
                table: "organizations",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationSettings",
                table: "organizations",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "PlanTier",
                table: "organizations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Starter");

            migrationBuilder.AddColumn<int>(
                name: "Seats",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlaHoursOverrides",
                table: "organizations",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "organizations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionStatus",
                table: "organizations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Trialing");

            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "organizations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Africa/Lagos");

            migrationBuilder.AddColumn<DateOnly>(
                name: "TrialEndsAt",
                table: "organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Department",
                table: "organization_members",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InviteEmailSentAt",
                table: "organization_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InviteExpiresAt",
                table: "organization_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActiveAt",
                table: "organization_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "org_audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Metadata = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_org_audit_events_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_org_audit_events_OrganizationId_OccurredAt",
                table: "org_audit_events",
                columns: new[] { "OrganizationId", "OccurredAt" });

            // Existing organisations start on the Starter plan's seat limit; existing members' invitations never expire.
            migrationBuilder.Sql("""UPDATE organizations SET "Seats" = 10 WHERE "Seats" IS NULL AND "PlanTier" = 'Starter';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_audit_events");

            migrationBuilder.DropColumn(
                name: "AccentColor",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "AllowedEmailDomains",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "AutoProvision",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "AutoProvisionRole",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "BaseName",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "BillingCycleEnd",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "BillingCycleStart",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Coordinators",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "DefaultOrigin",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAt",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "DocumentLegalName",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "DocumentShortName",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Industry",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "MicrosoftTenantId",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "MonthlyPriceUsd",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "NotificationSettings",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "PlanTier",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Seats",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "SlaHoursOverrides",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "TrialEndsAt",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "Department",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "InviteEmailSentAt",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "InviteExpiresAt",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "LastActiveAt",
                table: "organization_members");
        }
    }
}
