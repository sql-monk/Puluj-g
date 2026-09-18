namespace Puluj.Analytics.Contracts;

public sealed record RunDto(long Id, string Instance, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset UpdatedAt, string Status,
    long WatermarkFrom, long WatermarkTo, int MessagesScanned, int MessagesIndexed, string? Error);

/// <summary>State of the analytics service: where the index stands against the raw messages and how the last runs went.</summary>
public sealed record AnalyticsStatusDto(
    bool Initialized,
    long Watermark,
    long LatestRawMessageId,
    long Backlog,
    DateTimeOffset? HeartbeatAt,
    RunDto? LastRun,
    IReadOnlyList<RunDto> Runs,
    long MessagesIndexed,
    long SchemaBytes,
    IReadOnlyList<string> Migrations,
    AnalyticsInstanceDto? Instance);

/// <summary>What the analytics process says about itself (`Runtime:Worker:{name}:Status`, written every 10 s); null until the process publishes it.</summary>
public sealed record AnalyticsInstanceDto(string? Host, string? Version, DateTimeOffset? BuiltAt, DateTimeOffset? StartedAt, DateTimeOffset? At, int? Pid, long? WorkingSetBytes, double? CpuPercent, int? Threads);
