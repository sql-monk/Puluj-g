using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// The rest of the admin pipeline report after <see cref="AddPipelineReportIndexes"/>: its month failed on the 30-s command
/// timeout because of targets.
/// <list type="bullet">
/// <item><c>ix_targets_observed_at_covering</c> replaces the BRIN on observed_at. A history load inserts years of events in
/// one go, so every block range spans years: on 2026-10-08 a month of 232k targets matched 4.8M of 6.4M rows and read the
/// 4 GB heap (53 s cold). Covering source_id and duplicate_of_target_id makes the report's two target queries index-only.</item>
/// <item><c>ix_target_tracks_first_seen_at</c>: the report counts the tracks first seen in the period; without it, a
/// sequential scan of target_tracks on every poll.</item>
/// </list>
/// Built CONCURRENTLY, which cannot run inside a transaction, so the processor keeps inserting while a live database migrates.
/// SQL-only like <c>ix_raw_messages_received_at_covering</c>: neither belongs to the EF model.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20261008180000_AddTargetReportIndexes")]
public partial class AddTargetReportIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_targets_observed_at_covering
                ON targets (observed_at)
                INCLUDE (source_id, duplicate_of_target_id);
            """, suppressTransaction: true);
        migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_target_tracks_first_seen_at ON target_tracks (first_seen_at);", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_targets_observed_at;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_targets_observed_at ON targets USING brin (observed_at);", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_target_tracks_first_seen_at;", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_targets_observed_at_covering;", suppressTransaction: true);
    }
}
