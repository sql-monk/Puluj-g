using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// Replaces the legacy duplicate trigger left on upgraded databases after source copy statistics were removed.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260917190000_RefreshKinematicDuplicateTrigger")]
public partial class RefreshKinematicDuplicateTrigger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql.KinematicsSql.OnTargetDuplicate);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The retired implementation deliberately is not restored.
    }
}
