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
        services.AddSingleton<IndexProvider>();
        services.AddSingleton<IIndexes>(sp => sp.GetRequiredService<IndexProvider>());
        services.AddHostedService(sp => sp.GetRequiredService<IndexProvider>());
        services.AddSingleton<INormalizer, Normalizer>();
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.Section));
        services.AddSingleton(sp => new LlmBreaker(sp.GetRequiredService<IOptions<LlmOptions>>().Value.FailurePause));
        services.AddSingleton<RuleParser>();
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
}

/// <summary>Worker role names of the stage subscriptions (WorkerOptions.Roles).</summary>
public static class StageRoles
{
    public const string Normalizer = "normalizer";
    public const string Parser = "parser";
    public const string LlmWorker = "llm-worker";
    public const string Finalizer = "finalizer";
}
