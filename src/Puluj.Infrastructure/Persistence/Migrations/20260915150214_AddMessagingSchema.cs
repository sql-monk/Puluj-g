using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P03 (ADR-0006): additive schemas `messaging` (outbox, inbox, events archive, event links, subscription registry,
    /// topology versions) and `processing` (runs, generations, stage results, attempts, deliveries, quarantine).
    /// Nothing in `public` changes; rollback is this migration's Down (or simply not reading the schemas).
    /// The service roles of AddDbRoles get the same rights here as on `public` (read for the Api, read-write for the
    /// admin panel), so the message explorer (P13) and retries do not need the owner connection.
    /// </summary>
    public partial class AddMessagingSchema : Migration
    {
        private const string GrantSql = """
            GRANT USAGE ON SCHEMA messaging, processing TO puluj_reader, puluj_admin;
            GRANT SELECT ON ALL TABLES IN SCHEMA messaging, processing TO puluj_reader;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA messaging, processing TO puluj_admin;
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA messaging, processing TO puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA messaging GRANT SELECT ON TABLES TO puluj_reader;
            ALTER DEFAULT PRIVILEGES IN SCHEMA processing GRANT SELECT ON TABLES TO puluj_reader;
            ALTER DEFAULT PRIVILEGES IN SCHEMA messaging GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA processing GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA messaging GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA processing GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO puluj_admin;
            """;

        private const string RevokeSql = """
            ALTER DEFAULT PRIVILEGES IN SCHEMA messaging REVOKE ALL ON TABLES FROM puluj_reader, puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA processing REVOKE ALL ON TABLES FROM puluj_reader, puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA messaging REVOKE ALL ON SEQUENCES FROM puluj_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA processing REVOKE ALL ON SEQUENCES FROM puluj_admin;
            REVOKE USAGE ON SCHEMA messaging, processing FROM puluj_reader, puluj_admin;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "processing");

            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "processing",
                columns: table => new
                {
                    attempt_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    stage_result_id = table.Column<long>(type: "bigint", nullable: true),
                    worker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fencing_token = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    retry_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    retry_of_attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempts", x => x.attempt_id);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "processing",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    topology_version = table.Column<int>(type: "integer", nullable: false),
                    expected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    attempt_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => new { x.event_id, x.subscription_id });
                });

            migrationBuilder.CreateTable(
                name: "event_links",
                schema: "messaging",
                columns: table => new
                {
                    output_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_links", x => new { x.output_event_id, x.input_event_id });
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "messaging",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: true),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topology_version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "generations",
                schema: "processing",
                columns: table => new
                {
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rolled_back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generations", x => x.generation_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "messaging",
                columns: table => new
                {
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.subscription_id, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "messaging",
                columns: table => new
                {
                    outbox_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_queue = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    replay_source = table.Column<bool>(type: "boolean", nullable: false),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "quarantine",
                schema: "processing",
                columns: table => new
                {
                    quarantine_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    headers = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    last_attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    quarantined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    retry_outbox_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quarantine", x => x.quarantine_id);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "processing",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supersedes_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replays_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    scope = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    checkpoint = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "stage_results",
                schema: "processing",
                columns: table => new
                {
                    stage_result_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    stage_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outputs = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    worker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_results", x => x.stage_result_id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "messaging",
                columns: table => new
                {
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    topology_version = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bindings = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    lanes = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    queue_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_task = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waiver = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => new { x.subscription_id, x.topology_version });
                });

            migrationBuilder.CreateTable(
                name: "topology_versions",
                schema: "messaging",
                columns: table => new
                {
                    topology_version = table.Column<int>(type: "integer", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_versions", x => x.topology_version);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_job_key_fencing_token",
                schema: "processing",
                table: "attempts",
                columns: new[] { "job_key", "fencing_token" });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_stage_result_id",
                schema: "processing",
                table: "attempts",
                column: "stage_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_started_at",
                schema: "processing",
                table: "attempts",
                column: "started_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_subscription_id_event_id",
                schema: "processing",
                table: "attempts",
                columns: new[] { "subscription_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_subscription_id_outcome",
                schema: "processing",
                table: "deliveries",
                columns: new[] { "subscription_id", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_deliveries_pending",
                schema: "processing",
                table: "deliveries",
                column: "expected_at",
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_event_links_input_event_id",
                schema: "messaging",
                table: "event_links",
                column: "input_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_correlation_id",
                schema: "messaging",
                table: "events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_event_type_published_at",
                schema: "messaging",
                table: "events",
                columns: new[] { "event_type", "published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_published_at",
                schema: "messaging",
                table: "events",
                column: "published_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_events_raw_message_id_processing_run_id",
                schema: "messaging",
                table: "events",
                columns: new[] { "raw_message_id", "processing_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_processing_generations_active",
                schema: "processing",
                table: "generations",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_completed_at",
                schema: "messaging",
                table: "inbox",
                column: "completed_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_outbox_confirmed_at",
                schema: "messaging",
                table: "outbox",
                column: "confirmed_at",
                filter: "confirmed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_outbox_event_id_fanout",
                schema: "messaging",
                table: "outbox",
                column: "event_id",
                unique: true,
                filter: "target_queue IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_outbox_unconfirmed",
                schema: "messaging",
                table: "outbox",
                column: "next_attempt_at",
                filter: "confirmed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_subscription_id_event_id",
                schema: "processing",
                table: "quarantine",
                columns: new[] { "subscription_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ux_processing_quarantine_open",
                schema: "processing",
                table: "quarantine",
                columns: new[] { "subscription_id", "event_id" },
                unique: true,
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runs_lane_state",
                schema: "processing",
                table: "runs",
                columns: new[] { "lane", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_processing_runs_open_per_lane",
                schema: "processing",
                table: "runs",
                column: "lane",
                unique: true,
                filter: "state = 'running' AND kind IN ('live', 'history')");

            migrationBuilder.CreateIndex(
                name: "ix_stage_results_raw_message_id_run_id_stage_stage_version",
                schema: "processing",
                table: "stage_results",
                columns: new[] { "raw_message_id", "run_id", "stage", "stage_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_results_run_id_stage_outcome",
                schema: "processing",
                table: "stage_results",
                columns: new[] { "run_id", "stage", "outcome" });

            migrationBuilder.Sql(GrantSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RevokeSql);

            migrationBuilder.DropTable(
                name: "attempts",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "event_links",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "events",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "generations",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "quarantine",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "stage_results",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "topology_versions",
                schema: "messaging");

            migrationBuilder.Sql("DROP SCHEMA IF EXISTS messaging; DROP SCHEMA IF EXISTS processing;");
        }
    }
}
