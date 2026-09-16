using Microsoft.Extensions.DependencyInjection;

namespace Puluj.Messaging;

public static class DependencyInjection
{
    public const string RelayRole = "relay";
    public const string ArchiveRole = "archive";
    public const string RawWriterRole = "raw-writer";

    /// <summary>
    /// Broker runtime for the given roles (WorkerOptions): `relay` — topology declare, outbox relay, reconciliation and
    /// cleanup; `archive` — the archive subscription consumer; `raw-writer` — the raw-writer subscription (P04); the DLQ
    /// consumer covers every subscription consumed in this process. Infrastructure
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
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ReconciliationService>(sp, instanceName ?? Environment.MachineName.ToLowerInvariant()));
            services.AddHostedService(sp => sp.GetRequiredService<ReconciliationService>());
        }

        if (roles.Contains(ArchiveRole))
        {
            services.AddSingleton<ArchiveHandler>();
            services.AddSubscriptionConsumer<ArchiveHandler>(instanceName);
        }
        if (roles.Contains(RawWriterRole))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<RawWriterHandler>(sp);
                handler.Producer = ConsumerWorker(RawWriterHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<RawWriterHandler>(instanceName);
        }
        // One DLQ consumer per process for every subscription consumed here (the handlers registered so far and by
        // other modules, e.g. the processing stages); resolved lazily, so registration order does not matter.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DlqConsumer>(sp, (IReadOnlyList<string>)sp.GetServices<IDeliveryHandler>().Select(h => h.SubscriptionId).Distinct().ToList()));
        services.AddHostedService(sp => sp.GetRequiredService<DlqConsumer>());
        return services;
    }

    /// <summary>
    /// A subscription consumer for <typeparamref name="THandler"/> (already registered as a singleton): exposes it as
    /// <see cref="IDeliveryHandler"/> (DLQ coverage), one <see cref="SubscriptionConsumer"/> per subscription, hosted.
    /// </summary>
    public static IServiceCollection AddSubscriptionConsumer<THandler>(this IServiceCollection services, string? instanceName) where THandler : class, IDeliveryHandler
    {
        services.AddSingleton<IDeliveryHandler>(sp => sp.GetRequiredService<THandler>());
        services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<THandler>();
            return ActivatorUtilities.CreateInstance<SubscriptionConsumer>(sp, (IDeliveryHandler)handler, ConsumerWorker(handler.SubscriptionId, instanceName));
        });
        services.AddHostedService(sp => sp.GetServices<SubscriptionConsumer>().Single(c => c.Handler is THandler));
        return services;
    }

    /// <summary>`{subscription}@{instance}`: the worker name written into `processing.attempts`.</summary>
    public static string ConsumerWorker(string subscriptionId, string? instanceName) =>
        $"{subscriptionId}@{instanceName ?? Environment.MachineName.ToLowerInvariant()}";
}
