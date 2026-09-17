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


// ---- P15 (ADR-0013): lifecycle projection — reconciliation, backfill progress and the «Аналітика повідомлень» report.

public sealed record LifecycleReconciliationDto(
    DateTimeOffset At, int WindowHours,
    long RawRows, long Posts, long Edits,
    long ProjectedRaw, long ProjectedPosts, long MissingRoots,
    long LateAnalyses, long LateCompletions,
    long PendingAnalysis, long PendingDomain,
    long UnavailableTimings, long UnavailableCompletion);

public sealed record LifecycleBackfillDto(long Cursor, long MaxRawMessageId, bool CaughtUp, DateTimeOffset? At);

public sealed record LifecycleStatusDto(bool Available, LifecycleBackfillDto Backfill, LifecycleReconciliationDto? Reconciliation);

public sealed record LifecycleBucketDto(DateTimeOffset At, long Raw, long Analyzed, long WithFacts, long DomainCompleted, long Failed, long NoText);

public sealed record LifecycleSourceDto(int SourceId, string Code, long Raw, long Posts, long Edits, long NoText, long WithPayload, long Facts, double? TextLengthP50, double? CollectDelayP50Seconds, double? CollectDelayP95Seconds, long Live, long History, double? MaxGapSeconds);

public sealed record LifecycleFunnelDto(long Raw, long Posts, long Stored, long Analyzed, long WithFacts, long DomainCompleted, long Visible, long StuckAnalysis, long StuckDomain, long UnavailableTimings, long UnavailableCompletion,
    double? StoredToAnalyzedP50Seconds, double? StoredToAnalyzedP95Seconds, double? AnalyzedToDomainP50Seconds, double? AnalyzedToDomainP95Seconds);

public sealed record LifecycleParseDto(IReadOnlyDictionary<string, long> Outcomes, IReadOnlyDictionary<string, long> Methods, long MultiFact, long Unlocated, long TotalFacts, IReadOnlyDictionary<string, long> RuleVersions, IReadOnlyDictionary<string, long> ModelVersions);

public sealed record LifecycleCostModelDto(string Model, long Calls, long InputTokens, long CacheTokens, long OutputTokens, decimal CostUsd, double? LatencyP50Ms, double? LatencyP95Ms, long Failures, long Late);

public sealed record LifecycleCostDto(long Calls, long Roots, decimal CostUsd, long InputTokens, long CacheTokens, long OutputTokens, double? CacheShare, IReadOnlyList<LifecycleCostModelDto> ByModel, IReadOnlyList<KeyValuePair<string, decimal>> BySource);

public sealed record LifecycleQualityDto(IReadOnlyDictionary<string, long> ReviewOutcomes, long? PrecisionSamples, string PrecisionRecall, string RulesVsLlm);

public sealed record LifecycleResultsDto(IReadOnlyDictionary<string, long> EventKinds, IReadOnlyDictionary<string, long> IncidentPrecision, long Incidents, long Tracks, long Alerts, long IncidentsWithProvenance, long ActiveIncidents);

public sealed record LifecycleHistoryDto(IReadOnlyList<LifecycleRunDto> Runs, long ActiveGenerationIncidents, IReadOnlyList<KeyValuePair<string, long>> IncidentsByGeneration);

public sealed record LifecycleRunDto(Guid RunId, string Kind, string? PipelineVersion, long Rows, IReadOnlyDictionary<string, long> Outcomes);

public sealed record LifecycleReportDto(
    DateTimeOffset From, DateTimeOffset To, int Hours, string Bucket,
    LifecycleFunnelDto Funnel,
    IReadOnlyList<LifecycleBucketDto> Timeline,
    IReadOnlyList<LifecycleSourceDto> Sources,
    LifecycleParseDto Parse,
    LifecycleQualityDto Quality,
    LifecycleCostDto Cost,
    LifecycleResultsDto Results,
    LifecycleHistoryDto History,
    LifecycleReconciliationDto? Reconciliation);
