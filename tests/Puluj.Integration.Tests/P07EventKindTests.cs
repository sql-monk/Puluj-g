using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing.Indexes;
using Puluj.Processing.Pipeline;

namespace Puluj.Integration.Tests;

/// <summary>
/// P07 / plan §8.2 on a real PostGIS: clean migration + seed parity, batch backfill with its report, FK RESTRICT,
/// the pipeline writing both the legacy enum and the catalog id, a production-shaped backfill with EXPLAIN, and the
/// migration's Down. Evidence JSON goes to PULUJ_EVIDENCE_DIRECTORY when set.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class P07EventKindTests(PipelineFixture fixture)
{
    private ServiceProvider Services => fixture.Services!;
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private static readonly DateTimeOffset At = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly int[] LegacyValues = Enum.GetValues<EventType>().Select(t => (int)t).ToArray();

    [Fact]
    public async Task Seed_parity_reseed_is_a_noop_and_newer_policy_refreshes_presentation_only()
    {
        var file = await Services.GetRequiredService<SeedFiles>().ReadAsync<EventKindSeeder.EventKindsFile>(EventKindSeeder.FileName, CancellationToken.None);
        Assert.NotNull(file);
        await using var db = await Factory.CreateDbContextAsync();
        var rows = await db.EventKinds.AsNoTracking().OrderBy(k => k.Code).ToListAsync();
        Assert.Equal(file.Kinds.Select(k => k.Code).Order(StringComparer.Ordinal), rows.Select(r => r.Code));
        Assert.All(rows, r => Assert.Equal(file.PolicyVersion, r.PolicyVersion));
        Assert.Equal("target", await db.Database.SqlQueryRaw<string>("SELECT category AS \"Value\" FROM event_kinds WHERE code = 'target.observed'").SingleAsync());
        Assert.Equal(TimeSpan.FromMinutes(30), rows.Single(r => r.Code == "target.observed").MapLifetime);

        // Same file again: nothing changes.
        var seeder = Services.GetServices<ISeeder>().OfType<EventKindSeeder>().Single();
        var before = await Snapshot(db);
        await seeder.SeedAsync(db, CancellationToken.None);
        Assert.Equal(before, await Snapshot(db));

        // The admin disabled a kind and a newer seed changes its colour: presentation follows the file, Enabled stays, code untouched.
        var fire = await db.EventKinds.SingleAsync(k => k.Code == "fire.reported");
        fire.Enabled = false;
        await db.SaveChangesAsync();
        var newer = file with
        {
            PolicyVersion = file.PolicyVersion + 1,
            Kinds = file.Kinds.Select(k => k.Code == "fire.reported" ? k with { MapColor = "#000000", NameUk = "Пожежа (оновлено)" } : k).ToList(),
        };
        await SeederFor(newer).SeedAsync(db, CancellationToken.None);
        db.ChangeTracker.Clear();
        var refreshed = await db.EventKinds.AsNoTracking().SingleAsync(k => k.Code == "fire.reported");
        Assert.Equal("#000000", refreshed.MapColor);
        Assert.Equal("Пожежа (оновлено)", refreshed.NameUk);
        Assert.False(refreshed.Enabled);
        Assert.Equal(file.PolicyVersion + 1, refreshed.PolicyVersion);
        Assert.Equal(file.Kinds.Count, await db.EventKinds.CountAsync()); // no duplicates, no deletions

        // Restore the shipped seed state for the other tests (the DB owns the rows; this is test housekeeping).
        await db.Database.ExecuteSqlRawAsync("UPDATE event_kinds SET enabled = true, policy_version = 0");
        await seeder.SeedAsync(db, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task Backfill_maps_every_legacy_value_reports_unresolved_and_is_idempotent()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await Truncate(db);
        var (sourceId, rawId) = await RawFor(db, "p07-backfill");
        // One target per legacy enum value plus one with an enum value the map does not know (must stay NULL, never guessed).
        foreach (var value in LegacyValues.Append(999))
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO targets (raw_message_id, source_id, segment_index, observed_at, event_type, alert_level, model_confidence, classification_confidence, identification_method, object_count_is_approximate, location_kind, direction_kind, direction_confidence, confidence, parser_version) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, 0, 0, 0, 1, false, 0, 0, 0, 2, 'p07')", rawId, sourceId, value, At, value);
        }
        Assert.Equal(LegacyValues.Length + 1, await db.Targets.CountAsync(t => t.EventKindId == null));

        var report = await EventKindBackfill.RunAsync(db, batchSize: 3, CancellationToken.None);
        Assert.Equal(LegacyValues.Length + 1, report.TotalTargets);
        Assert.Equal(LegacyValues.Length + 1, report.NullBefore);
        Assert.Equal(LegacyValues.Length, report.Updated);
        Assert.Equal(1, report.NullAfter);
        Assert.True(report.Batches >= 3, $"batches {report.Batches}");
        Assert.Equal(new Dictionary<int, long> { [999] = 1 }, report.UnresolvedByEventType);
        Assert.Equal(LegacyValues.Length, report.PerKind.Values.Sum());
        Assert.InRange(report.Coverage, 0.87, 0.88);

        db.ChangeTracker.Clear();
        var rows = await db.Targets.AsNoTracking().Include(t => t.EventKind).OrderBy(t => t.TargetId).ToListAsync();
        foreach (var t in rows.Where(t => (int)t.EventType != 999))
        {
            Assert.Equal(EventKindLegacyMap.ToCode(t.EventType), t.EventKind!.Code); // legacy enum kept, code matches the map
        }
        Assert.Null(rows.Single(t => (int)t.EventType == 999).EventKindId);

        var again = await EventKindBackfill.RunAsync(db, batchSize: 3, CancellationToken.None);
        Assert.Equal(0, again.Updated);
        Assert.Equal(1, again.NullAfter);
        await Truncate(db);
    }

    [Fact]
    public async Task Foreign_key_restricts_delete_of_a_referenced_kind_and_code_is_unique()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await Truncate(db);
        var (sourceId, rawId) = await RawFor(db, "p07-fk");
        var kindId = await db.EventKinds.Where(k => k.Code == "target.observed").Select(k => k.EventKindId).SingleAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO targets (raw_message_id, source_id, segment_index, observed_at, event_type, event_kind_id, alert_level, model_confidence, classification_confidence, identification_method, object_count_is_approximate, location_kind, direction_kind, direction_confidence, confidence, parser_version) " +
            "VALUES ({0}, {1}, 0, {2}, 1, {3}, 0, 0, 0, 1, false, 0, 0, 0, 2, 'p07')", rawId, sourceId, At, kindId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM event_kinds WHERE event_kind_id = {0}", kindId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        var dup = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO event_kinds (code, name_uk, category, requires_location_for_map, creates_incident, enabled, map_visible, sort_order, policy_version) VALUES ('target.observed', 'dup', 'target', false, false, true, true, 0, 1)"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, dup.SqlState);
        await Truncate(db);
    }

    [Fact]
    public async Task Pipeline_writes_catalog_id_next_to_legacy_enum_for_text_and_structured_messages()
    {
        var ingestor = Services.GetRequiredService<RawMessageIngestor>();
        var processor = Services.GetRequiredService<RawMessageProcessor>();
        var indexes = Services.GetRequiredService<IndexProvider>();
        await indexes.RefreshAsync(CancellationToken.None);
        Assert.False(indexes.EventKinds.IsEmpty);

        await using var db = await Factory.CreateDbContextAsync();
        await Truncate(db);
        var telegram = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
        var alerts = await db.Sources.Where(s => s.Code == "alerts_in_ua").Select(s => s.SourceId).SingleAsync();

        var text = await ingestor.IngestAsync(new()
        {
            SourceId = telegram, SourceMessageId = "p07-text", PublishedAt = At, RawText = "Шахеди на Сумщині курсом на Полтавщину.",
            RawPayload = JsonDocument.Parse("{\"kind\":\"test\"}"),
        }, "tg_kpszsu", CancellationToken.None, enqueue: false);
        var structured = await ingestor.IngestAsync(new()
        {
            SourceId = alerts, SourceMessageId = "p07-alert:start", PublishedAt = At,
            RawPayload = JsonSerializer.SerializeToDocument(new
            {
                kind = "alert.started", at = At,
                alert = new { id = "p07-31", location_title = "Сумська область", location_oblast = "Сумська область", location_type = "oblast", alert_type = "air_raid", started_at = At },
            }),
        }, "alerts_in_ua", CancellationToken.None, enqueue: false);
        Assert.Equal(1, await processor.ProcessAsync(text.RawMessageId!.Value, CancellationToken.None));
        Assert.Equal(1, await processor.ProcessAsync(structured.RawMessageId!.Value, CancellationToken.None));

        var targets = await db.Targets.AsNoTracking().Include(t => t.EventKind).ToListAsync();
        var observed = targets.Single(t => t.RawMessageId == text.RawMessageId);
        Assert.Equal(EventType.TargetObserved, observed.EventType);
        Assert.Equal("target.observed", observed.EventKind!.Code);
        Assert.Equal(1, observed.ParserMetadata!.RootElement.GetProperty("eventKindPolicyVersion").GetInt32());
        var started = targets.Single(t => t.RawMessageId == structured.RawMessageId);
        Assert.Equal(EventType.AirRaidAlert, started.EventType);
        Assert.Equal("alert.air_raid.started", started.EventKind!.Code);
        // The SQL functions still see the legacy enum: the insert trigger counted the observed target in the daily stats.
        Assert.Equal(1, await db.SourceDailyStats.CountAsync(s => s.SourceId == telegram));
        await Truncate(db);
    }

    [Fact]
    public async Task Production_shaped_backfill_of_50k_targets_uses_the_kind_index()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await Truncate(db);
        var (sourceId, rawId) = await RawFor(db, "p07-bulk");
        const int rows = 50_000;
        // Synthetic history: 50k targets over 7 days with the legacy mix. Row triggers (kinematic linking, daily stats)
        // are disabled for the synthetic INSERT only — they are not what the backfill is measured on.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE targets DISABLE TRIGGER USER");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO targets (raw_message_id, source_id, segment_index, observed_at, event_type, alert_level, model_confidence, classification_confidence, identification_method, object_count_is_approximate, location_kind, direction_kind, direction_confidence, confidence, parser_version) " +
                "SELECT {0}, {1}, g, {2}::timestamptz - (g || ' seconds')::interval * 12, (ARRAY[1,1,1,1,10,11,20,21,12,0])[1 + g % 10], 0, 0, 0, 1, false, 0, 0, 0, 2, 'p07' FROM generate_series(1, {3}) g",
                rawId, sourceId, At, rows);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE targets ENABLE TRIGGER USER");
        }
        var report = await EventKindBackfill.RunAsync(db, EventKindBackfill.DefaultBatchSize, CancellationToken.None);
        Assert.Equal(rows, report.Updated);
        Assert.Equal(0, report.NullAfter);
        Assert.Equal(10, report.Batches);
        Assert.Equal(1.0, report.Coverage);
        // Fresh statistics after the UPDATE, so the plans below reflect the real distribution, not the pre-backfill one.
        await db.Database.ExecuteSqlRawAsync("ANALYZE targets");

        var kindId = await db.EventKinds.Where(k => k.Code == "target.observed").Select(k => k.EventKindId).SingleAsync();
        var mapQuery = $"SELECT target_id FROM targets WHERE event_kind_id = {kindId} ORDER BY observed_at DESC LIMIT 100";
        var mapPlan = await Explain(db, mapQuery);
        Assert.Contains("ix_targets_event_kind_id_observed_at", mapPlan);
        var backfillScan = "SELECT count(*) FROM targets WHERE event_kind_id IS NULL";
        var scanPlan = await Explain(db, backfillScan);

        await WriteEvidence("P07-backfill-report.json", new
        {
            environment = new { at = DateTimeOffset.UtcNow, postgres = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync() },
            syntheticRows = rows,
            batchSize = EventKindBackfill.DefaultBatchSize,
            report,
            explain = new { mapQuery, mapPlan, backfillScan, scanPlan },
            note = "Synthetic 50k targets (triggers disabled for the INSERT only). Timing is a single Testcontainers run, not a production measurement.",
        });
        await Truncate(db);
    }

    [Fact]
    public async Task Migration_down_removes_catalog_and_up_restores_it()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await Truncate(db);
        var migrator = db.GetService<IMigrator>();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var index = applied.FindIndex(m => m.EndsWith("_AddEventKinds", StringComparison.Ordinal));
        Assert.True(index > 0, "AddEventKinds must be applied");
        await migrator.MigrateAsync(applied[index - 1]);
        Assert.False(await TableExists(db, "event_kinds"));
        Assert.False(await ColumnExists(db, "targets", "event_kind_id"));

        await migrator.MigrateAsync();
        Assert.True(await TableExists(db, "event_kinds"));
        Assert.True(await ColumnExists(db, "targets", "event_kind_id"));
        Assert.Equal(0, await db.EventKinds.CountAsync()); // Down dropped the rows: seed must run again after Up
        foreach (var seeder in Services.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(db, CancellationToken.None);
        }
        Assert.True(await db.EventKinds.CountAsync() > 0);
        await Services.GetRequiredService<IndexProvider>().RefreshAsync(CancellationToken.None);
    }

    private EventKindSeeder SeederFor(EventKindSeeder.EventKindsFile file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "puluj-p07-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "taxonomy"));
        File.WriteAllText(Path.Combine(dir, EventKindSeeder.FileName), JsonSerializer.Serialize(file, SeedFiles.Json));
        var options = Options.Create(new SeedOptions { DataDirectory = dir });
        return new EventKindSeeder(new SeedFiles(options, Services.GetRequiredService<IHostEnvironment>()), options, NullLogger<EventKindSeeder>.Instance);
    }

    private static async Task<string> Snapshot(PulujDbContext db) =>
        string.Join("\n", await db.Database.SqlQueryRaw<string>(
            "SELECT code || '|' || name_uk || '|' || category || '|' || coalesce(map_color, '') || '|' || enabled || '|' || policy_version AS \"Value\" FROM event_kinds ORDER BY code").ToListAsync());

    private static async Task<(int SourceId, long RawMessageId)> RawFor(PulujDbContext db, string key)
    {
        var sourceId = await db.Sources.Where(s => s.Code == "tg_kpszsu").Select(s => s.SourceId).SingleAsync();
        var raw = new RawMessage { SourceId = sourceId, SourceMessageId = key, PublishedAt = At, ReceivedAt = At, RawText = key, Hash = key, ProcessingStatus = ProcessingStatus.Processed };
        db.RawMessages.Add(raw);
        await db.SaveChangesAsync();
        return (sourceId, raw.RawMessageId);
    }

    private static Task Truncate(PulujDbContext db) =>
        db.Database.ExecuteSqlRawAsync("TRUNCATE raw_messages, targets, target_tracks, air_alerts, source_daily_stats, source_copies, processing_errors RESTART IDENTITY CASCADE");

    private static async Task<string> Explain(PulujDbContext db, string sql)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }
        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Task<bool> TableExists(PulujDbContext db, string table) =>
        db.Database.SqlQueryRaw<bool>("SELECT to_regclass({0}) IS NOT NULL AS \"Value\"", table).SingleAsync();

    private static Task<bool> ColumnExists(PulujDbContext db, string table, string column) =>
        db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = {0} AND column_name = {1}) AS \"Value\"", table, column).SingleAsync();

    private static async Task WriteEvidence(string name, object payload)
    {
        var directory = Environment.GetEnvironmentVariable("PULUJ_EVIDENCE_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
