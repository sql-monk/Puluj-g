using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Analytics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCopyDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copies",
                schema: "analytics");

            migrationBuilder.DropIndex(
                name: "ix_messages_bands",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_source_id_channel_id",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "pairs_found",
                schema: "analytics",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "bands",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "channel_id",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "forwarded_external",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "forwarded_from",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "forwarded_source_id",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "min_hash",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "shingle_count",
                schema: "analytics",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "text_length",
                schema: "analytics",
                table: "messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pairs_found",
                schema: "analytics",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long[]>(
                name: "bands",
                schema: "analytics",
                table: "messages",
                type: "bigint[]",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "channel_id",
                schema: "analytics",
                table: "messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "forwarded_external",
                schema: "analytics",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "forwarded_from",
                schema: "analytics",
                table: "messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "forwarded_source_id",
                schema: "analytics",
                table: "messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "min_hash",
                schema: "analytics",
                table: "messages",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "shingle_count",
                schema: "analytics",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "text_length",
                schema: "analytics",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "copies",
                schema: "analytics",
                columns: table => new
                {
                    copy_source_id = table.Column<int>(type: "integer", nullable: false),
                    copy_post_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    original_source_id = table.Column<int>(type: "integer", nullable: false),
                    original_post_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    containment = table.Column<float>(type: "real", nullable: false),
                    copy_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    copy_raw_message_id = table.Column<long>(type: "bigint", nullable: false),
                    delay_seconds = table.Column<double>(type: "double precision", nullable: false),
                    found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    jaccard = table.Column<float>(type: "real", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    original_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    original_raw_message_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_copies", x => new { x.copy_source_id, x.copy_post_key, x.original_source_id, x.original_post_key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_bands",
                schema: "analytics",
                table: "messages",
                column: "bands",
                filter: "bands IS NOT NULL")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_messages_source_id_channel_id",
                schema: "analytics",
                table: "messages",
                columns: new[] { "source_id", "channel_id" });

            migrationBuilder.CreateIndex(
                name: "ix_copies_copy_published_at",
                schema: "analytics",
                table: "copies",
                column: "copy_published_at");

            migrationBuilder.CreateIndex(
                name: "ix_copies_copy_source_id_original_source_id_copy_published_at",
                schema: "analytics",
                table: "copies",
                columns: new[] { "copy_source_id", "original_source_id", "copy_published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_copies_original_raw_message_id",
                schema: "analytics",
                table: "copies",
                column: "original_raw_message_id");
        }
    }
}
