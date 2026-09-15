using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "attempt_id",
                table: "llm_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "fencing_token",
                table: "llm_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_request_id",
                table: "llm_requests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "request_id",
                table: "llm_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                table: "llm_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "extractions",
                schema: "processing",
                columns: table => new
                {
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_version = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    facts = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    error = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    llm_request_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    finalized_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extractions", x => x.extraction_id);
                });

            migrationBuilder.CreateTable(
                name: "observations",
                schema: "processing",
                columns: table => new
                {
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind_code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    legacy_target_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observations", x => x.observation_id);
                    table.ForeignKey(
                        name: "fk_observations_extractions_extraction_id",
                        column: x => x.extraction_id,
                        principalSchema: "processing",
                        principalTable: "extractions",
                        principalColumn: "extraction_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_requests_request_id",
                table: "llm_requests",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ux_processing_attempts_job_token",
                schema: "processing",
                table: "attempts",
                columns: new[] { "job_key", "fencing_token" },
                unique: true,
                filter: "fencing_token > 0");

            migrationBuilder.CreateIndex(
                name: "ix_extractions_created_at",
                schema: "processing",
                table: "extractions",
                column: "created_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_extractions_raw_message_id_run_id",
                schema: "processing",
                table: "extractions",
                columns: new[] { "raw_message_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_observations_event_kind_code_effective_at",
                schema: "processing",
                table: "observations",
                columns: new[] { "event_kind_code", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "ix_observations_extraction_id",
                schema: "processing",
                table: "observations",
                column: "extraction_id");

            migrationBuilder.CreateIndex(
                name: "ix_observations_legacy_target_id",
                schema: "processing",
                table: "observations",
                column: "legacy_target_id");

            migrationBuilder.CreateIndex(
                name: "ix_observations_raw_message_id_run_id",
                schema: "processing",
                table: "observations",
                columns: new[] { "raw_message_id", "run_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "observations",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "extractions",
                schema: "processing");

            migrationBuilder.DropIndex(
                name: "ix_llm_requests_request_id",
                table: "llm_requests");

            migrationBuilder.DropIndex(
                name: "ux_processing_attempts_job_token",
                schema: "processing",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "attempt_id",
                table: "llm_requests");

            migrationBuilder.DropColumn(
                name: "fencing_token",
                table: "llm_requests");

            migrationBuilder.DropColumn(
                name: "provider_request_id",
                table: "llm_requests");

            migrationBuilder.DropColumn(
                name: "request_id",
                table: "llm_requests");

            migrationBuilder.DropColumn(
                name: "run_id",
                table: "llm_requests");
        }
    }
}
