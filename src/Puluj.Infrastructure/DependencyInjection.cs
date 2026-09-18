using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Messaging.Topology;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Seeding;
using Puluj.Infrastructure.Settings;

namespace Puluj.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Puluj";

    /// <param name="instanceName">Name of the running process (WorkerOptions.InstanceName): `producer` suffix of the bridge
    /// envelopes and `created_by` of processing runs. Defaults to the machine name.</param>
    public static IServiceCollection AddPulujInfrastructure(this IServiceCollection services, IConfiguration configuration, string? instanceName = null)
    {
        instanceName ??= Environment.MachineName.ToLowerInvariant();
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContextFactory<PulujDbContext>(o => ConfigureDbContext(o, connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PulujDbContext>>().CreateDbContext());
        services.AddSingleton<INotifyPublisher, PgNotifyPublisher>();
        services.AddSingleton<PulujMetrics>();
        services.AddSingleton<IRawMessageQueue, RawMessageQueue>();
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

        // Message platform (P03): registry + outbox writer are always available; whether the ingestor writes the outbox
        // and whether this process talks to the broker is configuration (Messaging:Outbox:Enabled, Messaging:Enabled).
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.Section));
        services.AddSingleton(TopologyRegistry.LoadEmbedded());
        services.AddSingleton<TopologyRegistrar>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            return new ProcessingRuns(options.Outbox.PipelineVersion ?? PipelineVersion(), instanceName);
        });
        services.AddSingleton<OutboxWriter>();
        services.AddSingleton(sp =>
        {
            var writer = ActivatorUtilities.CreateInstance<IngressWriter>(sp);
            writer.Instance = instanceName;
            return writer;
        });
        services.AddSingleton<SubscriptionAdmin>();
        // P13 (ADR-0012): ops snapshot, alarms and the message explorer — database-only, usable from any process.
        services.Configure<Messaging.Ops.OpsOptions>(configuration.GetSection(Messaging.Ops.OpsOptions.Section));
        services.AddSingleton<Messaging.Ops.BrokerManagementClient>();
        services.AddSingleton<Messaging.Ops.OpsSnapshotService>();
        services.AddSingleton<Messaging.Ops.MessageExplorer>();
        // P14 (ADR-0005): run/generation orchestration — usable from Admin (commands) and the messaging worker (publisher).
        services.Configure<Processing.ReplayOptions>(configuration.GetSection(Processing.ReplayOptions.Section));
        services.AddSingleton<Processing.RunService>();
        return services;
    }

    /// <summary>Build identity written into every envelope (`pipeline_version`): the informational version of this assembly.</summary>
    public static string PipelineVersion() =>
        typeof(DependencyInjection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DependencyInjection).Assembly.GetName().Version?.ToString() ?? "0.0.0";

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
