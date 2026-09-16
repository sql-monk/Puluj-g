using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_processing_runs_open_replay",
                schema: "processing",
                table: "runs",
                column: "kind",
                unique: true,
                filter: "kind = 'replay' AND state IN ('created', 'running', 'paused', 'verified')");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_events_run",
                schema: "messaging",
                table: "events",
                column: "processing_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_processing_runs_open_replay",
                schema: "processing",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "ix_messaging_events_run",
                schema: "messaging",
                table: "events");
        }
    }
}
