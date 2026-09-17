namespace Puluj.Analytics.Persistence;

public enum RunStatus
{
    Running = 0,
    Ok = 1,
    Failed = 2,
}

/// <summary>Key/value state of the service: `watermark` (last raw_message_id taken), `track_firsts_at`.</summary>
public class AnalyticsState
{
    public const string WatermarkKey = "watermark";
    public const string TrackFirstsAtKey = "track_firsts_at";

    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One wake-up of the loop: from the first batch to the moment the backlog is drained. `UpdatedAt` ticks after every batch, so a stale `Running` row means the container died.</summary>
public class AnalysisRun
{
    public long RunId { get; set; }
    public required string Instance { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; }
    public long WatermarkFrom { get; set; }
    public long WatermarkTo { get; set; }
    public int MessagesScanned { get; set; }
    public int MessagesFingerprinted { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// One row per raw message: which logical post it belongs to (edits share the `PostKey`).  This is the lightweight
/// index used by the independent track-first analytics; it deliberately carries no text similarity or forwarding data.
/// </summary>
public class MessageFingerprint
{
    public long RawMessageId { get; set; }
    public int SourceId { get; set; }
    /// <summary>`source_message_id` without the `:e{timestamp}` edit suffix.</summary>
    public required string PostKey { get; set; }
    public bool IsEdit { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>Per day, source and target category: how often the source was the first to report a track, how many tracks it took part in, and how far behind the first it was otherwise.</summary>
public class TrackFirst
{
    public DateOnly Day { get; set; }
    public int SourceId { get; set; }
    public int TargetCategoryId { get; set; }
    public required string CategoryCode { get; set; }
    public int Firsts { get; set; }
    public int Participations { get; set; }
    public double LagSecondsSum { get; set; }
    public int LagCount { get; set; }
}

