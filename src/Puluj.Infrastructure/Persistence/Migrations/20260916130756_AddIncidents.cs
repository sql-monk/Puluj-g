using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    incident_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_kind_id = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    suppressed = table.Column<bool>(type: "boolean", nullable: false),
                    first_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location_kind = table.Column<int>(type: "integer", nullable: false),
                    location_place_id = table.Column<int>(type: "integer", nullable: true),
                    geometry = table.Column<Geometry>(type: "geography (geometry, 4326)", nullable: true),
                    accuracy_km = table.Column<double>(type: "double precision", nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    source_count = table.Column<int>(type: "integer", nullable: false),
                    independent_source_count = table.Column<int>(type: "integer", nullable: true),
                    canonical_observation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    closure_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    merged_into_incident_id = table.Column<long>(type: "bigint", nullable: true),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                name: "incident_observations",
                columns: table => new
                {
                    incident_id = table.Column<long>(type: "bigint", nullable: false),
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_target_id = table.Column<long>(type: "bigint", nullable: true),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    relation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    decision_reason = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    revision = table.Column<int>(type: "integer", nullable: false),
                    change = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    triggering_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    snapshot = table.Column<JsonDocument>(type: "jsonb", nullable: false)
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
                name: "ix_incidents_state_last_reported_at",
                table: "incidents",
                columns: new[] { "state", "last_reported_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_observations");

            migrationBuilder.DropTable(
                name: "incident_revisions");

            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}
