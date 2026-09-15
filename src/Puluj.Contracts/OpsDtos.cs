namespace Puluj.Contracts;

// ---- Instance telemetry: written by every Worker / Analytics process into app_settings
// (`Runtime:Worker:{instance}:Status`, JSON, every 10 s) and read by the admin panel. See docs/plan-admin-ops.md §2.1.

/// <summary>What one running instance says about itself. Processing / Llm are null for instances without the processing role.</summary>
public sealed record WorkerStatusDto(
    string Instance,
    string Host,
    IReadOnlyList<string> Roles,
    string Version,
    DateTimeOffset BuiltAt,
    DateTimeOffset StartedAt,
    DateTimeOffset At,
    int Pid,
    long WorkingSetBytes,
    double CpuPercent,
    int Threads,
    ProcessingStatusDto? Processing,
    LlmStatusDto? Llm,
    string? Paused,
    ProcessingPauseDto? Pause = null);

/// <summary>Why all processors are temporarily held. SourceStatus is the live collector status that owns the hold.</summary>
public sealed record ProcessingPauseDto(string Reason, string? SourceStatus);

/// <summary>Counters since the process started plus timings over the last five minutes.</summary>
public sealed record ProcessingStatusDto(
    int Concurrency,
    long Processed,
    long Skipped,
    long Failed,
    long Retried,
    long RetriedTransient,
    double PerMinute1,
    double PerMinute5,
    StageTimingDto Parse,
    StageTimingDto Lock,
    StageTimingDto Store,
    StageTimingDto Total,
    DateTimeOffset? LastProcessedAt,
    long? LastRawMessageId,
    IReadOnlyList<ClaimDto> Claims);

public sealed record StageTimingDto(int Samples, double MeanMs, double P50Ms, double P90Ms, double MaxMs);

/// <summary>A raw message this instance holds right now.</summary>
public sealed record ClaimDto(long RawMessageId, DateTimeOffset Since);

public sealed record LlmStatusDto(bool Enabled, string Model, DateTimeOffset? PausedUntil, string? PauseReason, long Calls, long Failures);

// ---- Admin panel: instances, containers, pipeline. See docs/plan-admin-ops.md §2.4.

/// <summary>One instance as the panel shows it: heartbeat, its own status document, its share of the work, its container.</summary>
public sealed record WorkerInstanceDto(
    string Name,
    /// <summary>processor | collector-telegram | collector-alerts | analytics | worker | migrate | other</summary>
    string Kind,
    bool Alive,
    DateTimeOffset? HeartbeatAt,
    WorkerStatusDto? Status,
    long Processed24h,
    long InProgress,
    string? ContainerId,
    string? ContainerName,
    string? ContainerState,
    double? CpuPercent,
    long? MemoryBytes);

public sealed record ContainerDto(
    string Id,
    string Name,
    string Service,
    string Image,
    /// <summary>running | exited | restarting | paused | created | dead</summary>
    string State,
    string Status,
    DateTimeOffset? StartedAt,
    double? CpuPercent,
    long? MemoryBytes,
    long? MemoryLimitBytes,
    /// <summary>False for the protected services (admin, postgis, migrate): view only.</summary>
    bool Controllable,
    int? ReplicaNumber);

public sealed record ContainersDto(bool Available, string? Unavailable, string Project, IReadOnlyList<ContainerDto> Containers, int ProcessorReplicas);

public sealed record ContainerActionResultDto(bool Ok, string Message, string Output);

public sealed record ScaleRequest(int Replicas);

public sealed record PipelineTotalsDto(
    long Received, long Processed, long Skipped, long Failed, long Pending, long InProgress,
    long Targets, long Duplicates, long Tracks, long Errors,
    double? P50Ms, double? P90Ms, double? MeanMs);

/// <summary>Series: messages received per bucket, aligned with PipelineReportDto.BucketStarts.</summary>
public sealed record PipelineSourceDto(
    int SourceId, string Code, string Name, string Type, bool Enabled,
    long Received, long Processed, long Skipped, long Failed, long Pending,
    long WithTargets, long Targets, long Tracks,
    double? MedianLagSeconds, double? P50Ms, double? P90Ms,
    IReadOnlyList<int> Series);

public sealed record PipelineBucketDto(DateTimeOffset At, int Received, int Processed, int Targets, int Errors, int Transient, double? P50Ms, double? P90Ms);

/// <summary>Who did how much: by raw_messages.claimed_by.</summary>
public sealed record PipelineInstanceDto(string Instance, long Processed, double? P50Ms, double? P90Ms, DateTimeOffset? LastAt);

public sealed record PipelineReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    /// <summary>hour | day</summary>
    string Bucket,
    IReadOnlyList<DateTimeOffset> BucketStarts,
    PipelineTotalsDto Totals,
    IReadOnlyList<PipelineSourceDto> Sources,
    IReadOnlyList<PipelineBucketDto> Timeline,
    IReadOnlyList<PipelineInstanceDto> Instances,
    IReadOnlyDictionary<string, long> Queue,
    IReadOnlyDictionary<string, long> ErrorsByStage,
    IReadOnlyList<ProcessingErrorDto> RecentErrors);
