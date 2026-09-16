using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lane",
                schema: "processing",
                table: "deliveries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                schema: "processing",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "control_audit",
                schema: "messaging",
                columns: table => new
                {
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_audit", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_lanes",
                schema: "messaging",
                columns: table => new
                {
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_lanes", x => new { x.subscription_id, x.lane });
                    table.CheckConstraint("ck_subscription_lanes_state", "state IN ('active', 'paused', 'draining')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_processing_deliveries_completed_brin",
                schema: "processing",
                table: "deliveries",
                column: "completed_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_processing_deliveries_expected_brin",
                schema: "processing",
                table: "deliveries",
                column: "expected_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_processing_deliveries_pending_lane",
                schema: "processing",
                table: "deliveries",
                columns: new[] { "subscription_id", "lane" },
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processing_attempts_event",
                schema: "processing",
                table: "attempts",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_processing_attempts_running",
                schema: "processing",
                table: "attempts",
                column: "subscription_id",
                filter: "state = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_control_audit_at",
                schema: "messaging",
                table: "control_audit",
                column: "at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_control_audit_subscription_id_lane_at",
                schema: "messaging",
                table: "control_audit",
                columns: new[] { "subscription_id", "lane", "at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "control_audit",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "subscription_lanes",
                schema: "messaging");

            migrationBuilder.DropIndex(
                name: "ix_processing_deliveries_completed_brin",
                schema: "processing",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_processing_deliveries_expected_brin",
                schema: "processing",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_processing_deliveries_pending_lane",
                schema: "processing",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_processing_attempts_event",
                schema: "processing",
                table: "attempts");

            migrationBuilder.DropIndex(
                name: "ix_processing_attempts_running",
                schema: "processing",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "lane",
                schema: "processing",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "occurred_at",
                schema: "processing",
                table: "deliveries");
        }
    }
}
