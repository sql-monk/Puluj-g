using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Reporting;
using Puluj.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Puluj.Analytics.Tests;

/// <summary>
/// The runner over a real database: the pipeline's schema (for raw_messages / sources) plus the analytics schema.
/// Uses PULUJ_TEST_CONNECTION when set, otherwise a postgis container via Docker; without either the tests are no-ops.
/// </summary>
public sealed class AnalysisRunnerTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private ServiceProvider? _services;
    private int _a, _b;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("PULUJ_TEST_CONNECTION");
        if (string.IsNullOrEmpty(cs))
        {
            try
            {
                _container = new PostgreSqlBuilder("postgis/postgis:17-3.5").WithDatabase("puluj_analytics_test").Build();
                await _container.StartAsync();
                cs = _container.GetConnectionString();
            }
            catch (Exception)
            {
                _container = null;
                return;
            }
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Puluj"] = cs,
            ["Analytics:SafetyLag"] = "00:00:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<PulujDbContext>(o => Puluj.Infrastructure.DependencyInjection.ConfigureDbContext(o, cs));
        services.AddPulujAnalytics(config);
        _services = services.BuildServiceProvider();

        await using (var db = await _services.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis");
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM targets WHERE source_id IN (SELECT source_id FROM sources WHERE code LIKE 'test_analytics_%')");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM raw_messages WHERE source_id IN (SELECT source_id FROM sources WHERE code LIKE 'test_analytics_%')");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sources WHERE code LIKE 'test_analytics_%'");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO sources (code, name, type, trust_level, priority, enabled) VALUES
                    ('test_analytics_a', 'Test A', 2, 0.9, 10, true), ('test_analytics_b', 'Test B', 2, 0.5, 5, true)
                """);
            var ids = await db.Sources.Where(s => s.Code.StartsWith("test_analytics_")).OrderBy(s => s.Code).Select(s => s.SourceId).ToListAsync();
            (_a, _b) = (ids[0], ids[1]);
        }
        await using (var adb = await Factory.CreateDbContextAsync())
        {
            await adb.Database.MigrateAsync();
            await Reports.ResetAsync(CancellationToken.None);
            await adb.Database.ExecuteSqlRawAsync("DELETE FROM analytics.runs");
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private IDbContextFactory<AnalyticsDbContext> Factory => _services!.GetRequiredService<IDbContextFactory<AnalyticsDbContext>>();
    private AnalysisRunner Runner => _services!.GetRequiredService<AnalysisRunner>();
    private AnalyticsReportService Reports => _services!.GetRequiredService<AnalyticsReportService>();

    private const string X = "Групи ударних БпЛА на Сумщині в районі Боромля, Лебедин та Недригайлів західним курсом на Полтавщину та Черкащину.";
    private const string Y = "Пуски керованих авіаційних бомб ворожою тактичною авіацією на південь Харківщини, будьте обережні в укриттях.";

    private async Task<long> InsertAsync(int source, string key, DateTimeOffset publishedAt, string text, long? channelId = null, string? forwardedFrom = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var payload = channelId is null && forwardedFrom is null ? "{}" : $$"""{"channelId": {{(channelId?.ToString() ?? "null")}}, "forwardedFrom": {{(forwardedFrom is null ? "null" : $"\"{forwardedFrom}\"")}}}""";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source}|{key}|{text}")));
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO raw_messages (source_id, source_message_id, published_at, received_at, raw_text, raw_payload, hash, processing_status, attempts)
            VALUES ({source}, {key}, {publishedAt}, {DateTimeOffset.UtcNow.AddHours(-1)}, {text}, {payload}::jsonb, {hash}, 1, 1)
            """);
        return (await db.Database.SqlQuery<long>($"SELECT raw_message_id AS \"Value\" FROM raw_messages WHERE source_id = {source} AND source_message_id = {key}").ToListAsync()).Single();
    }

    private async Task<List<MessageCopy>> CopiesAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Copies.AsNoTracking().OrderBy(c => c.CopyPostKey).ThenBy(c => c.OriginalPostKey).ToListAsync();
    }

    private async Task<int> InsertPlaceAsync()
    {
        await using var db = await _services!.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync();
        var point = Geo.Point(34.8, 50.9);
        var place = new Place
        {
            Name = "Analytics test place",
            ExternalKey = "test:analytics-place:" + Guid.NewGuid().ToString("N"),
            Level = PlaceLevel.City,
            Geometry = point,
            Centroid = point,
            RadiusKm = 1,
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return place.PlaceId;
    }

    private async Task InsertFactAsync(long rawMessageId, int sourceId, DateTimeOffset observedAt, int placeId)
    {
        await using var db = await _services!.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync();
        db.Targets.Add(new Target
        {
            RawMessageId = rawMessageId,
            SourceId = sourceId,
            ObservedAt = observedAt,
            EventType = EventType.TargetObserved,
            TargetCategoryId = null, // target type is intentionally unknown in this integration fixture
            ObjectCount = 3,
            LocationPlaceId = placeId,
            Location = Geo.Point(34.8, 50.9),
            LocationAccuracyKm = 1,
            LocationKind = LocationKind.City,
            DirectionDeg = 270,
            DirectionKind = DirectionKind.Compass,
            DirectionConfidence = ConfidenceLevel.High,
            Confidence = ConfidenceLevel.High,
            ParserVersion = "test",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Does_not_pair_identical_unparsed_posts()
    {
        if (_services is null)
        {
            return; // no database available
        }
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        await InsertAsync(_a, "1", t0, X, channelId: 111);
        await InsertAsync(_b, "2", t0.AddMinutes(5), "🛵 " + X.ToUpperInvariant(), channelId: 222);

        var first = await Runner.RunOnceAsync(CancellationToken.None);
        Assert.True(first.Locked);
        Assert.Equal(2, first.Scanned);
        Assert.Equal(2, first.Fingerprinted);
        Assert.Empty(await CopiesAsync());

        // Nothing new: the second run scans nothing and changes nothing.
        var second = await Runner.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second.Scanned);
        Assert.Empty(await CopiesAsync());
    }

    [Fact]
    public async Task Pairs_differently_worded_posts_when_their_parsed_event_matches()
    {
        if (_services is null)
        {
            return;
        }
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var first = await InsertAsync(_a, "semantic-1", t0, "Три шахеди над Сумами курсом на Полтаву.", channelId: 111);
        var second = await InsertAsync(_b, "semantic-2", t0.AddMinutes(5), "Група БпЛА з Сум рухається на захід.", channelId: 222);
        var place = await InsertPlaceAsync();
        await InsertFactAsync(first, _a, t0, place);
        await InsertFactAsync(second, _b, t0.AddMinutes(5), place);

        await Runner.RunOnceAsync(CancellationToken.None);

        var pair = Assert.Single(await CopiesAsync());
        Assert.Equal((_a, _b), (pair.OriginalSourceId, pair.CopySourceId));
        Assert.Equal(CopyKind.Near, pair.Kind);
        Assert.True(pair.Jaccard < 0.7); // different wording was not used to decide the pair
    }
}
