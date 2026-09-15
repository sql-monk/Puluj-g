using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Collectors.AlertsInUa;
using Puluj.Collectors.Telegram;

namespace Puluj.Collectors;

/// <summary>Names the host uses to pick collectors for a process (Worker:Roles).</summary>
public static class CollectorNames
{
    public const string Telegram = "telegram";
    public const string AlertsInUa = "alerts";
}

public static class DependencyInjection
{
    /// <param name="names">Collectors to run in this process (CollectorNames); null = all of them.</param>
    public static IServiceCollection AddPulujCollectors(this IServiceCollection services, IConfiguration configuration, IReadOnlyCollection<string>? names = null)
    {
        services.Configure<AlertsInUaOptions>(configuration.GetSection(AlertsInUaOptions.Section));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.Section));

        // Retry + circuit breaker + timeout for every HTTP source (spec §29).
        services.AddHttpClient(AlertsInUaCollector.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .AddStandardResilienceHandler();

        services.AddSingleton<CollectorStateStore>();
        services.AddSingleton<CollectorIngress>();
        if (names is null || names.Contains(CollectorNames.AlertsInUa))
        {
            services.AddSingleton<ICollector, AlertsInUaCollector>();
            services.AddSingleton<ICollector, AlertsInUaHistoryCollector>(); // idle unless Collectors:AlertsInUa:BackfillPeriod is set
        }
        if (names is null || names.Contains(CollectorNames.Telegram))
        {
            services.AddSingleton<ICollector, TelegramCollector>();
        }
        services.AddHostedService<CollectorSupervisor>();
        return services;
    }
}
