using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Notifications;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;
using Puluj.Infrastructure.Settings;

namespace Puluj.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Puluj";

    public static IServiceCollection AddPulujInfrastructure(this IServiceCollection services, IConfiguration configuration, string? instanceName = null)
    {
        instanceName ??= Environment.MachineName.ToLowerInvariant();
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContextFactory<PulujDbContext>(o => ConfigureDbContext(o, connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContext());
        services.AddSingleton<INotifyPublisher, PgNotifyPublisher>();
        services.AddSingleton<PulujMetrics>();
        services.AddSingleton<IRawMessageSignalBuffer, RawMessageSignalBuffer>();
        services.AddSingleton<RawMessageIngestor>();
        services.AddSingleton<ReprocessService>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.AddSingleton<SeedFiles>();
        services.AddSingleton<ISeeder, TaxonomySeeder>();
        services.AddSingleton<ISeeder, SourceSeeder>();
        services.AddSingleton<ISeeder, GazetteerSeeder>();
        services.AddSingleton<ISeeder, EventKindSeeder>();
        services.AddSingleton<Rules.RulesetService>();
        services.AddSingleton<ISeeder, EventKindRuleSeeder>();
        services.AddSingleton<ISeeder, EventKindBackfill>();
        services.AddSingleton<PgNotifyListener>();

        return services;
    }

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString)
    {
        options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNetTopologySuite();
                npgsql.MigrationsAssembly(typeof(PulujDbContext).Assembly.FullName);
            })
            .UseSnakeCaseNamingConvention();
    }
}
