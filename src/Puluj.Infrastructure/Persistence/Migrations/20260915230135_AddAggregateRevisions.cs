using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAggregateRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            // targets is the largest table: build the partial unique index without an exclusive lock (outside the migration transaction, ADR-0006).
            // An interrupted CONCURRENTLY build leaves an INVALID index that IF NOT EXISTS would keep: drop it first, then build.
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ux_targets_observation_id", suppressTransaction: true);
            migrationBuilder.Sql("CREATE UNIQUE INDEX CONCURRENTLY ux_targets_observation_id ON targets (observation_id) WHERE observation_id IS NOT NULL", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ux_targets_observation_id", suppressTransaction: true);

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
                name: "last_correlation_id",
                table: "air_alerts");

            migrationBuilder.DropColumn(
                name: "last_event_id",
                table: "air_alerts");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "air_alerts");
        }
    }
}
