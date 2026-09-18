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
using Puluj.Processing.Structured;
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
        services.AddSingleton<ProcessorSingletonLock>();
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

    /// <summary>Indexes, normalizer, rule parser, target builder and structured handler used by the processor.</summary>
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
        services.AddSingleton<LlmParser>(); // the mapping/prompt owner and IParser implementation
        services.TryAddSingleton<ILlmCompletion, AnthropicCompletion>();
        return services;
    }
}
