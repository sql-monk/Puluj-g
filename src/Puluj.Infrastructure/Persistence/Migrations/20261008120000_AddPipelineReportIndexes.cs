using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// The admin pipeline report (<c>GET /ops/pipeline</c>, polled every 15 s) and the workers tab (every 5 s) read the whole of
/// raw_messages on every call: on 2026-10-08 (7.2M rows, a 10 GB heap) one 24-hour report took ~20 s. Both indexes cover
/// their queries, so every raw_messages read of the two becomes an index-only scan of the period:
/// <list type="bullet">
/// <item><c>ix_raw_messages_status_processed_at</c>: outcomes by processed_at per status, the queue (statuses 0 and 4) per
/// source, the totals per status (the whole index instead of the heap) and the per-instance split by claimed_by. It also
/// serves everything <c>ix_raw_messages_processed_at_status1</c> did — an index created by hand on the live database and
/// never in a migration — so that one is dropped (IF EXISTS: other databases never had it).</item>
/// <item><c>ix_raw_messages_received_at_covering</c> replaces the BRIN on received_at: processing updates move rows, so
/// received_at no longer follows the physical order (correlation 0.19) and the BRIN returned ~1M lossy rows for a day of
/// 6k, read from 100k heap pages.</item>
/// </list>
/// Built CONCURRENTLY, which cannot run inside a transaction, so the collector keeps inserting while a live database migrates.
/// SQL-only like <c>ix_raw_messages_source_id_received_at</c>: neither belongs to the EF model.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20261008120000_AddPipelineReportIndexes")]
public partial class AddPipelineReportIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_raw_messages_status_processed_at
                ON raw_messages (processing_status, processed_at)
                INCLUDE (source_id, processing_ms, claimed_by, raw_message_id);
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_raw_messages_received_at_covering
                ON raw_messages (received_at)
                INCLUDE (source_id, published_at);
            """, suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_raw_messages_received_at;", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_raw_messages_processed_at_status1;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_raw_messages_received_at ON raw_messages USING brin (received_at);", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_raw_messages_received_at_covering;", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_raw_messages_status_processed_at;", suppressTransaction: true);
    }
}
