using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puluj.Analytics.Analysis;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Reporting;

namespace Puluj.Analytics;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Puluj";

    /// <summary>Everything the analytics service runs: the independent index and runner (owner connection: it migrates its own schema).</summary>
    public static IServiceCollection AddPulujAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPulujAnalyticsReporting(configuration);
        services.AddSingleton<AnalyticsMetrics>();
        services.AddSingleton<AnalysisRunner>();
        return services;
    }

    /// <summary>Read side only — what the admin panel needs to show the page.</summary>
    public static IServiceCollection AddPulujAnalyticsReporting(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
        services.Configure<AnalyticsOptions>(configuration.GetSection(AnalyticsOptions.Section));
        services.AddDbContextFactory<AnalyticsDbContext>(o => AnalyticsDbContext.Configure(o, connectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AnalyticsReportService>();
        services.AddSingleton<Lifecycle.LifecycleReportService>();
        services.AddSingleton<Lifecycle.LifecycleBackfill>(); // the worker's loop drives it; the admin panel can too (audited)
        services.AddSingleton<Lifecycle.LifecycleReconciliation>();
        return services;
    }
}
