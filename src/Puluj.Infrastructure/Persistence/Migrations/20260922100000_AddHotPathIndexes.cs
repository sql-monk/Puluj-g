using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// Two indexes for queries that were reading whole tables on every call (2026-09-22, the database at 13 cores):
/// <list type="bullet">
/// <item>The entity-extractor claim (<c>WHERE status = 'pending' OR (status = 'in_progress' AND lease_expires_at &lt;= now())
/// ORDER BY enqueued_at, delivery_id LIMIT 1</c>): the OR defeats <c>ix_ee_delivery_queue_status_enqueued_at</c>, so with
/// millions of pending rows every claim was a seq scan plus an on-disk sort. A partial index keyed by the sort order and
/// restricted to the two claimable statuses turns it into one ordered index probe.</item>
/// <item>The latest raw message per source (admin channel titles), now a LATERAL probe per source.</item>
/// </list>
/// Built CONCURRENTLY, which cannot run inside a transaction, so the collector keeps inserting while a live database migrates.
/// SQL-only like <c>ux_sources_telegram_channel</c>: neither belongs to the EF model.
/// Also creates pg_stat_statements, the per-query statistics that found the two; it only reports once the library is
/// preloaded (deploy/docker-compose.yml, shared_preload_libraries).
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260922100000_AddHotPathIndexes")]
public partial class AddHotPathIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_stat_statements;");
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_ee_delivery_queue_claim
                ON ee_delivery_queue (enqueued_at, delivery_id)
                WHERE status IN ('pending', 'in_progress');
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_raw_messages_source_id_received_at
                ON raw_messages (source_id, received_at DESC, raw_message_id DESC);
            """, suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_ee_delivery_queue_claim;", suppressTransaction: true);
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_raw_messages_source_id_received_at;", suppressTransaction: true);
    }
}
