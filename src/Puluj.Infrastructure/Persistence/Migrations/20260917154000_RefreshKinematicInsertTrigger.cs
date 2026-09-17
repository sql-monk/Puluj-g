using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// Replaces the legacy target insert trigger left on upgraded databases after source copy statistics were removed.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260917154000_RefreshKinematicInsertTrigger")]
public partial class RefreshKinematicInsertTrigger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql.KinematicsSql.OnTargetInsert);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The retired implementation deliberately is not restored.
    }
}
