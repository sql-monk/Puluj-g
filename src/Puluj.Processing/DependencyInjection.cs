using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Puluj.Processing.Correlation;
using Puluj.Processing.Indexes;
using Puluj.Processing.Llm;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Stages;
using Puluj.Processing.Structured;
using Puluj.Messaging;
using Puluj.Processing.Text;

namespace Puluj.Processing;

public static class DependencyInjection
{
    /// <param name="instanceName">Name this processor writes into raw_messages.claimed_by; unique per running instance
    /// (WorkerOptions.InstanceName). Defaults to the machine name.</param>
    public static IServiceCollection AddPulujProcessing(this IServiceCollection services, IConfiguration configuration, string? instanceName = null)
    {
        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.Section));
        services.AddSingleton(new ProcessorIdentity(instanceName ?? Environment.MachineName));
        services.AddSingleton<RawMessageClaims>();
        services.AddSingleton<ProcessingStats>();
        services.AddPulujParsing(configuration);
        services.AddSingleton<IParser>(sp => sp.GetRequiredService<LlmParser>()); // rules first, model only as a fallback
        services.AddSingleton<RawMessageProcessor>();
        services.AddHostedService<ProcessingLoop>();

        services.Configure<CorrelationOptions>(configuration.GetSection(CorrelationOptions.Section));
        services.AddSingleton<ITargetSink, Structured.TextAlertSink>(); // before correlation: it needs the intervals in place
        services.AddSingleton<ITargetSink, CorrelationSink>();
        services.AddHostedService<TrackWatchdog>();
        return services;
    }

    /// <summary>Indexes, normalizer, rule parser, target builder, structured handler: what both the legacy processor and the stage workers need. Idempotent.</summary>
    public static IServiceCollection AddPulujParsing(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IndexProvider)))
        {
            return services;
        }
        services.Configure<Rules.RulesetOptions>(configuration.GetSection(Rules.RulesetOptions.Section));
        services.AddSingleton<IndexProvider>();
        services.AddSingleton<IIndexes>(sp => sp.GetRequiredService<IndexProvider>());
        services.AddHostedService(sp => sp.GetRequiredService<IndexProvider>());
        services.AddSingleton<INormalizer, Normalizer>();
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.Section));
        services.AddSingleton(sp => new LlmBreaker(sp.GetRequiredService<IOptions<LlmOptions>>().Value.FailurePause));
        services.AddSingleton<RuleParser>();
        services.AddSingleton<Rules.RulesetEvaluator>();
        services.AddSingleton<Rules.RulesetPreview>();
        services.AddSingleton<TargetBuilder>();
        services.AddSingleton<AlertsInUaHandler>();
        services.AddSingleton<LlmParser>(); // the mapping/prompt owner; also the legacy IParser (registered separately by AddPulujProcessing)
        services.TryAddSingleton<ILlmCompletion, AnthropicCompletion>();
        return services;
    }

    /// <summary>
    /// Stage workers of P05 as broker subscriptions (roles `normalizer`, `parser`): pure recognition with persisted stage
    /// results, no domain writes. Requires <c>AddPulujMessaging</c> (broker runtime) in the same process; the legacy
    /// processing loop is not started by this call.
    /// </summary>
    public static IServiceCollection AddPulujStages(this IServiceCollection services, IConfiguration configuration, IReadOnlySet<string> roles, string? instanceName = null)
    {
        services.AddPulujParsing(configuration);
        services.AddSingleton<AlertsInUaStructuredAdapter>();
        if (roles.Contains(StageRoles.Normalizer))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<NormalizerHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(NormalizerHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<NormalizerHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.Parser))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<ParserHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(ParserHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<ParserHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.LlmWorker))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<LlmWorkerHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(LlmWorkerHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<LlmWorkerHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.Finalizer))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<FinalizerHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(FinalizerHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<FinalizerHandler>(instanceName);
        }
        return services;
    }

    /// <summary>
    /// P09 domain writers (`track-worker`, `alert-worker`) and the command-emitting `watchdog`: the platform-path owners of
    /// tracks and alert intervals. Never registered together with the legacy loop in one process; the cutover procedure
    /// (ADR-0009) stops the `processing` role first. Reuses the legacy sinks as singletons.
    /// </summary>
    public static IServiceCollection AddPulujDomainWriters(this IServiceCollection services, IConfiguration configuration, IReadOnlySet<string> roles, string? instanceName = null)
    {
        services.AddPulujParsing(configuration);
        services.Configure<CorrelationOptions>(configuration.GetSection(CorrelationOptions.Section));
        services.TryAddSingleton<CorrelationSink>();
        services.TryAddSingleton<Structured.TextAlertSink>();
        if (roles.Contains(StageRoles.TrackWorker))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<Writers.TrackWriterHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(Writers.TrackWriterHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<Writers.TrackWriterHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.AlertWorker))
        {
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<Writers.AlertWriterHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(Writers.AlertWriterHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<Writers.AlertWriterHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.IncidentWorker))
        {
            services.TryAddSingleton<Incidents.IncidentStateWriter>();
            services.AddSingleton(sp =>
            {
                var handler = ActivatorUtilities.CreateInstance<Incidents.IncidentWriterHandler>(sp);
                handler.Producer = Puluj.Messaging.DependencyInjection.ConsumerWorker(Incidents.IncidentWriterHandler.Subscription, instanceName);
                return handler;
            });
            services.AddSubscriptionConsumer<Incidents.IncidentWriterHandler>(instanceName);
        }
        if (roles.Contains(StageRoles.Watchdog))
        {
            services.AddSingleton(sp =>
            {
                var watchdog = ActivatorUtilities.CreateInstance<Writers.DomainWatchdog>(sp);
                watchdog.Instance = Puluj.Messaging.DependencyInjection.ConsumerWorker(Writers.DomainWatchdog.Producer, instanceName);
                return watchdog;
            });
            services.AddHostedService(sp => sp.GetRequiredService<Writers.DomainWatchdog>());
        }
        return services;
    }
}

/// <summary>Worker role names of the stage subscriptions (WorkerOptions.Roles).</summary>
public static class StageRoles
{
    public const string Normalizer = "normalizer";
    public const string Parser = "parser";
    public const string LlmWorker = "llm-worker";
    public const string Finalizer = "finalizer";
    public const string TrackWorker = "track-worker";
    public const string AlertWorker = "alert-worker";
    public const string Watchdog = "watchdog";
    public const string IncidentWorker = "incident-worker";
    public static readonly string[] DomainWriters = [TrackWorker, AlertWorker, Watchdog, IncidentWorker];
}
