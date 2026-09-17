using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCopyDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Older databases have this legacy view from the former copy-detection schema.
            // PostgreSQL will not drop its source table while the view still depends on it.
            migrationBuilder.Sql("DROP VIEW IF EXISTS source_rating_daily;");

            migrationBuilder.DropTable(
                name: "source_copies");

            migrationBuilder.DropTable(
                name: "source_daily_stats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_copies",
                columns: table => new
                {
                    copier_source_id = table.Column<int>(type: "integer", nullable: false),
                    original_source_id = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    delay_seconds_sum = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_copies", x => new { x.copier_source_id, x.original_source_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "source_daily_stats",
                columns: table => new
                {
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    copied_by = table.Column<int>(type: "integer", nullable: false),
                    copies = table.Column<int>(type: "integer", nullable: false),
                    lead_seconds_sum = table.Column<double>(type: "double precision", nullable: false),
                    targets = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_daily_stats", x => new { x.source_id, x.day });
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_copies_day",
                table: "source_copies",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_source_daily_stats_day",
                table: "source_daily_stats",
                column: "day");
        }
    }
}
