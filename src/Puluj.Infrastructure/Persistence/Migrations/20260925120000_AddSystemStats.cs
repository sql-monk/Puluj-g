using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// Enables the system_stats extension supplied by the custom PostgreSQL image.
/// It exposes host CPU, memory, disk, network, and per-process metrics through
/// pg_sys_* functions for operational diagnostics.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260925120000_AddSystemStats")]
public partial class AddSystemStats : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS system_stats;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the operational extension and monitor_system_stats role in place
        // when rolling the application schema back.
    }
}
