using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>Lets the restricted admin service run VACUUM, ANALYZE and concurrent REINDEX on application tables.</summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260917200000_GrantAdminTableMaintenance")]
public partial class GrantAdminTableMaintenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                GRANT MAINTAIN ON ALL TABLES IN SCHEMA public TO puluj_admin;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT MAINTAIN ON TABLES TO puluj_admin;
            END IF;
        END $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE MAINTAIN ON TABLES FROM puluj_admin;
                REVOKE MAINTAIN ON ALL TABLES IN SCHEMA public FROM puluj_admin;
            END IF;
        END $$;
        """);
}
