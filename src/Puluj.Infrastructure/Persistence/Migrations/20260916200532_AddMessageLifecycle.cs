using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "message_lifecycle",
                schema: "analytics",
                columns: table => new
                {
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    source_message_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    has_text = table.Column<bool>(type: "boolean", nullable: false),
                    text_length = table.Column<int>(type: "integer", nullable: false),
                    has_payload = table.Column<bool>(type: "boolean", nullable: false),
                    is_edit = table.Column<bool>(type: "boolean", nullable: false),
                    analyzed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    analysis_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    fact_count = table.Column<int>(type: "integer", nullable: false),
                    unlocated_facts = table.Column<int>(type: "integer", nullable: false),
                    versions = table.Column<string>(type: "jsonb", nullable: true),
                    timings = table.Column<string>(type: "jsonb", nullable: true),
                    timings_available = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    expected_branches = table.Column<string[]>(type: "text[]", nullable: false),
                    branches_done = table.Column<string[]>(type: "text[]", nullable: false),
                    domain_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completion_available = table.Column<bool>(type: "boolean", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    track_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    alert_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    llm_calls = table.Column<int>(type: "integer", nullable: false),
                    llm_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    llm_cache_tokens = table.Column<long>(type: "bigint", nullable: false),
                    llm_output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    llm_cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    llm_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    source_of_truth = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_lifecycle", x => new { x.raw_message_id, x.run_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_lifecycle_pending",
                schema: "analytics",
                table: "message_lifecycle",
                columns: new[] { "received_at", "run_id" },
                filter: "analyzed_at IS NULL OR domain_completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_message_lifecycle_post",
                schema: "analytics",
                table: "message_lifecycle",
                columns: new[] { "source_id", "source_message_key" });

            migrationBuilder.CreateIndex(
                name: "ix_message_lifecycle_received_brin",
                schema: "analytics",
                table: "message_lifecycle",
                column: "received_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_message_lifecycle_window",
                schema: "analytics",
                table: "message_lifecycle",
                columns: new[] { "received_at", "source_id", "analysis_outcome" });

            // The admin panel (puluj_admin) reads and backfills the projection; the reader role may read it (same rule as the analytics schema's own tables).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                        GRANT USAGE ON SCHEMA analytics TO puluj_admin;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON analytics.message_lifecycle TO puluj_admin;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_reader') THEN
                        GRANT USAGE ON SCHEMA analytics TO puluj_reader;
                        GRANT SELECT ON analytics.message_lifecycle TO puluj_reader;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_lifecycle",
                schema: "analytics");
        }
    }
}
