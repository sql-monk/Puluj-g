using Microsoft.Extensions.DependencyInjection;

namespace Puluj.Messaging;

public static class DependencyInjection
{
    public const string RelayRole = "relay";
    public const string ArchiveRole = "archive";

    /// <summary>
    /// Broker runtime for the given roles (WorkerOptions): `relay` — topology declare, outbox relay, reconciliation and
    /// cleanup; `archive` — the archive subscription consumer plus the DLQ consumer of its queues. Infrastructure
    /// (`AddPulujInfrastructure`) must be registered first; `Messaging:Enabled` is checked by the caller.
    /// </summary>
    public static IServiceCollection AddPulujMessaging(this IServiceCollection services, IReadOnlySet<string> roles, string? instanceName = null)
    {
        services.AddSingleton<BrokerConnection>();
        services.AddSingleton<MessagingMetrics>();
        services.AddSingleton<TopologyDeclarer>();

        if (roles.Contains(RelayRole))
        {
            services.AddSingleton<OutboxRelay>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
            services.AddSingleton<ReconciliationService>();
            services.AddHostedService(sp => sp.GetRequiredService<ReconciliationService>());
        }

        var consumed = new List<string>();
        if (roles.Contains(ArchiveRole))
        {
            services.AddSingleton<ArchiveHandler>();
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SubscriptionConsumer>(sp, sp.GetRequiredService<ArchiveHandler>(), ConsumerWorker(ArchiveHandler.Subscription, instanceName)));
            services.AddHostedService(sp => sp.GetRequiredService<SubscriptionConsumer>());
            consumed.Add(ArchiveHandler.Subscription);
        }
        if (consumed.Count > 0)
        {
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DlqConsumer>(sp, (IReadOnlyList<string>)consumed));
            services.AddHostedService(sp => sp.GetRequiredService<DlqConsumer>());
        }
        return services;
    }

    /// <summary>`{subscription}@{instance}`: the worker name written into `processing.attempts`.</summary>
    public static string ConsumerWorker(string subscriptionId, string? instanceName) =>
        $"{subscriptionId}@{instanceName ?? Environment.MachineName.ToLowerInvariant()}";
}
