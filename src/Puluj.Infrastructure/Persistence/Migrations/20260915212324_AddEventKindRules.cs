using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventKindRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_kind_rule_shadow",
                columns: table => new
                {
                    shadow_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    live_version = table.Column<int>(type: "integer", nullable: false),
                    shadow_version = table.Column<int>(type: "integer", nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    live_kind = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    shadow_kind = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    live_rule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    shadow_rule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_rule_shadow", x => x.shadow_id);
                });

            migrationBuilder.CreateTable(
                name: "event_kind_ruleset_audit",
                columns: table => new
                {
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_ruleset_audit", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "event_kind_rulesets",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    parent_version = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    notes = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_rulesets", x => x.version);
                    table.CheckConstraint("ck_event_kind_rulesets_state", "state IN ('draft', 'shadow', 'published', 'superseded')");
                });

            migrationBuilder.CreateTable(
                name: "event_kind_rules",
                columns: table => new
                {
                    rule_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ruleset_version = table.Column<int>(type: "integer", nullable: false),
                    rule_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_kind_id = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    source_scope = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    positive_patterns = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    negative_patterns = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    extraction_hints = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    confidence_modifier = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_kind_rules", x => x.rule_id);
                    table.ForeignKey(
                        name: "fk_event_kind_rules_event_kind_rulesets_ruleset_version",
                        column: x => x.ruleset_version,
                        principalTable: "event_kind_rulesets",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_event_kind_rules_event_kinds_event_kind_id",
                        column: x => x.event_kind_id,
                        principalTable: "event_kinds",
                        principalColumn: "event_kind_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rule_shadow_raw_message_id_run_id",
                table: "event_kind_rule_shadow",
                columns: new[] { "raw_message_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rule_shadow_shadow_version_created_at",
                table: "event_kind_rule_shadow",
                columns: new[] { "shadow_version", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rules_event_kind_id",
                table: "event_kind_rules",
                column: "event_kind_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_rules_ruleset_version_rule_code",
                table: "event_kind_rules",
                columns: new[] { "ruleset_version", "rule_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_kind_ruleset_audit_version_at",
                table: "event_kind_ruleset_audit",
                columns: new[] { "version", "at" });

            migrationBuilder.CreateIndex(
                name: "ux_event_kind_rulesets_active",
                table: "event_kind_rulesets",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_event_kind_rulesets_shadow",
                table: "event_kind_rulesets",
                column: "state",
                unique: true,
                filter: "state = 'shadow'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_kind_rule_shadow");

            migrationBuilder.DropTable(
                name: "event_kind_rules");

            migrationBuilder.DropTable(
                name: "event_kind_ruleset_audit");

            migrationBuilder.DropTable(
                name: "event_kind_rulesets");
        }
    }
}
