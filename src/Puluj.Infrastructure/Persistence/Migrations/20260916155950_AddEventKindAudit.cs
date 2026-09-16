using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventKindAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "presentation_overridden_at",
                table: "event_kinds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_kind_audit",
                columns: table => new
                {
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_kind_id = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    before = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    after = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_audit", x => x.audit_id);
                    table.ForeignKey(
                        name: "fk_event_kind_audit_event_kinds_event_kind_id",
                        column: x => x.event_kind_id,
                        principalTable: "event_kinds",
                        principalColumn: "event_kind_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_audit_event_kind_id_at",
                table: "event_kind_audit",
                columns: new[] { "event_kind_id", "at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_kind_audit");

            migrationBuilder.DropColumn(
                name: "presentation_overridden_at",
                table: "event_kinds");
        }
    }
}
