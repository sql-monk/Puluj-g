using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// Lets the restricted admin service execute the guarded operational-data reset.
/// The API controls which operational tables are included; this migration grants no
/// schema ownership or DDL rights.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260917180000_GrantAdminOperationalTruncate")]
public partial class GrantAdminOperationalTruncate : Migration
{
    private const string Schemas = "'public', 'analytics', 'messaging', 'processing'";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql($"""
        DO $$
        DECLARE schema_name text;
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                FOREACH schema_name IN ARRAY ARRAY[{Schemas}] LOOP
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = schema_name) THEN
                        EXECUTE format('GRANT USAGE ON SCHEMA %I TO puluj_admin', schema_name);
                        EXECUTE format('GRANT TRUNCATE ON ALL TABLES IN SCHEMA %I TO puluj_admin', schema_name);
                        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT TRUNCATE ON TABLES TO puluj_admin', schema_name);
                    END IF;
                END LOOP;
            END IF;
        END $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql($"""
        DO $$
        DECLARE schema_name text;
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                FOREACH schema_name IN ARRAY ARRAY[{Schemas}] LOOP
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = schema_name) THEN
                        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I REVOKE TRUNCATE ON TABLES FROM puluj_admin', schema_name);
                        EXECUTE format('REVOKE TRUNCATE ON ALL TABLES IN SCHEMA %I FROM puluj_admin', schema_name);
                    END IF;
                END LOOP;
            END IF;
        END $$;
        """);
}
