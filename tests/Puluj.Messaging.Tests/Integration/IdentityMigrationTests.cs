using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// `AddRawMessageIdentity` (P04, ADR-0003) on a fresh PostGIS: legacy rows written before the migration get their key/revision
/// by the identity-cases rules, the identity index is unique, the content hash is not; Down removes the columns and keeps the
/// rows (the unique hash is deliberately not restored).
/// </summary>
public sealed class IdentityMigrationTests : IAsyncLifetime
{
    private const string Before = "20260915150214_AddMessagingSchema";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.5").WithDatabase("puluj_migration_test").Build();
    private DbContextOptions<PulujDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Real PostGIS is required (Docker). No tests were executed.", ex);
        }
        var builder = new DbContextOptionsBuilder<PulujDbContext>();
        Puluj.Infrastructure.DependencyInjection.ConfigureDbContext(builder, _postgres.GetConnectionString());
        _options = builder.Options;
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Up_backfills_identity_from_legacy_ids_and_Down_keeps_the_rows()
    {
        await using var db = new PulujDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO sources (code, name, type, enabled, trust_level, priority) VALUES ('tg_monitoringwar', 'test', 0, true, 1, 0), ('alerts_in_ua', 'alerts', 1, true, 1, 0);
            INSERT INTO raw_messages (source_id, source_message_id, published_at, received_at, raw_text, hash, processing_status, attempts)
            SELECT s.source_id, v.id, now(), now(), v.id, md5(v.id), 0, 0
            FROM (VALUES ('48213'), ('48213:e1789466500'), ('31:start'), ('31:end'), ('weird:e12x')) AS v(id)
            JOIN sources s ON s.code = CASE WHEN v.id LIKE '31:%' THEN 'alerts_in_ua' ELSE 'tg_monitoringwar' END
            """);

        await migrator.MigrateAsync();

        var rows = await db.RawMessages.AsNoTracking().Select(r => new { r.SourceMessageId, r.SourceMessageKey, r.SourceRevision }).ToListAsync();
        var identity = rows.ToDictionary(r => r.SourceMessageId, r => (r.SourceMessageKey, r.SourceRevision));
        Assert.Equal(("48213", "0"), identity["48213"]);
        Assert.Equal(("48213", "e1789466500"), identity["48213:e1789466500"]);
        Assert.Equal(("31:start", "0"), identity["31:start"]);
        Assert.Equal(("31:end", "0"), identity["31:end"]);
        Assert.Equal(("weird:e12x", "0"), identity["weird:e12x"]); // not a Telegram edit suffix: whole id is the key
        Assert.Equal(5, identity.Count);

        // Identity is unique, content is not.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, hash, processing_status, attempts) SELECT source_id, 'dup', '48213', '0', now(), now(), 'x', 0, 0 FROM sources WHERE code = 'tg_monitoringwar'"));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO raw_messages (source_id, source_message_id, source_message_key, source_revision, published_at, received_at, hash, processing_status, attempts) SELECT source_id, '99', '99', '0', now(), now(), md5('48213'), 0, 0 FROM sources WHERE code = 'tg_monitoringwar'");
        Assert.Equal(2, await db.RawMessages.CountAsync(r => r.Hash == Md5("48213")));
        // No default on the identity columns: a pre-P04 writer fails loudly instead of storing everything under ('', '').
        Assert.Equal(["source_message_key|", "source_revision|"], (await ColumnsAsync()).Where(c => c.StartsWith("source_message_key|", StringComparison.Ordinal) || c.StartsWith("source_revision|", StringComparison.Ordinal)).Order());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO raw_messages (source_id, source_message_id, published_at, received_at, hash, processing_status, attempts) SELECT source_id, 'legacy-writer', now(), now(), 'y', 0, 0 FROM sources WHERE code = 'tg_monitoringwar'"));
        var indexes = await IndexesAsync();
        Assert.Contains("ix_raw_messages_source_id_source_message_key_source_revision|t", indexes);
        Assert.Contains("ix_raw_messages_hash|f", indexes);
        Assert.Contains("ix_raw_messages_source_id_source_message_id|t", indexes);

        // Rollback: columns gone, rows (including the two with equal content) kept, hash stays non-unique.
        await migrator.MigrateAsync(Before);
        var columns = (await ColumnsAsync()).Select(c => c.Split('|')[0]).ToList();
        Assert.DoesNotContain("source_message_key", columns);
        Assert.DoesNotContain("source_revision", columns);
        Assert.Contains("source_message_id", columns);
        var count = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM raw_messages").SingleAsync();
        Assert.Equal(6, count);
        indexes = await IndexesAsync();
        Assert.Contains("ix_raw_messages_hash|f", indexes);
        Assert.DoesNotContain(indexes, i => i.StartsWith("ix_raw_messages_source_id_source_message_key", StringComparison.Ordinal));

        // And forward again: the backfill is idempotent (only empty keys are filled) and the duplicate-content rows survive.
        await migrator.MigrateAsync();
        Assert.Equal(6, await db.RawMessages.CountAsync());
        Assert.Equal("99", await db.RawMessages.Where(r => r.SourceMessageId == "99").Select(r => r.SourceMessageKey).SingleAsync());
    }

    private static string Md5(string s) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    private async Task<List<string>> IndexesAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT i.relname || '|' || CASE WHEN ix.indisunique THEN 't' ELSE 'f' END FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_class t ON t.oid = ix.indrelid WHERE t.relname = 'raw_messages'", conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private async Task<List<string>> ColumnsAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT column_name || '|' || coalesce(column_default, '') FROM information_schema.columns WHERE table_name = 'raw_messages'", conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }
}
