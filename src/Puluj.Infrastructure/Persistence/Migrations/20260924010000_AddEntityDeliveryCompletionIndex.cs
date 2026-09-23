using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>Latest successful completion without scanning/sorting the successful delivery history on every admin poll.</summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260924010000_AddEntityDeliveryCompletionIndex")]
public partial class AddEntityDeliveryCompletionIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_ee_delivery_queue_success_completed_at
            ON ee_delivery_queue (completed_at DESC)
            WHERE status = 'succeeded' AND completed_at IS NOT NULL;
        """, suppressTransaction: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP INDEX CONCURRENTLY IF EXISTS ix_ee_delivery_queue_success_completed_at;", suppressTransaction: true);
}
