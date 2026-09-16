namespace Puluj.Analytics;

/// <summary>Settings of the source analytics service (`Analytics` section). Pairing is based on parsed event facts;
/// text fingerprints are retained only as diagnostic evidence on an already accepted event pair.</summary>
public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";

    /// <summary>Instance name for the heartbeat (`Runtime:Worker:{Name}:Heartbeat`), logs and the runs table.</summary>
    public string Name { get; set; } = "analytics";

    /// <summary>Sleep between runs once the backlog is drained.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Raw messages taken per transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Only rows received at least this long ago are taken, so the watermark never passes a row whose insert is still uncommitted.</summary>
    public TimeSpan SafetyLag { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum difference between reports and their observed facts for them to represent one event.</summary>
    public TimeSpan EventWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Minimum text length retained in existing configuration; semantic pairing does not use it.</summary>
    public int MinTextLength { get; set; } = 40;

    /// <summary>Maximum matching event candidates read per parsed event type/category.</summary>
    public int CandidateScan { get; set; } = 200;

    /// <summary>Days of `track_firsts` rebuilt after each run.</summary>
    public int TrackFirstsDays { get; set; } = 30;

    /// <summary>P15: the lifecycle report is served from cache for this long (several viewers share one computation).</summary>
    public int ReportCacheSeconds { get; set; } = 30;
    /// <summary>P15: a lifecycle row without an analysis (or without a domain completion) older than this counts as stuck in the funnel.</summary>
    public int LifecycleStaleMinutes { get; set; } = 15;
    /// <summary>P15: backfill batch (raw rows per transaction) and batches per loop pass.</summary>
    public int LifecycleBackfillBatch { get; set; } = 2000;
    public int LifecycleBackfillBatchesPerPass { get; set; } = 5;
    /// <summary>P15: reconciliation window and the grace a row gets before it is considered late.</summary>
    public TimeSpan LifecycleReconcileWindow { get; set; } = TimeSpan.FromHours(48);
    public TimeSpan LifecycleReconcileGrace { get; set; } = TimeSpan.FromMinutes(2);
}
