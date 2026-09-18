using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityExtractor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ee_delivery_queue",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<short>(type: "smallint", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_delivery_queue", x => x.delivery_id);
                    table.ForeignKey(
                        name: "fk_ee_delivery_queue_raw_messages_raw_message_id",
                        column: x => x.raw_message_id,
                        principalTable: "raw_messages",
                        principalColumn: "raw_message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ee_entity_definitions",
                columns: table => new
                {
                    entity_definition_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    table_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    fields = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    map_settings = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_entity_definitions", x => x.entity_definition_id);
                });

            migrationBuilder.CreateTable(
                name: "ee_extractors",
                columns: table => new
                {
                    extractor_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    execution_order = table.Column<int>(type: "integer", nullable: false),
                    timeout_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_extractors", x => x.extractor_id);
                });

            migrationBuilder.CreateTable(
                name: "ee_delivery_attempts",
                columns: table => new
                {
                    delivery_attempt_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    result = table.Column<short>(type: "smallint", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_delivery_attempts", x => x.delivery_attempt_id);
                    table.ForeignKey(
                        name: "fk_ee_delivery_attempts_ee_delivery_queue_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "ee_delivery_queue",
                        principalColumn: "delivery_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ee_processing_runs",
                columns: table => new
                {
                    processing_run_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    result = table.Column<short>(type: "smallint", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_processing_runs", x => x.processing_run_id);
                    table.ForeignKey(
                        name: "fk_ee_processing_runs_ee_delivery_queue_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "ee_delivery_queue",
                        principalColumn: "delivery_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ee_processing_runs_raw_messages_raw_message_id",
                        column: x => x.raw_message_id,
                        principalTable: "raw_messages",
                        principalColumn: "raw_message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ee_extractor_runs",
                columns: table => new
                {
                    extractor_run_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    processing_run_id = table.Column<long>(type: "bigint", nullable: false),
                    extractor_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    writes_count = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    stdout = table.Column<string>(type: "text", nullable: true),
                    stderr = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_extractor_runs", x => x.extractor_run_id);
                    table.ForeignKey(
                        name: "fk_ee_extractor_runs_ee_extractors_extractor_id",
                        column: x => x.extractor_id,
                        principalTable: "ee_extractors",
                        principalColumn: "extractor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ee_extractor_runs_ee_processing_runs_processing_run_id",
                        column: x => x.processing_run_id,
                        principalTable: "ee_processing_runs",
                        principalColumn: "processing_run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ee_entity_writes",
                columns: table => new
                {
                    entity_write_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    processing_run_id = table.Column<long>(type: "bigint", nullable: false),
                    extractor_run_id = table.Column<long>(type: "bigint", nullable: true),
                    entity_definition_id = table.Column<long>(type: "bigint", nullable: false),
                    table_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ee_entity_writes", x => x.entity_write_id);
                    table.ForeignKey(
                        name: "fk_ee_entity_writes_ee_entity_definitions_entity_definition_id",
                        column: x => x.entity_definition_id,
                        principalTable: "ee_entity_definitions",
                        principalColumn: "entity_definition_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ee_entity_writes_ee_extractor_runs_extractor_run_id",
                        column: x => x.extractor_run_id,
                        principalTable: "ee_extractor_runs",
                        principalColumn: "extractor_run_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ee_entity_writes_ee_processing_runs_processing_run_id",
                        column: x => x.processing_run_id,
                        principalTable: "ee_processing_runs",
                        principalColumn: "processing_run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ee_delivery_attempts_delivery_id_started_at",
                table: "ee_delivery_attempts",
                columns: new[] { "delivery_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ee_delivery_queue_lease_expires_at",
                table: "ee_delivery_queue",
                column: "lease_expires_at",
                filter: "status = 'in_progress'");

            migrationBuilder.CreateIndex(
                name: "ix_ee_delivery_queue_raw_message_id",
                table: "ee_delivery_queue",
                column: "raw_message_id",
                unique: true,
                filter: "origin = 'live'");

            migrationBuilder.CreateIndex(
                name: "ix_ee_delivery_queue_status_enqueued_at",
                table: "ee_delivery_queue",
                columns: new[] { "status", "enqueued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ee_entity_definitions_entity_name",
                table: "ee_entity_definitions",
                column: "entity_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ee_entity_definitions_table_name",
                table: "ee_entity_definitions",
                column: "table_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ee_entity_writes_entity_definition_id",
                table: "ee_entity_writes",
                column: "entity_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_ee_entity_writes_extractor_run_id",
                table: "ee_entity_writes",
                column: "extractor_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_ee_entity_writes_processing_run_id_created_at",
                table: "ee_entity_writes",
                columns: new[] { "processing_run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ee_extractor_runs_extractor_id",
                table: "ee_extractor_runs",
                column: "extractor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ee_extractor_runs_processing_run_id_extractor_id",
                table: "ee_extractor_runs",
                columns: new[] { "processing_run_id", "extractor_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ee_extractors_enabled_execution_order",
                table: "ee_extractors",
                columns: new[] { "enabled", "execution_order" });

            migrationBuilder.CreateIndex(
                name: "ix_ee_extractors_name",
                table: "ee_extractors",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ee_processing_runs_delivery_id",
                table: "ee_processing_runs",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ee_processing_runs_raw_message_id_started_at",
                table: "ee_processing_runs",
                columns: new[] { "raw_message_id", "started_at" });

            migrationBuilder.Sql(
                """
                ALTER TABLE ee_delivery_queue
                    ADD CONSTRAINT ck_ee_delivery_queue_status CHECK (status IN ('pending', 'in_progress', 'succeeded', 'failed')),
                    ADD CONSTRAINT ck_ee_delivery_queue_origin CHECK (origin IN ('live', 'manual')),
                    ADD CONSTRAINT ck_ee_delivery_queue_result CHECK (result IS NULL OR result IN (0, 1));
                ALTER TABLE ee_delivery_attempts
                    ADD CONSTRAINT ck_ee_delivery_attempts_result CHECK (result IS NULL OR result IN (0, 1));
                ALTER TABLE ee_processing_runs
                    ADD CONSTRAINT ck_ee_processing_runs_result CHECK (result IS NULL OR result IN (0, 1));

                CREATE TABLE ee_targets (
                    "targetId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    target_type text NULL,
                    status text NULL,
                    geometry geometry(Point, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_tracks (
                    "trackId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    geometry geometry(LineString, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_alerts (
                    "alertId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    alert_type text NULL,
                    status text NULL,
                    geometry geometry(Geometry, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_impacts (
                    "impactId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    confidence numeric NULL,
                    geometry geometry(Point, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_explosions (
                    "explosionId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    confidence numeric NULL,
                    geometry geometry(Point, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_air_defense_actions (
                    "airDefenseActionId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    confidence numeric NULL,
                    geometry geometry(Point, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_launches (
                    "launchId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    launch_type text NULL,
                    geometry geometry(LineString, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                CREATE TABLE ee_takeoffs (
                    "takeoffId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    raw_message_id bigint NOT NULL REFERENCES raw_messages(raw_message_id) ON DELETE CASCADE,
                    occurred_at timestamptz NULL,
                    label text NULL,
                    aircraft_type text NULL,
                    geometry geometry(Point, 4326) NULL,
                    attributes jsonb NOT NULL DEFAULT '{}'::jsonb
                );

                CREATE INDEX ix_ee_targets_raw_message_id ON ee_targets(raw_message_id);
                CREATE INDEX ix_ee_targets_geometry ON ee_targets USING gist(geometry);
                CREATE INDEX ix_ee_tracks_raw_message_id ON ee_tracks(raw_message_id);
                CREATE INDEX ix_ee_tracks_geometry ON ee_tracks USING gist(geometry);
                CREATE INDEX ix_ee_alerts_raw_message_id ON ee_alerts(raw_message_id);
                CREATE INDEX ix_ee_alerts_geometry ON ee_alerts USING gist(geometry);
                CREATE INDEX ix_ee_impacts_raw_message_id ON ee_impacts(raw_message_id);
                CREATE INDEX ix_ee_impacts_geometry ON ee_impacts USING gist(geometry);
                CREATE INDEX ix_ee_explosions_raw_message_id ON ee_explosions(raw_message_id);
                CREATE INDEX ix_ee_explosions_geometry ON ee_explosions USING gist(geometry);
                CREATE INDEX ix_ee_air_defense_actions_raw_message_id ON ee_air_defense_actions(raw_message_id);
                CREATE INDEX ix_ee_air_defense_actions_geometry ON ee_air_defense_actions USING gist(geometry);
                CREATE INDEX ix_ee_launches_raw_message_id ON ee_launches(raw_message_id);
                CREATE INDEX ix_ee_launches_geometry ON ee_launches USING gist(geometry);
                CREATE INDEX ix_ee_takeoffs_raw_message_id ON ee_takeoffs(raw_message_id);
                CREATE INDEX ix_ee_takeoffs_geometry ON ee_takeoffs USING gist(geometry);

                INSERT INTO ee_entity_definitions
                    (entity_name, table_name, fields, map_settings, enabled, created_at, updated_at)
                VALUES
                    ('target', 'ee_targets',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"targetType","type":"text","required":false},{"name":"status","type":"text","required":false},{"name":"geometry","type":"point","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"icon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('track', 'ee_tracks',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"geometry","type":"line","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"line","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('alert', 'ee_alerts',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"alertType","type":"text","required":false},{"name":"status","type":"text","required":false},{"name":"geometry","type":"polygon","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"polygon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('impact', 'ee_impacts',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"confidence","type":"decimal","required":false},{"name":"geometry","type":"point","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"icon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('explosion', 'ee_explosions',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"confidence","type":"decimal","required":false},{"name":"geometry","type":"point","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"icon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('airDefenseAction', 'ee_air_defense_actions',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"confidence","type":"decimal","required":false},{"name":"geometry","type":"point","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"icon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('launch', 'ee_launches',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"launchType","type":"text","required":false},{"name":"geometry","type":"line","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"line","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now()),
                    ('takeoff', 'ee_takeoffs',
                     '[{"name":"occurredAt","type":"datetime","required":false},{"name":"label","type":"text","required":false},{"name":"aircraftType","type":"text","required":false},{"name":"geometry","type":"point","required":false},{"name":"attributes","type":"json","required":false}]'::jsonb,
                     '{"enabled":true,"renderer":"icon","geometryField":"geometry","labelField":"label","svg":null}'::jsonb, true, now(), now());

                INSERT INTO ee_extractors (name, code, enabled, execution_order, timeout_ms)
                VALUES
                    ('target', E'def extract(message, write):\n    # Add target recognition and call write("ee_targets", values).\n    return None\n', false, 10, 5000),
                    ('track', E'def extract(message, write):\n    # Add track recognition and call write("ee_tracks", values).\n    return None\n', false, 20, 5000),
                    ('alert', E'def extract(message, write):\n    # Add alert recognition and call write("ee_alerts", values).\n    return None\n', false, 30, 5000),
                    ('impact', E'def extract(message, write):\n    # Add impact recognition and call write("ee_impacts", values).\n    return None\n', false, 40, 5000),
                    ('explosion', E'def extract(message, write):\n    # Add explosion recognition and call write("ee_explosions", values).\n    return None\n', false, 50, 5000),
                    ('airDefenseAction', E'def extract(message, write):\n    # Add air-defense recognition and call write("ee_air_defense_actions", values).\n    return None\n', false, 60, 5000),
                    ('launch', E'def extract(message, write):\n    # Add launch recognition and call write("ee_launches", values).\n    return None\n', false, 70, 5000),
                    ('takeoff', E'def extract(message, write):\n    # Add takeoff recognition and call write("ee_takeoffs", values).\n    return None\n', false, 80, 5000);

                CREATE FUNCTION ee_create_entity_definition(
                    p_entity_name text,
                    p_fields jsonb,
                    p_map_settings jsonb,
                    p_enabled boolean DEFAULT true)
                RETURNS jsonb
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    normalized_entity text;
                    snake_entity text;
                    physical_table text;
                    id_column text;
                    logical_id text;
                    field jsonb;
                    field_name text;
                    field_column text;
                    field_type text;
                    field_required boolean;
                    column_sql text := '';
                    seen_columns text[] := ARRAY[]::text[];
                    entity_sequence text;
                    definition jsonb;
                BEGIN
                    normalized_entity := lower(substr(trim(p_entity_name), 1, 1)) || substr(trim(p_entity_name), 2);
                    IF normalized_entity !~ '^[A-Za-z][A-Za-z0-9_]{0,62}$' THEN
                        RAISE EXCEPTION 'invalid entity name';
                    END IF;
                    IF jsonb_typeof(p_fields) <> 'array' OR jsonb_array_length(p_fields) = 0 THEN
                        RAISE EXCEPTION 'fields must be a non-empty array';
                    END IF;

                    snake_entity := ltrim(lower(regexp_replace(normalized_entity, '([A-Z])', '_\1', 'g')), '_');
                    IF right(snake_entity, 1) = 's' THEN
                        physical_table := 'ee_' || snake_entity;
                    ELSIF right(snake_entity, 1) = 'y' THEN
                        physical_table := 'ee_' || left(snake_entity, length(snake_entity) - 1) || 'ies';
                    ELSE
                        physical_table := 'ee_' || snake_entity || 's';
                    END IF;
                    logical_id := normalized_entity || 'Id';
                    id_column := logical_id;
                    IF length(physical_table) > 63 OR length(id_column) > 63 THEN
                        RAISE EXCEPTION 'derived identifier is too long';
                    END IF;

                    FOR field IN SELECT value FROM jsonb_array_elements(p_fields)
                    LOOP
                        IF jsonb_typeof(field) <> 'object' THEN
                            RAISE EXCEPTION 'each field must be an object';
                        END IF;
                        field_name := field->>'name';
                        field_type := lower(field->>'type');
                        field_required := coalesce((field->>'required')::boolean, false);
                        IF field_name IS NULL OR field_name !~ '^[A-Za-z][A-Za-z0-9_]{0,62}$' THEN
                            RAISE EXCEPTION 'invalid field name';
                        END IF;
                        IF lower(field_name) IN ('rawmessageid', lower(logical_id)) THEN
                            RAISE EXCEPTION 'automatic field % cannot be declared', field_name;
                        END IF;
                        field_column := ltrim(lower(regexp_replace(field_name, '([A-Z])', '_\1', 'g')), '_');
                        IF length(field_column) > 63 OR field_column = ANY(seen_columns) THEN
                            RAISE EXCEPTION 'duplicate or invalid derived field %', field_column;
                        END IF;
                        seen_columns := array_append(seen_columns, field_column);
                        field_type := CASE field_type
                            WHEN 'text' THEN 'text'
                            WHEN 'integer' THEN 'bigint'
                            WHEN 'decimal' THEN 'numeric'
                            WHEN 'boolean' THEN 'boolean'
                            WHEN 'datetime' THEN 'timestamptz'
                            WHEN 'json' THEN 'jsonb'
                            WHEN 'point' THEN 'geometry(Point,4326)'
                            WHEN 'line' THEN 'geometry(LineString,4326)'
                            WHEN 'polygon' THEN 'geometry(Polygon,4326)'
                            ELSE NULL
                        END;
                        IF field_type IS NULL THEN
                            RAISE EXCEPTION 'unsupported field type';
                        END IF;
                        column_sql := column_sql || format(', %I %s%s', field_column, field_type,
                            CASE WHEN field_required THEN ' NOT NULL' ELSE '' END);
                    END LOOP;

                    EXECUTE format(
                        'CREATE TABLE public.%I (%I bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, raw_message_id bigint NOT NULL REFERENCES public.raw_messages(raw_message_id) ON DELETE CASCADE%s)',
                        physical_table, id_column, column_sql);
                    EXECUTE format('CREATE INDEX %I ON public.%I(raw_message_id)',
                        left('ix_' || physical_table || '_raw_message_id', 54) || '_' || substr(md5(physical_table), 1, 8),
                        physical_table);
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_reader') THEN
                        EXECUTE format('GRANT SELECT ON TABLE public.%I TO puluj_reader', physical_table);
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_ee') THEN
                        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.%I TO puluj_ee', physical_table);
                        entity_sequence := pg_get_serial_sequence(format('public.%I', physical_table), id_column);
                        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO puluj_ee', entity_sequence);
                    END IF;

                    INSERT INTO public.ee_entity_definitions
                        (entity_name, table_name, fields, map_settings, enabled, created_at, updated_at)
                    VALUES
                        (normalized_entity, physical_table, p_fields, coalesce(p_map_settings, '{}'::jsonb),
                         coalesce(p_enabled, true), now(), now())
                    RETURNING to_jsonb(ee_entity_definitions) INTO definition;
                    RETURN definition;
                END
                $function$;

                REVOKE ALL ON FUNCTION ee_create_entity_definition(text, jsonb, jsonb, boolean) FROM PUBLIC;
                DO $grant$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_reader') THEN
                        REVOKE ALL ON FUNCTION ee_create_entity_definition(text, jsonb, jsonb, boolean) FROM puluj_reader;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                        GRANT EXECUTE ON FUNCTION ee_create_entity_definition(text, jsonb, jsonb, boolean) TO puluj_admin;
                    END IF;
                END
                $grant$;

                DO $ee_role$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_ee') THEN
                        CREATE ROLE puluj_ee LOGIN PASSWORD 'puluj_ee';
                    END IF;
                    EXECUTE format('GRANT CONNECT ON DATABASE %I TO puluj_ee', current_database());
                END
                $ee_role$;
                GRANT USAGE ON SCHEMA public TO puluj_ee;

                GRANT SELECT ON TABLE ee_extractors, ee_entity_definitions TO puluj_ee;
                GRANT SELECT, INSERT, UPDATE ON TABLE ee_processing_runs, ee_extractor_runs TO puluj_ee;
                GRANT INSERT ON TABLE ee_entity_writes TO puluj_ee;
                GRANT INSERT ON TABLE llm_requests TO puluj_ee;
                GRANT SELECT (llm_request_id) ON TABLE llm_requests TO puluj_ee;
                GRANT SELECT, INSERT ON TABLE
                    ee_targets, ee_tracks, ee_alerts, ee_impacts, ee_explosions,
                    ee_air_defense_actions, ee_launches, ee_takeoffs
                TO puluj_ee;

                CREATE FUNCTION ee_get_llm_settings()
                RETURNS TABLE(key text, value text)
                LANGUAGE sql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $function$
                    SELECT settings.key, settings.value
                    FROM public.app_settings AS settings
                    WHERE settings.key LIKE 'Llm:%'
                $function$;
                REVOKE ALL ON FUNCTION ee_get_llm_settings() FROM PUBLIC, puluj_reader;
                GRANT EXECUTE ON FUNCTION ee_get_llm_settings() TO puluj_ee;
                CREATE FUNCTION ee_finalize_llm_request(
                    p_llm_request_id bigint, p_outcome text, p_status_code integer, p_duration_ms integer,
                    p_input_tokens integer, p_cache_creation_input_tokens integer,
                    p_cache_read_input_tokens integer, p_output_tokens integer,
                    p_estimated_cost_usd numeric, p_facts_count integer, p_response_text text,
                    p_response_payload jsonb, p_error text)
                RETURNS boolean
                LANGUAGE sql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $function$
                    UPDATE public.llm_requests
                    SET outcome=p_outcome, status_code=p_status_code, duration_ms=p_duration_ms,
                        input_tokens=p_input_tokens,
                        cache_creation_input_tokens=p_cache_creation_input_tokens,
                        cache_read_input_tokens=p_cache_read_input_tokens,
                        output_tokens=p_output_tokens, estimated_cost_usd=p_estimated_cost_usd,
                        facts_count=p_facts_count, response_text=p_response_text,
                        response_payload=p_response_payload, error=p_error
                    WHERE llm_request_id=p_llm_request_id AND worker='entity-extractor'
                    RETURNING true
                $function$;
                REVOKE ALL ON FUNCTION ee_finalize_llm_request(bigint, text, integer, integer, integer, integer, integer, integer, numeric, integer, text, jsonb, text) FROM PUBLIC, puluj_reader;
                GRANT EXECUTE ON FUNCTION ee_finalize_llm_request(bigint, text, integer, integer, integer, integer, integer, integer, numeric, integer, text, jsonb, text) TO puluj_ee;
                GRANT USAGE, SELECT ON SEQUENCE
                    ee_processing_runs_processing_run_id_seq,
                    ee_extractor_runs_extractor_run_id_seq,
                    ee_entity_writes_entity_write_id_seq,
                    llm_requests_llm_request_id_seq
                TO puluj_ee;
                DO $entity_sequence_grants$
                DECLARE entity_table text;
                        entity_id_column text;
                        entity_sequence text;
                BEGIN
                    FOR entity_table, entity_id_column IN
                        SELECT * FROM (VALUES
                            ('ee_targets', 'targetId'),
                            ('ee_tracks', 'trackId'),
                            ('ee_alerts', 'alertId'),
                            ('ee_impacts', 'impactId'),
                            ('ee_explosions', 'explosionId'),
                            ('ee_air_defense_actions', 'airDefenseActionId'),
                            ('ee_launches', 'launchId'),
                            ('ee_takeoffs', 'takeoffId')) AS seeded(table_name, id_column)
                    LOOP
                        entity_sequence := pg_get_serial_sequence(
                            format('public.%I', entity_table), entity_id_column);
                        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO puluj_ee', entity_sequence);
                    END LOOP;
                END
                $entity_sequence_grants$;

                -- The public API can read the registry and concrete entity rows, but delivery state,
                -- extractor source code and processing audit stay outside its database role.
                REVOKE ALL PRIVILEGES ON TABLE
                    ee_delivery_queue, ee_delivery_attempts, ee_extractors,
                    ee_processing_runs, ee_extractor_runs, ee_entity_writes
                FROM puluj_reader;
                GRANT SELECT ON TABLE
                    ee_entity_definitions,
                    ee_targets, ee_tracks, ee_alerts, ee_impacts, ee_explosions,
                    ee_air_defense_actions, ee_launches, ee_takeoffs
                TO puluj_reader;

                CREATE FUNCTION ee_enqueue_raw_message(p_raw_message_id bigint)
                RETURNS integer LANGUAGE plpgsql AS $$
                DECLARE inserted integer;
                BEGIN
                    INSERT INTO ee_delivery_queue (raw_message_id, origin, status, enqueued_at, attempts)
                    SELECT raw_message_id, 'manual', 'pending', now(), 0
                    FROM raw_messages WHERE raw_message_id = p_raw_message_id;
                    GET DIAGNOSTICS inserted = ROW_COUNT;
                    RETURN inserted;
                END $$;

                CREATE FUNCTION ee_enqueue_raw_messages(p_raw_message_ids bigint[])
                RETURNS integer LANGUAGE plpgsql AS $$
                DECLARE inserted integer;
                BEGIN
                    INSERT INTO ee_delivery_queue (raw_message_id, origin, status, enqueued_at, attempts)
                    SELECT r.raw_message_id, 'manual', 'pending', now(), 0
                    FROM raw_messages r
                    JOIN (SELECT DISTINCT unnest(p_raw_message_ids) AS raw_message_id) requested USING (raw_message_id);
                    GET DIAGNOSTICS inserted = ROW_COUNT;
                    RETURN inserted;
                END $$;

                CREATE FUNCTION ee_enqueue_raw_messages(
                    p_from_id bigint, p_to_id bigint, p_source_id integer,
                    p_from_published_at timestamptz, p_to_published_at timestamptz, p_limit integer)
                RETURNS integer LANGUAGE plpgsql AS $$
                DECLARE inserted integer;
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;
                    INSERT INTO ee_delivery_queue (raw_message_id, origin, status, enqueued_at, attempts)
                    SELECT r.raw_message_id, 'manual', 'pending', now(), 0
                    FROM raw_messages r
                    WHERE (p_from_id IS NULL OR r.raw_message_id >= p_from_id)
                      AND (p_to_id IS NULL OR r.raw_message_id <= p_to_id)
                      AND (p_source_id IS NULL OR r.source_id = p_source_id)
                      AND (p_from_published_at IS NULL OR r.published_at >= p_from_published_at)
                      AND (p_to_published_at IS NULL OR r.published_at <= p_to_published_at)
                    ORDER BY r.raw_message_id
                    LIMIT p_limit;
                    GET DIAGNOSTICS inserted = ROW_COUNT;
                    RETURN inserted;
                END $$;

                CREATE FUNCTION ee_enqueue_raw_message_after_insert()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO ee_delivery_queue (raw_message_id, origin, status, enqueued_at, attempts)
                    VALUES (NEW.raw_message_id, 'live', 'pending', now(), 0)
                    ON CONFLICT (raw_message_id) WHERE origin = 'live' DO NOTHING;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER ee_enqueue_raw_message
                    AFTER INSERT ON raw_messages
                    FOR EACH ROW EXECUTE FUNCTION ee_enqueue_raw_message_after_insert();

                REVOKE ALL ON FUNCTION ee_enqueue_raw_message(bigint) FROM PUBLIC, puluj_reader;
                REVOKE ALL ON FUNCTION ee_enqueue_raw_messages(bigint[]) FROM PUBLIC, puluj_reader;
                REVOKE ALL ON FUNCTION ee_enqueue_raw_messages(bigint, bigint, integer, timestamptz, timestamptz, integer) FROM PUBLIC, puluj_reader;
                GRANT EXECUTE ON FUNCTION ee_enqueue_raw_message(bigint) TO puluj_admin;
                GRANT EXECUTE ON FUNCTION ee_enqueue_raw_messages(bigint[]) TO puluj_admin;
                GRANT EXECUTE ON FUNCTION ee_enqueue_raw_messages(bigint, bigint, integer, timestamptz, timestamptz, integer) TO puluj_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $drop_registered_entity_tables$
                DECLARE registered_table text;
                BEGIN
                    FOR registered_table IN
                        SELECT table_name
                        FROM ee_entity_definitions
                        WHERE table_name ~ '^ee_[a-z][a-z0-9_]{0,59}$'
                          AND table_name NOT IN (
                              'ee_delivery_queue', 'ee_delivery_attempts', 'ee_extractors',
                              'ee_entity_definitions', 'ee_processing_runs', 'ee_extractor_runs',
                              'ee_entity_writes')
                    LOOP
                        EXECUTE format('DROP TABLE IF EXISTS public.%I', registered_table);
                    END LOOP;
                END
                $drop_registered_entity_tables$;

                DROP FUNCTION IF EXISTS ee_create_entity_definition(text, jsonb, jsonb, boolean);
                DROP FUNCTION IF EXISTS ee_get_llm_settings();
                DROP FUNCTION IF EXISTS ee_finalize_llm_request(bigint, text, integer, integer, integer, integer, integer, integer, numeric, integer, text, jsonb, text);
                DROP TRIGGER IF EXISTS ee_enqueue_raw_message ON raw_messages;
                DROP FUNCTION IF EXISTS ee_enqueue_raw_message_after_insert();
                DROP FUNCTION IF EXISTS ee_enqueue_raw_messages(bigint, bigint, integer, timestamptz, timestamptz, integer);
                DROP FUNCTION IF EXISTS ee_enqueue_raw_messages(bigint[]);
                DROP FUNCTION IF EXISTS ee_enqueue_raw_message(bigint);
                DROP TABLE IF EXISTS ee_takeoffs;
                DROP TABLE IF EXISTS ee_launches;
                DROP TABLE IF EXISTS ee_air_defense_actions;
                DROP TABLE IF EXISTS ee_explosions;
                DROP TABLE IF EXISTS ee_impacts;
                DROP TABLE IF EXISTS ee_alerts;
                DROP TABLE IF EXISTS ee_tracks;
                DROP TABLE IF EXISTS ee_targets;
                """);

            migrationBuilder.DropTable(
                name: "ee_delivery_attempts");

            migrationBuilder.DropTable(
                name: "ee_entity_writes");

            migrationBuilder.DropTable(
                name: "ee_entity_definitions");

            migrationBuilder.DropTable(
                name: "ee_extractor_runs");

            migrationBuilder.DropTable(
                name: "ee_extractors");

            migrationBuilder.DropTable(
                name: "ee_processing_runs");

            migrationBuilder.DropTable(
                name: "ee_delivery_queue");

            migrationBuilder.Sql(
                """
                DO $drop_ee_role$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_ee') THEN
                        DROP OWNED BY puluj_ee;
                        EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM puluj_ee', current_database());
                        DROP ROLE puluj_ee;
                    END IF;
                END
                $drop_ee_role$;
                """);
        }
    }
}
