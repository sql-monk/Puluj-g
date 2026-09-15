using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE track_targets, target_track_revisions, target_tracks, targets, air_alerts, processing_errors, raw_messages RESTART IDENTITY CASCADE");
        foreach (var seeder in Services.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(db, CancellationToken.None);
        }
        await Services.GetRequiredService<IndexProvider>().RefreshAsync(CancellationToken.None);
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
