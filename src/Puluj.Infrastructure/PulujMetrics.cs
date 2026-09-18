using System.Diagnostics.Metrics;

namespace Puluj.Infrastructure;

/// <summary>Application metrics (spec §29: source latency monitoring, parser error logging).</summary>
public sealed class PulujMetrics
{
    public const string MeterName = "Puluj";

    private readonly Counter<long> _rawReceived;
    private readonly Counter<long> _targetsCreated;
    private readonly Counter<long> _parserUnmatched;
    private readonly Counter<long> _processingErrors;
    private readonly Histogram<double> _sourceLatency;
    private readonly Counter<long> _llmCalls;
    private readonly Counter<long> _rawProcessed;
    private readonly Histogram<double> _processingStage;

    public PulujMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _rawReceived = meter.CreateCounter<long>("puluj.rawmessages.received");
        _targetsCreated = meter.CreateCounter<long>("puluj.targets.created");
        _parserUnmatched = meter.CreateCounter<long>("puluj.parser.unmatched");
        _processingErrors = meter.CreateCounter<long>("puluj.processing.errors");
        _sourceLatency = meter.CreateHistogram<double>("puluj.source.latency", unit: "s", description: "ReceivedAt - PublishedAt");
        _llmCalls = meter.CreateCounter<long>("puluj.llm.calls");
        _rawProcessed = meter.CreateCounter<long>("puluj.rawmessages.processed", description: "Raw messages finished by this instance, by outcome");
        _processingStage = meter.CreateHistogram<double>("puluj.processing.stage", unit: "ms", description: "Time per pipeline stage of one raw message: parse, lock (waiting for the store lock), store");
    }

    public void RawReceived(string source, TimeSpan latency)
    {
        var tag = new KeyValuePair<string, object?>("source", source);
        _rawReceived.Add(1, tag);
        _sourceLatency.Record(latency.TotalSeconds, tag);
    }

    public void TargetCreated(string source, string method) =>
        _targetsCreated.Add(1, new("source", source), new("method", method));

    public void ParserUnmatched(string source) => _parserUnmatched.Add(1, new KeyValuePair<string, object?>("source", source));

    public void ProcessingError(string stage) => _processingErrors.Add(1, new KeyValuePair<string, object?>("stage", stage));

    public void LlmCall(string outcome) => _llmCalls.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>outcome: processed | skipped | failed | retried (failed, will be retried) | retried_transient (deadlock or the like, attempt not counted) | released (claim returned unprocessed).</summary>
    public void RawProcessed(string instance, string outcome) =>
        _rawProcessed.Add(1, new KeyValuePair<string, object?>("instance", instance), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>stage: parse | lock | store. Lock time close to store time means the store lock is the throughput ceiling.</summary>
    public void ProcessingStage(string stage, double milliseconds) =>
        _processingStage.Record(milliseconds, new KeyValuePair<string, object?>("stage", stage));
}
