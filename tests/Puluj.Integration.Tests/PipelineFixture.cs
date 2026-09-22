using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing;
using Puluj.Processing.Indexes;
using Testcontainers.PostgreSql;
using Npgsql;

namespace Puluj.Integration.Tests;

/// <summary>
/// One real PostGIS database for every integration test class (xunit collection fixture: the classes run one after
/// another). Uses PULUJ_TEST_CONNECTION when set (e.g. a local PostGIS), otherwise starts a postgis container via
/// Docker. Infrastructure failures fail the suite: an unexecuted database test must never appear passed.
/// External databases must end in _test and require PULUJ_TEST_ALLOW_RESET=1. Migrated, seeded, indexes loaded.
/// </summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public ServiceProvider? Services { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("PULUJ_TEST_CONNECTION");
        if (!string.IsNullOrEmpty(connectionString))
        {
            var target = new NpgsqlConnectionStringBuilder(connectionString);
            if (target.Database?.EndsWith("_test", StringComparison.Ordinal) != true
                || Environment.GetEnvironmentVariable("PULUJ_TEST_ALLOW_RESET") != "1")
            {
                throw new InvalidOperationException("External integration DB must end in _test and explicitly allow destructive reset with PULUJ_TEST_ALLOW_RESET=1. Use an expendable database, or unset PULUJ_TEST_CONNECTION for Testcontainers.");
            }
        }
        if (string.IsNullOrEmpty(connectionString))
        {
            try
            {
                _container = new PostgreSqlBuilder("postgis/postgis:17-3.5").WithDatabase("puluj_test").Build();
                await _container.StartAsync();
                connectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                if (_container is not null) await _container.DisposeAsync();
                _container = null;
                throw new InvalidOperationException("Real PostGIS is required. Start Docker or configure an expendable _test database; no tests were executed.", ex);
            }
        }

        var repoRoot = FindRepoRoot();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Puluj"] = connectionString,
            ["Seed:DataDirectory"] = Path.Combine(repoRoot, "data"),
            ["Seed:SeedGazetteer"] = "true",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(repoRoot));
        services.AddPulujInfrastructure(config);
        services.AddPulujProcessing(config, "test");
        Services = services.BuildServiceProvider();

        await using var db = await Services.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis");
        await db.Database.MigrateAsync();
        await ResetDataAsync(db);
        foreach (var seeder in Services.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(db, CancellationToken.None);
        }
        await EnsureDeterministicSourcesAsync(db);
        await EnsureDeterministicGazetteerAsync(db);
        await Services.GetRequiredService<IndexProvider>().RefreshAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reset only generated pipeline state while preserving seeded sources, taxonomy and gazetteer. Integration test
    /// classes share this fixture, so a test that asserts exact aggregate counts must opt in to a fresh state instead
    /// of depending on xUnit's class execution order.
    /// </summary>
    public async Task ResetDataAsync()
    {
        await using var db = await Services!.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContextAsync();
        await ResetDataAsync(db);
        await EnsureDeterministicSourcesAsync(db);
    }

    private static async Task ResetDataAsync(PulujDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("TRUNCATE track_targets, target_track_revisions, target_tracks, targets, air_alerts, processing_errors, raw_messages, llm_requests RESTART IDENTITY CASCADE");
    }

    /// <summary>
    /// Operator-managed source configuration is intentionally mutable and may differ between checkouts. Integration
    /// tests must not disappear when a local <c>data/sources.json</c> omits a fixture source, so keep the source
    /// identities used by the pipeline scenarios inside the disposable test database.
    /// </summary>
    private static async Task EnsureDeterministicSourcesAsync(PulujDbContext db)
    {
        if (!await db.Sources.AnyAsync(source => source.Code == "alerts_in_ua"))
        {
            db.Sources.Add(new Source
            {
                Code = "alerts_in_ua",
                Name = "alerts.in.ua",
                Type = SourceType.RestApi,
                Url = "https://api.alerts.in.ua/v1/alerts/active.json",
                TrustLevel = 0.95,
                Priority = 100,
                Enabled = true,
                PollingInterval = TimeSpan.FromSeconds(30),
                Config = JsonDocument.Parse("""{"collector":"alerts_in_ua"}"""),
            });
        }

        await EnsureTelegramFixtureAsync(db, new Source
        {
            Code = "tg_kpszsu",
            Name = "Повітряні сили ЗС України",
            Type = SourceType.Telegram,
            Url = "https://t.me/kpszsu",
            TrustLevel = 0.95,
            Priority = 90,
            Enabled = true,
            Config = JsonDocument.Parse("""{"channel":"kpszsu","language":"uk","official":true}"""),
        });

        await EnsureTelegramFixtureAsync(db, new Source
        {
            Code = "tg_monitoringwar",
            Name = "monitorwar",
            Type = SourceType.Telegram,
            Url = "https://t.me/monitoringwar",
            TrustLevel = 0.85,
            Priority = 85,
            Enabled = true,
            Config = JsonDocument.Parse("""{"channel":"monitoringwar","language":"uk","official":false}"""),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a fixture Telegram source unless its code exists. data/sources.json may have seeded the same channel under
    /// its plain code; one channel is one source (ux_sources_telegram_channel), so that row gives way to the fixture's —
    /// raw_messages were truncated just before, and nothing in the tests refers to the plain code.
    /// </summary>
    private static async Task EnsureTelegramFixtureAsync(PulujDbContext db, Source wanted)
    {
        if (await db.Sources.AnyAsync(source => source.Code == wanted.Code))
        {
            return;
        }
        var telegram = await db.Sources.Where(s => s.Type == SourceType.Telegram).ToListAsync();
        if (SourceCodes.FindTelegramChannel(telegram, SourceCodes.TelegramChannel(wanted)) is { } other)
        {
            db.Sources.Remove(other);
            await db.SaveChangesAsync(); // before the insert: the unique index sees one channel at a time
        }
        db.Sources.Add(wanted);
    }

    /// <summary>
    /// The full administrative boundary files are deliberately downloaded rather than committed.  A clean clone
    /// therefore still needs a small, real PostGIS geometry set for integration tests; without it the apparently
    /// provider-backed suite silently loses every region scenario.  Keep this fallback test-only and only use it when
    /// the optional boundary import produced no regions.
    /// </summary>
    private static async Task EnsureDeterministicGazetteerAsync(PulujDbContext db)
    {
        if (await db.Places.AnyAsync(p => p.Level == PlaceLevel.Region))
        {
            return;
        }

        Place Region(string key, string name, string[] variants, double lon, double lat, double halfWidth = 0.45, double halfHeight = 0.35) => new()
        {
            Name = name,
            NameVariants = variants,
            Level = PlaceLevel.Region,
            CountryCode = "UA",
            ExternalKey = $"test:{key}",
            Geometry = Geo.Factory.CreatePolygon([
                new(lon - halfWidth, lat - halfHeight), new(lon + halfWidth, lat - halfHeight),
                new(lon + halfWidth, lat + halfHeight), new(lon - halfWidth, lat + halfHeight), new(lon - halfWidth, lat - halfHeight),
            ]),
            Centroid = Geo.Point(lon, lat),
            // Coarse region anchors must not create a path from centre-to-centre.  The fallback boundaries are
            // intentionally small rectangles, so retain an oblast-scale accuracy radius independently of them.
            RadiusKm = 180,
        };

        var kyiv = Region("UA-32", "Київська область", ["київськ обл", "київщин", "київськ"], 30.5, 50.45);
        var poltava = Region("UA-53", "Полтавська область", ["полтавськ обл", "полтавщин", "полтавськ"], 34.55, 49.6);
        // These two oblasts intentionally overlap in the miniature geometry.  The pipeline regression verifies
        // that an ambiguous coarse region pair does not fabricate a route line between their centroids.
        var sumy = Region("UA-59", "Сумська область", ["сумськ обл", "сумщин", "сумськ"], 34.8, 51.0, 1.0, 1.3);
        var kirovohrad = Region("UA-35", "Кіровоградська область", ["кіровоградськ обл", "кіровоградщин", "кіровоградськ"], 32.25, 48.5);
        var kharkiv = Region("UA-63", "Харківська область", ["харківськ обл", "харківщин", "харківськ"], 36.45, 49.95);
        db.Places.AddRange(kyiv, poltava, sumy, kirovohrad, kharkiv);
        await db.SaveChangesAsync();

        var boryspil = new Place
        {
            Name = "Броварський район",
            NameVariants = ["броварськ район", "броварськ"],
            Level = PlaceLevel.District,
            ParentId = kyiv.PlaceId,
            CountryCode = "UA",
            ExternalKey = "test:UA-32-brovary",
            Geometry = Geo.Factory.CreatePolygon([
                new(30.5, 50.35), new(30.9, 50.35), new(30.9, 50.65), new(30.5, 50.65), new(30.5, 50.35),
            ]),
            Centroid = Geo.Point(30.7, 50.5),
            RadiusKm = 25,
        };
        db.Places.Add(boryspil);
        await db.SaveChangesAsync();
        db.Places.Add(new Place
        {
            Name = "Тестова громада",
            NameVariants = ["тестов гром"],
            Level = PlaceLevel.Hromada,
            ParentId = boryspil.PlaceId,
            CountryCode = "UA",
            ExternalKey = "test:UA-32-brovary-hromada",
            Geometry = Geo.Factory.CreatePolygon([
                new(30.6, 50.4), new(30.8, 50.4), new(30.8, 50.55), new(30.6, 50.55), new(30.6, 50.4),
            ]),
            Centroid = Geo.Point(30.7, 50.475),
            RadiusKm = 15,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Puluj.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Puluj.Integration.Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture>
{
    public const string Name = "pipeline";
}
