using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmRequestAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_requests",
                columns: table => new
                {
                    llm_request_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    raw_message_id = table.Column<long>(type: "bigint", nullable: true),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    worker = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cache_creation_input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cache_read_input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: true),
                    facts_count = table.Column<int>(type: "integer", nullable: false),
                    request_text = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: false),
                    response_text = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_requests", x => x.llm_request_id);
                    table.ForeignKey(
                        name: "fk_llm_requests_raw_messages_raw_message_id",
                        column: x => x.raw_message_id,
                        principalTable: "raw_messages",
                        principalColumn: "raw_message_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_llm_requests_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "sources",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_requests_occurred_at",
                table: "llm_requests",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_llm_requests_raw_message_id",
                table: "llm_requests",
                column: "raw_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_llm_requests_source_id_occurred_at",
                table: "llm_requests",
                columns: new[] { "source_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_requests");
        }
    }
}
