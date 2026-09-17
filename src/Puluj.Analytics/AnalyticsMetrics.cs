using System.Diagnostics.Metrics;

namespace Puluj.Analytics;

/// <summary>OpenTelemetry metrics of the analytics service (meter `Puluj.Analytics`).</summary>
public sealed class AnalyticsMetrics
{
    public const string MeterName = "Puluj.Analytics";

    private readonly Counter<long> _indexed;
    private readonly Histogram<double> _runDuration;

    /// <summary>Raw messages above the watermark after the last run.</summary>
    public long Backlog { get; set; }

    public AnalyticsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _indexed = meter.CreateCounter<long>("puluj.analytics.messages.indexed");
        _runDuration = meter.CreateHistogram<double>("puluj.analytics.run.duration", unit: "s");
        meter.CreateObservableGauge("puluj.analytics.backlog", () => Backlog);
    }

    public void MessagesIndexed(int count) => _indexed.Add(count);

    public void RunFinished(TimeSpan elapsed) => _runDuration.Record(elapsed.TotalSeconds);
}
