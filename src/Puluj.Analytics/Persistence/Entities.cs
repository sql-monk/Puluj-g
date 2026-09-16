namespace Puluj.Analytics.Persistence;

public enum RunStatus
{
    Running = 0,
    Ok = 1,
    Failed = 2,
}

public enum CopyKind
{
    /// <summary>A post about the same parsed event, but not an explicit Telegram forward.</summary>
    Near = 1,
    /// <summary>Legacy value for text-verbatim pairs created before semantic matching.</summary>
    Verbatim = 2,
    /// <summary>A Telegram forward whose origin channel is the original's source.</summary>
    Forward = 3,
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
    public int PairsFound { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// One row per raw message with text: which logical post it belongs to (edits share the `PostKey`), where it was
/// forwarded from, plus legacy text-fingerprint columns. Event facts, not these columns, drive matching.
/// </summary>
public class MessageFingerprint
{
    public long RawMessageId { get; set; }
    public int SourceId { get; set; }
    /// <summary>`source_message_id` without the `:e{timestamp}` edit suffix.</summary>
    public required string PostKey { get; set; }
    public bool IsEdit { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    /// <summary>Telegram channel id of the source (from the payload), used to resolve forwards to sources.</summary>
    public long? ChannelId { get; set; }
    /// <summary>Raw `forwardedFrom` of the payload (`channel 123…` or a display name).</summary>
    public string? ForwardedFrom { get; set; }
    /// <summary>The forward origin resolved to one of our sources.</summary>
    public int? ForwardedSourceId { get; set; }
    /// <summary>Forwarded from a channel we do not collect.</summary>
    public bool ForwardedExternal { get; set; }
    public int TextLength { get; set; }
    public int ShingleCount { get; set; }
    public byte[]? MinHash { get; set; }
    public long[]? Bands { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>
/// A copy relation between two logical posts of different sources (the key is per post, so an edit of either side
/// updates the row instead of adding one). `IsPrimary` marks the earliest original of the copy — the one credited
/// as the first publisher; the other rows say whom the copier actually reads.
/// </summary>
public class MessageCopy
{
    public int CopySourceId { get; set; }
    public required string CopyPostKey { get; set; }
    public int OriginalSourceId { get; set; }
    public required string OriginalPostKey { get; set; }
    public long CopyRawMessageId { get; set; }
    public long OriginalRawMessageId { get; set; }
    public DateTimeOffset CopyPublishedAt { get; set; }
    public DateTimeOffset OriginalPublishedAt { get; set; }
    public double DelaySeconds { get; set; }
    public float Jaccard { get; set; }
    public float Containment { get; set; }
    public CopyKind Kind { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset FoundAt { get; set; }
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

