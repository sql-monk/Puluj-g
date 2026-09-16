namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// `Ops` section (P13, ADR-0012): the SLO thresholds the alarm rules are evaluated with. Defaults follow ADR-0007
/// (5 min live / 1 h history for an expected delivery without a receipt, 60 s for an unconfirmed outbox row) and the
/// admin panel's existing 90 s heartbeat window; P16 revisits the absolute values against measured load.
/// </summary>
public sealed class OpsOptions
{
    public const string Section = "Ops";

    public SloOptions Slo { get; set; } = new();

    public sealed class SloOptions
    {
        /// <summary>Oldest expected delivery without a receipt per lane (seconds); `oldest_age_slo`.</summary>
        public Dictionary<string, int> OldestAgeSeconds { get; set; } = new(StringComparer.Ordinal) { ["live"] = 300, ["history"] = 3600, ["replay"] = 3600 };
        /// <summary>Oldest unconfirmed outbox row → `outbox_stuck` (warn), and → error past the critical value.</summary>
        public int OutboxUnconfirmedSeconds { get; set; } = 60;
        public int OutboxCriticalSeconds { get; set; } = 300;
        /// <summary>Heartbeat older than this = stale worker (the panel's existing window; heartbeats are written every 30 s).</summary>
        public int StaleHeartbeatSeconds { get; set; } = 90;
        /// <summary>A `running` attempt older than this with nothing completing → `inflight_stuck`.</summary>
        public int InflightStuckSeconds { get; set; } = 300;
        /// <summary>Pending deliveries older than this on an active lane without any live consumer → `required_consumer_missing`.</summary>
        public int RequiredConsumerMissingSeconds { get; set; } = 60;
        /// <summary>The snapshot is served from cache for this long (the UI polls every 10 s; several viewers share one computation).</summary>
        public int SnapshotCacheSeconds { get; set; } = 5;
    }
}
