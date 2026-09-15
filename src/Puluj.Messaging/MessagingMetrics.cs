using System.Diagnostics.Metrics;
using Puluj.Infrastructure;

namespace Puluj.Messaging;

/// <summary>
/// Broker/outbox/inbox metrics (plan §9 minimum for wave 2, ADR-0007 alarms). Counters are per event; the gauges
/// hold the last reconciliation scan (outbox unconfirmed age/count, overdue deliveries), so their cardinality stays
/// bounded: tags are subscription id, outcome and queue only.
/// </summary>
public sealed class MessagingMetrics
{
    private readonly Counter<long> _published;
    private readonly Counter<long> _publishFailed;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _quarantined;
    private readonly Counter<long> _declareFailed;
    private readonly Histogram<double> _confirmLatency;

    private long _outboxUnconfirmed;
    private double _outboxOldestAgeSeconds;
    private long _deliveriesOverdue;
    private long _quarantineOpen;

    public MessagingMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(PulujMetrics.MeterName);
        _published = meter.CreateCounter<long>("puluj.outbox.published", description: "Outbox rows confirmed by the broker");
        _publishFailed = meter.CreateCounter<long>("puluj.outbox.publish_failed", description: "Publish attempts that ended in nack, return (unroutable) or timeout, by reason");
        _delivered = meter.CreateCounter<long>("puluj.inbox.delivered", description: "Deliveries handled, by subscription and outcome");
        _duplicates = meter.CreateCounter<long>("puluj.inbox.duplicates", description: "Redeliveries absorbed by the inbox without a business effect");
        _quarantined = meter.CreateCounter<long>("puluj.inbox.quarantined", description: "Deliveries moved to quarantine/DLQ, by subscription and reason");
        _declareFailed = meter.CreateCounter<long>("puluj.topology.declare_failed", description: "Queue declares refused by the broker (argument drift)");
        _confirmLatency = meter.CreateHistogram<double>("puluj.outbox.confirm_latency", unit: "ms", description: "Publish → broker confirm per outbox batch");
        meter.CreateObservableGauge("puluj.outbox.unconfirmed", () => Volatile.Read(ref _outboxUnconfirmed), description: "Outbox rows without confirm at the last reconciliation scan");
        meter.CreateObservableGauge("puluj.outbox.oldest_unconfirmed_age", () => Volatile.Read(ref _outboxOldestAgeSeconds), unit: "s");
        meter.CreateObservableGauge("puluj.deliveries.overdue", () => Volatile.Read(ref _deliveriesOverdue), description: "Expected deliveries without a terminal receipt older than the SLO");
        meter.CreateObservableGauge("puluj.quarantine.open", () => Volatile.Read(ref _quarantineOpen));
    }

    public void Published(int count, double latencyMs)
    {
        _published.Add(count);
        _confirmLatency.Record(latencyMs);
    }

    /// <summary>reason: nack | unroutable | timeout | error</summary>
    public void PublishFailed(string reason) => _publishFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>outcome: completed | noop | quarantined | duplicate | retry</summary>
    public void Delivered(string subscription, string outcome) =>
        _delivered.Add(1, new KeyValuePair<string, object?>("subscription", subscription), new KeyValuePair<string, object?>("outcome", outcome));

    public void Duplicate(string subscription) => _duplicates.Add(1, new KeyValuePair<string, object?>("subscription", subscription));

    public void Quarantined(string subscription, string reason) =>
        _quarantined.Add(1, new KeyValuePair<string, object?>("subscription", subscription), new KeyValuePair<string, object?>("reason", reason));

    public void TopologyDeclareFailed(string queue) => _declareFailed.Add(1, new KeyValuePair<string, object?>("queue", queue));

    public void Reconciled(long outboxUnconfirmed, double oldestAgeSeconds, long deliveriesOverdue, long quarantineOpen)
    {
        Volatile.Write(ref _outboxUnconfirmed, outboxUnconfirmed);
        Volatile.Write(ref _outboxOldestAgeSeconds, oldestAgeSeconds);
        Volatile.Write(ref _deliveriesOverdue, deliveriesOverdue);
        Volatile.Write(ref _quarantineOpen, quarantineOpen);
    }
}
