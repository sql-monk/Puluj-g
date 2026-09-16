using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Api;
using Puluj.Api.Services;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

/// <summary>
/// P11 (ADR-0011) query/payload budget and history semantics on real PostGIS: 10 000 incidents are paged through the keyset
/// without gaps or duplicates, the window scan uses the read index (no sequential scan), a page of 500 stays inside the
/// payload budget, the snapshot cap truncates and says so, and the as-of views come from the revision snapshots.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class IncidentReadSideTests(PipelineFixture fixture)
{
    private ServiceProvider Services => fixture.Services!;

    private static readonly Guid Live = new("0f7d3c2a-5b61-4e0c-9a8e-7c1d2b3e4f50"); // any generation id; marked active below
    private static readonly Guid Shadow = Guid.NewGuid();

    private async Task<(IDbContextFactory<PulujDbContext> Factory, IncidentQueries Queries, MapOptions Options)> BuildAsync(MapOptions? options = null)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
        var refs = new ReferenceCache(factory, NullLogger<ReferenceCache>.Instance);
        await refs.RefreshAsync(CancellationToken.None);
        var map = options ?? new MapOptions();
        return (factory, new IncidentQueries(factory, refs, TimeProvider.System, Options.Create(map)), map);
    }

    private static async Task ResetAsync(IDbContextFactory<PulujDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE incident_revisions, incident_observations, incidents RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM processing.generations");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES ({Live}, true, now()), ({Shadow}, false, now())");
    }

    /// <summary>Bulk rows straight into the table (the writer's shape): 3 kinds, 5 regions, spread over 48 hours, every 7th one from the shadow generation.</summary>
    private static async Task<int[]> SeedAsync(IDbContextFactory<PulujDbContext> factory, int count, DateTimeOffset now)
    {
        await using var db = await factory.CreateDbContextAsync();
        var kinds = await db.EventKinds.AsNoTracking().Where(k => k.Category == Domain.Enums.EventKindCategory.Incident).OrderBy(k => k.EventKindId).Select(k => k.EventKindId).Take(3).ToArrayAsync();
        var regionRows = await db.Places.AsNoTracking().Where(p => p.Level == Domain.Enums.PlaceLevel.Region).OrderBy(p => p.PlaceId).Select(p => new { p.PlaceId, p.Centroid }).Take(5).ToListAsync();
        var regions = regionRows.Select(r => r.PlaceId).ToArray();
        Assert.Equal(3, kinds.Length);
        Assert.Equal(5, regions.Length);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY incidents (generation_id, event_kind_id, state, suppressed, first_reported_at, last_reported_at, event_at, location_kind, location_place_id, geometry, accuracy_km, confidence, source_count, revision, created_at, updated_at) FROM STDIN (FORMAT BINARY)");
        for (var i = 0; i < count; i++)
        {
            var at = now.AddSeconds(-(double)i * 48 * 3600 / count);
            await writer.StartRowAsync();
            await writer.WriteAsync(i % 7 == 6 ? Shadow : Live, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(kinds[i % 3], NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(i % 11 == 10 ? "resolved" : "reported", NpgsqlTypes.NpgsqlDbType.Text);
            await writer.WriteAsync(i % 13 == 12, NpgsqlTypes.NpgsqlDbType.Boolean);
            await writer.WriteAsync(at.AddMinutes(-5), NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(at, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(at.AddMinutes(-5), NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync((int)Domain.Enums.LocationKind.Region, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(regions[i % 5], NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(regionRows[i % 5].Centroid, NpgsqlTypes.NpgsqlDbType.Geography);
            await writer.WriteAsync(120.0, NpgsqlTypes.NpgsqlDbType.Double);
            await writer.WriteAsync((int)Domain.Enums.ConfidenceLevel.Medium, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(1, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(1, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(at, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(at, NpgsqlTypes.NpgsqlDbType.TimestampTz);
        }
        await writer.CompleteAsync();
        return kinds;
    }

    [Fact]
    public async Task R03_Ten_thousand_incidents_page_through_the_keyset_inside_the_budgets()
    {
        var (factory, queries, map) = await BuildAsync(new MapOptions { IncidentHours = 48, IncidentSnapshotLimit = 1000 });
        await ResetAsync(factory);
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory, 10_000, now);
        var from = now.AddHours(-48);

        // Every visible live incident exactly once across the pages; no page above the limit; the cursor is opaque and stable.
        var seen = new HashSet<long>();
        string? cursor = null;
        var pages = 0;
        var largestPage = 0;
        do
        {
            var page = await queries.ListAsync(new IncidentQueries.Query(from, now, null, null, null, cursor, 500, null, null, false), CancellationToken.None);
            Assert.True(page.Items.Count <= 500);
            foreach (var item in page.Items)
            {
                Assert.True(seen.Add(item.Id), $"incident {item.Id} appeared twice");
                Assert.False(item.Suppressed);
                Assert.NotEqual("retracted", item.State);
                Assert.Equal("region", item.Location!.Precision); // a region centroid is an area, never a point
                Assert.NotNull(item.Location.Point);
                Assert.Equal(4326, item.Location.Point.SRID);
            }
            largestPage = Math.Max(largestPage, JsonSerializer.SerializeToUtf8Bytes(page, Json()).Length);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 100);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var expected = await db.Incidents.CountAsync(i => i.GenerationId == Live && !i.Suppressed && i.State != "retracted" && i.LastReportedAt >= from && i.LastReportedAt <= now);
            Assert.Equal(expected, seen.Count); // the shadow generation never reaches the read side
            Assert.Equal(10_000 - 10_000 / 7 - (10_000 - 10_000 / 7) / 13 - 1, expected); // 6/7 live, 12/13 visible (the seed's modulo pattern)
        }
        Assert.True(largestPage <= 500 * 1024, $"a page of 500 serialises to {largestPage} bytes");

        // The window scan is served by the read index (query budget): no sequential scan over incidents.
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("ANALYZE incidents");
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            // The SQL EF generates for the list page (same window and states as the paging loop above), explained as-is.
            var states = new HashSet<string> { "reported", "confirmed", "resolved" };
            var sql = queries.ListSql(db, from, now, states, 500);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p11-r03-query.sql"), sql);
            // ToQueryString() prints the parameters as a comment header and leaves the placeholders in the text: bind them by name.
            var body = string.Join(Environment.NewLine, sql.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.StartsWith("--", StringComparison.Ordinal)));
            await using var cmd = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + body, conn);
            // Header lines: `-- @name='value' (DbType = X)` — EF expands the state set into @states1..3 (IN list); dates parse back to timestamptz.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, @"^-- @(\w+)='([^']*)'(?: \(DbType = (\w+)\))?", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var (name, text, type) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                object value = type is "DateTime" or "DateTimeOffset" ? DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal)
                    : int.TryParse(text, out var n) && type is "" or "Int32" ? n
                    : text;
                cmd.Parameters.AddWithValue(name, value);
            }
            var plan = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.DoesNotContain("\"Seq Scan\"", plan);
            Assert.Contains("\"Index Name\": \"ix_incidents_read_keyset\"", plan); // the window is walked through the read index in keyset order
            Assert.DoesNotContain("\"Node Type\": \"Sort\"", plan);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p11-r03-plan.json"), plan);
        }

        // Snapshot cap: the newest 1000 and the truncation flag.
        var (live, truncated) = await queries.LiveAsync(now, CancellationToken.None);
        Assert.Equal(map.IncidentSnapshotLimit, live.Count);
        Assert.True(truncated);
        Assert.True(live.Zip(live.Skip(1)).All(p => p.First.LastReportedAt >= p.Second.LastReportedAt), "newest first");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(live, Json()).Length <= 1024 * 1024, "snapshot incidents within 1 MB");

        // Window budget: more than the maximum span is refused, not scanned.
        var ex = await Assert.ThrowsAsync<IncidentQueries.QueryException>(() => queries.ListAsync(new IncidentQueries.Query(now.AddDays(-30), now, null, null, null, null, 10, null, null, false), CancellationToken.None));
        Assert.Contains("7 days", ex.Message);
        await ResetAsync(factory);
    }

    [Fact]
    public async Task R04_As_of_views_come_from_the_revision_snapshots()
    {
        var (factory, queries, _) = await BuildAsync(new MapOptions { IncidentHours = 48 });
        await ResetAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var kinds = await SeedAsync(factory, 1, now);
        var kindCode = (await factory.CreateDbContextAsync()).EventKinds.Single(k => k.EventKindId == kinds[0]).Code;
        var t1 = now.AddMinutes(-30);
        var t2 = now.AddMinutes(-10);
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Two revisions, recorded 20 minutes apart: reported with one source, then confirmed with two.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO incident_revisions (incident_id, revision, change, effective_at, recorded_at, actor, reason, snapshot) VALUES
                (1, 1, 'created', {t1}, {t1}, 'incident-worker@test', NULL, {Snapshot(kindCode, "reported", 1, 1, t1)}::jsonb),
                (1, 2, 'updated', {t2}, {t2}, 'ops', 'confirmed by the utility', {Snapshot(kindCode, "confirmed", 2, 2, t2)}::jsonb)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE incidents SET state = 'confirmed', source_count = 2, revision = 2, created_at = {t1}, first_reported_at = {t1.AddMinutes(-5)}, event_at = {t1.AddMinutes(-5)}, last_reported_at = {t2} WHERE incident_id = 1");
        }

        var details = await queries.DetailsAsync(1, null, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Equal("confirmed", details.Incident.State);
        Assert.Equal([1, 2], details.Revisions.Select(r => r.Revision));
        Assert.Equal(["system", "operator"], details.Revisions.Select(r => r.Actor)); // redacted on the public API
        var asOf1 = await queries.DetailsAsync(1, 1, CancellationToken.None);
        Assert.Equal("reported", asOf1!.Incident.State);
        Assert.Equal(1, asOf1.Incident.SourceCount);
        Assert.Equal(1, asOf1.Incident.Revision);
        Assert.Null(await queries.DetailsAsync(1, 9, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE incidents SET suppressed = true WHERE incident_id = 1");
            Assert.Null(await queries.DetailsAsync(1, null, CancellationToken.None)); // moderated: not public by id either (review N1)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE incidents SET suppressed = false, generation_id = {Shadow} WHERE incident_id = 1");
            Assert.Null(await queries.DetailsAsync(1, null, CancellationToken.None)); // a shadow generation never reaches the public read side
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE incidents SET generation_id = {Live} WHERE incident_id = 1");
        }

        // recorded mode: between the two revisions the system knew "reported"; after both — "confirmed"; before the first — nothing.
        var between = await queries.ListAsync(new IncidentQueries.Query(now.AddHours(-2), null, null, null, null, null, 10, "recorded", t1.AddMinutes(5), false), CancellationToken.None);
        Assert.Equal("reported", Assert.Single(between.Items).State);
        var after = await queries.ListAsync(new IncidentQueries.Query(now.AddHours(-2), null, null, null, null, null, 10, "recorded", now, false), CancellationToken.None);
        Assert.Equal("confirmed", Assert.Single(after.Items).State);
        var before = await queries.ListAsync(new IncidentQueries.Query(now.AddHours(-2), null, null, null, null, null, 10, "recorded", t1.AddMinutes(-1), false), CancellationToken.None);
        Assert.Empty(before.Items);
        // effective mode is today's reconstruction.
        var effective = await queries.ListAsync(new IncidentQueries.Query(now.AddHours(-2), null, null, null, null, null, 10, null, null, false), CancellationToken.None);
        Assert.Equal("confirmed", Assert.Single(effective.Items).State);
        Assert.Equal(2, effective.Items[0].Revision);
        await ResetAsync(factory);
    }

    private static string Snapshot(string kind, string state, int sources, int revision, DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        incident_id = 1,
        event_kind_code = kind,
        state,
        suppressed = false,
        first_reported_at = at.AddMinutes(-5).ToString("O"),
        last_reported_at = at.ToString("O"),
        event_at = at.AddMinutes(-5).ToString("O"),
        location = new { kind = "region", place_id = 8, accuracy_km = 120.0 },
        confidence = "medium",
        source_count = sources,
        revision,
        observations = Enumerable.Range(1, sources).Select(s => new { observation_id = Guid.NewGuid().ToString(), relation = s == 1 ? "canonical" : "confirms", source_id = s, score = 1.0, effective_at = at.ToString("O") }).ToArray(),
    });

    private static JsonSerializerOptions Json()
    {
        var o = new JsonSerializerOptions();
        ApiDependencyInjection.ConfigureJson(o);
        return o;
    }
}
