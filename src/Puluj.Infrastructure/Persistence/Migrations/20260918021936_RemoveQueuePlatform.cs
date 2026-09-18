using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveQueuePlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE event_kind_rulesets SET state = 'draft' WHERE state = 'shadow';");
            migrationBuilder.Sql("UPDATE event_kinds SET category = 'event' WHERE category = 'incident';");

            migrationBuilder.DropTable(
                name: "attempts",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "control_audit",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "processing");

            migrationBuilder.DropTable(
                name: "event_kind_rule_shadow");

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
                name: "incident_observations");

            migrationBuilder.DropTable(
                name: "incident_revisions");

            migrationBuilder.DropTable(
                name: "message_lifecycle",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "observations",
                schema: "processing");

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
                name: "subscription_lanes",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "topology_versions",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "extractions",
                schema: "processing");

            migrationBuilder.DropIndex(
                name: "ux_targets_observation_id",
                table: "targets");

            migrationBuilder.DropIndex(
                name: "ix_llm_requests_request_id",
                table: "llm_requests");

            migrationBuilder.DropIndex(
                name: "ux_event_kind_rulesets_shadow",
                table: "event_kind_rulesets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_event_kind_rulesets_state",
                table: "event_kind_rulesets");

            migrationBuilder.DropColumn(
                name: "observation_id",
                table: "targets");

            migrationBuilder.DropColumn(
                name: "last_correlation_id",
                table: "target_tracks");

            migrationBuilder.DropColumn(
                name: "last_event_id",
                table: "target_tracks");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "target_tracks");

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

            migrationBuilder.DropColumn(
                name: "creates_incident",
                table: "event_kinds");

            migrationBuilder.DropColumn(
                name: "dedup_policy",
                table: "event_kinds");

            migrationBuilder.DropColumn(
                name: "last_correlation_id",
                table: "air_alerts");

            migrationBuilder.DropColumn(
                name: "last_event_id",
                table: "air_alerts");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "air_alerts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_event_kind_rulesets_state",
                table: "event_kind_rulesets",
                sql: "state IN ('draft', 'published', 'superseded')");

            migrationBuilder.Sql("DROP SCHEMA IF EXISTS messaging CASCADE;");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS processing CASCADE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE event_kinds SET category = 'incident' WHERE category = 'event';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_event_kind_rulesets_state",
                table: "event_kind_rulesets");

            migrationBuilder.EnsureSchema(
                name: "processing");

            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.AddColumn<Guid>(
                name: "observation_id",
                table: "targets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_correlation_id",
                table: "target_tracks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_event_id",
                table: "target_tracks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "target_tracks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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

            migrationBuilder.AddColumn<bool>(
                name: "creates_incident",
                table: "event_kinds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "dedup_policy",
                table: "event_kinds",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_correlation_id",
                table: "air_alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_event_id",
                table: "air_alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "air_alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "processing",
                columns: table => new
                {
                    attempt_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fencing_token = table.Column<int>(type: "integer", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    job_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_of_attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    retry_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    stage_result_id = table.Column<long>(type: "bigint", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    worker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempts", x => x.attempt_id);
                });

            migrationBuilder.CreateTable(
                name: "control_audit",
                schema: "messaging",
                columns: table => new
                {
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_audit", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "processing",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    topology_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => new { x.event_id, x.subscription_id });
                });

            migrationBuilder.CreateTable(
                name: "event_kind_rule_shadow",
                columns: table => new
                {
                    shadow_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    live_kind = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    live_rule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    live_version = table.Column<int>(type: "integer", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    shadow_kind = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    shadow_rule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    shadow_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_rule_shadow", x => x.shadow_id);
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
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: true),
                    topology_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "extractions",
                schema: "processing",
                columns: table => new
                {
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    extraction_version = table.Column<int>(type: "integer", nullable: false),
                    facts = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    finalized_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    llm_request_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extractions", x => x.extraction_id);
                });

            migrationBuilder.CreateTable(
                name: "generations",
                schema: "processing",
                columns: table => new
                {
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
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
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.subscription_id, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    incident_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_kind_id = table.Column<int>(type: "integer", nullable: false),
                    accuracy_km = table.Column<double>(type: "double precision", nullable: true),
                    canonical_observation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closure_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    geometry = table.Column<Geometry>(type: "geography (geometry, 4326)", nullable: true),
                    independent_source_count = table.Column<int>(type: "integer", nullable: true),
                    last_correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location_kind = table.Column<int>(type: "integer", nullable: false),
                    location_place_id = table.Column<int>(type: "integer", nullable: true),
                    merged_into_incident_id = table.Column<long>(type: "bigint", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_count = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    suppressed = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.incident_id);
                    table.CheckConstraint("ck_incidents_state", "state IN ('reported', 'confirmed', 'resolved', 'retracted')");
                    table.ForeignKey(
                        name: "fk_incidents_event_kinds_event_kind_id",
                        column: x => x.event_kind_id,
                        principalTable: "event_kinds",
                        principalColumn: "event_kind_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_lifecycle",
                schema: "analytics",
                columns: table => new
                {
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    analysis_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    analyzed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    branches_done = table.Column<string[]>(type: "text[]", nullable: false),
                    completion_available = table.Column<bool>(type: "boolean", nullable: false),
                    domain_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    expected_branches = table.Column<string[]>(type: "text[]", nullable: false),
                    fact_count = table.Column<int>(type: "integer", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    has_payload = table.Column<bool>(type: "boolean", nullable: false),
                    has_text = table.Column<bool>(type: "boolean", nullable: false),
                    incident_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    is_edit = table.Column<bool>(type: "boolean", nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    llm_cache_tokens = table.Column<long>(type: "bigint", nullable: false),
                    llm_calls = table.Column<int>(type: "integer", nullable: false),
                    llm_cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    llm_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    llm_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    llm_output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    source_message_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_of_truth = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    text_length = table.Column<int>(type: "integer", nullable: false),
                    timings = table.Column<string>(type: "jsonb", nullable: true),
                    timings_available = table.Column<bool>(type: "boolean", nullable: false),
                    track_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    unlocated_facts = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    versions = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_lifecycle", x => new { x.raw_message_id, x.run_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "messaging",
                columns: table => new
                {
                    outbox_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replay_source = table.Column<bool>(type: "boolean", nullable: false),
                    routing_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_queue = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
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
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    headers = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    quarantined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    retry_outbox_id = table.Column<long>(type: "bigint", nullable: true),
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
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
                    checkpoint = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    replays_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    supersedes_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true)
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
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outputs = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    stage_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    versions = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    worker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_results", x => x.stage_result_id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_lanes",
                schema: "messaging",
                columns: table => new
                {
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lane = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_lanes", x => new { x.subscription_id, x.lane });
                    table.CheckConstraint("ck_subscription_lanes_state", "state IN ('active', 'paused', 'draining')");
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "messaging",
                columns: table => new
                {
                    subscription_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    topology_version = table.Column<int>(type: "integer", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    bindings = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    lanes = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    owner_task = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    queue_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    waiver = table.Column<JsonDocument>(type: "jsonb", nullable: true)
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
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_versions", x => x.topology_version);
                });

            migrationBuilder.CreateTable(
                name: "observations",
                schema: "processing",
                columns: table => new
                {
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_kind_code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    legacy_target_id = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "incident_observations",
                columns: table => new
                {
                    incident_id = table.Column<long>(type: "bigint", nullable: false),
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_reason = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_target_id = table.Column<long>(type: "bigint", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    relation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_observations", x => new { x.incident_id, x.observation_id });
                    table.ForeignKey(
                        name: "fk_incident_observations_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "incident_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incident_revisions",
                columns: table => new
                {
                    incident_revision_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    incident_id = table.Column<long>(type: "bigint", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    change = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    triggering_event_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_revisions", x => x.incident_revision_id);
                    table.ForeignKey(
                        name: "fk_incident_revisions_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "incident_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_targets_observation_id",
                table: "targets",
                column: "observation_id",
                unique: true,
                filter: "observation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_llm_requests_request_id",
                table: "llm_requests",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ux_event_kind_rulesets_shadow",
                table: "event_kind_rulesets",
                column: "state",
                unique: true,
                filter: "state = 'shadow'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_event_kind_rulesets_state",
                table: "event_kind_rulesets",
                sql: "state IN ('draft', 'shadow', 'published', 'superseded')");

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
                name: "ux_processing_attempts_job_token",
                schema: "processing",
                table: "attempts",
                columns: new[] { "job_key", "fencing_token" },
                unique: true,
                filter: "fencing_token > 0");

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

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_subscription_id_outcome",
                schema: "processing",
                table: "deliveries",
                columns: new[] { "subscription_id", "outcome" });

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
                name: "ix_processing_deliveries_pending",
                schema: "processing",
                table: "deliveries",
                column: "expected_at",
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processing_deliveries_pending_lane",
                schema: "processing",
                table: "deliveries",
                columns: new[] { "subscription_id", "lane" },
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rule_shadow_raw_message_id_run_id",
                table: "event_kind_rule_shadow",
                columns: new[] { "raw_message_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rule_shadow_shadow_version_created_at",
                table: "event_kind_rule_shadow",
                columns: new[] { "shadow_version", "created_at" });

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
                name: "ix_messaging_events_run",
                schema: "messaging",
                table: "events",
                column: "processing_run_id");

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
                name: "ix_incident_observations_observation_id",
                table: "incident_observations",
                column: "observation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_revisions_incident_id_revision",
                table: "incident_revisions",
                columns: new[] { "incident_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_event_kind_id_event_at",
                table: "incidents",
                columns: new[] { "event_kind_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_generation_id",
                table: "incidents",
                column: "generation_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_geometry",
                table: "incidents",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_read_keyset",
                table: "incidents",
                columns: new[] { "last_reported_at", "incident_id" },
                descending: new bool[0],
                filter: "NOT suppressed");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_state_last_reported_at",
                table: "incidents",
                columns: new[] { "state", "last_reported_at" });

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
                name: "ux_processing_runs_open_replay",
                schema: "processing",
                table: "runs",
                column: "kind",
                unique: true,
                filter: "kind = 'replay' AND state IN ('created', 'running', 'paused', 'verified')");

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
        }
    }
}
