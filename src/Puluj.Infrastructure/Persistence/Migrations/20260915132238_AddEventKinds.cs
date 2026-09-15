using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "event_kind_id",
                table: "targets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_kinds",
                columns: table => new
                {
                    event_kind_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    name_uk = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    default_severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    state_model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    requires_location_for_map = table.Column<bool>(type: "boolean", nullable: false),
                    render_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    map_color = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    map_icon = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    map_lifetime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    creates_incident = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    map_visible = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    dedup_policy = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    presentation = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    policy_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kinds", x => x.event_kind_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_targets_event_kind_id_observed_at",
                table: "targets",
                columns: new[] { "event_kind_id", "observed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_event_kinds_code",
                table: "event_kinds",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_targets_event_kinds_event_kind_id",
                table: "targets",
                column: "event_kind_id",
                principalTable: "event_kinds",
                principalColumn: "event_kind_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_targets_event_kinds_event_kind_id",
                table: "targets");

            migrationBuilder.DropTable(
                name: "event_kinds");

            migrationBuilder.DropIndex(
                name: "ix_targets_event_kind_id_observed_at",
                table: "targets");

            migrationBuilder.DropColumn(
                name: "event_kind_id",
                table: "targets");
        }
    }
}
