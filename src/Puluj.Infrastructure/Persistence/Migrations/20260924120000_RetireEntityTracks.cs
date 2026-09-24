using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>Retire EE tracks without deleting historical rows or changing the legacy pipeline.</summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260924120000_RetireEntityTracks")]
public partial class RetireEntityTracks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE ee_extractors SET enabled = false WHERE name = 'track';
            UPDATE ee_entity_definitions
            SET enabled = false,
                map_settings = coalesce(map_settings, '{}'::jsonb) || '{"enabled":false}'::jsonb
            WHERE entity_name = 'track' OR table_name = 'ee_tracks';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not guess the previous enabled state or resurrect retired extraction on rollback.
        // An administrator can deliberately restore it from the retained definition and code.
    }
}
