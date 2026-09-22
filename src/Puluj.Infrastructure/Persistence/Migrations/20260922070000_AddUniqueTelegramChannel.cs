using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// One Telegram channel, one source (docs/naming.md, "Коди джерел"): the seeder and the admin API already refuse a
/// second row for a channel the database has under another code; this makes the database refuse it too, whatever
/// wrote the row. Expression index on the jsonb config, so it lives in SQL rather than in the EF model.
/// A pre-check names the offending channels instead of leaving the operator with a bare unique-violation.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260922070000_AddUniqueTelegramChannel")]
public partial class AddUniqueTelegramChannel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$
        DECLARE dupes text;
        BEGIN
            SELECT string_agg(channel || ' (' || codes || ')', ', ') INTO dupes
            FROM (
                SELECT lower(config->>'channel') AS channel, string_agg(code, ', ' ORDER BY code) AS codes
                FROM sources
                WHERE type = 2 AND config->>'channel' IS NOT NULL
                GROUP BY lower(config->>'channel')
                HAVING count(*) > 1) d;
            IF dupes IS NOT NULL THEN
                RAISE EXCEPTION 'sources: the same Telegram channel under several codes: %. Disable the duplicate, delete its raw messages and the row, then migrate again (docs/naming.md).', dupes;
            END IF;
        END $$;

        CREATE UNIQUE INDEX ux_sources_telegram_channel ON sources (lower(config->>'channel')) WHERE type = 2;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP INDEX IF EXISTS ux_sources_telegram_channel;");
}
