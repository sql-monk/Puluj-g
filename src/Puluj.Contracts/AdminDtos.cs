namespace Puluj.Contracts;

/// <summary>One configurable key. Secret values are never sent; `hasValue` tells whether something is set.</summary>
public sealed record SettingDto(string Key, string? Value, bool IsSecret, bool HasValue, string Source);

public sealed record SettingsUpdateRequest(Dictionary<string, string?> Values);

public sealed record AdminSourceDto(
    int Id, string Code, string Name, string Type, bool Enabled, double TrustLevel, int Priority, string? Url, string? Channel,
    int? PollingIntervalSeconds, string? HomeRegion, bool HasToken, long RawMessageCount,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastMessageAt, int ConsecutiveFailures, string? LastError, string Status);

/// <param name="Token">API token stored on the source (write-only: the DTO only says whether one is set). Empty string removes it.</param>
public sealed record SourceUpdateRequest(bool? Enabled, double? TrustLevel, string? Name, int? Priority, int? PollingIntervalSeconds, string? Channel, string? Url, string? HomeRegion, string? Token);

public sealed record SourceCreateRequest(string Name, string Type, string? Channel, string? Url, double? TrustLevel, int? Priority, int? PollingIntervalSeconds);

public sealed record AdminStatusDto(
    bool AlertsConfigured, bool TelegramConfigured, bool LlmConfigured,
    string? TelegramStatus, bool AdminTokenSet, bool WorkerAlive, DateTimeOffset? WorkerLastSeen);

public sealed record TestResultDto(bool Ok, string Message);

public sealed record TelegramCodeRequest(string Code);

// ---- Operations: per-component status, statistics and logs (admin panel only) ----

/// <summary>One service of the system as the admin panel sees it: `ok`, `warn`, `down` or `unknown`.</summary>
public sealed record ServiceStatusDto(string Name, string Status, string? Detail, DateTimeOffset? LastSeen);

public sealed record DbOverviewDto(string Version, long SizeBytes, int Connections, string? LastMigration, int MigrationCount);

/// <param name="ProcessorCount">Message processor instances with a fresh heartbeat (replicas of the `processor` service).</param>
public sealed record OpsOverviewDto(DateTimeOffset GeneratedAt, IReadOnlyList<ServiceStatusDto> Services, DbOverviewDto Db, int ProcessorCount);

/// <param name="PerHour">Messages received per hour for the last 24 hours, oldest first.</param>
public sealed record CollectorStatusDto(
    int SourceId, string Code, string Name, string Type, bool Enabled,
    DateTimeOffset? LastPolledAt, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastMessageAt, string? LastError, int ConsecutiveFailures,
    long Messages24h, IReadOnlyList<int> PerHour);

public sealed record ProcessingErrorDto(long Id, DateTimeOffset OccurredAt, string Stage, string Message, int? SourceId, long? RawMessageId, string? Exception);

/// <summary>One application table together with PostgreSQL's cumulative maintenance and write counters.</summary>
public sealed record DbTableDto(
    string Name, long Rows, long Bytes, long Inserts, long Updates, long Deletes, long DeadRows,
    DateTimeOffset? LastVacuumAt, DateTimeOffset? LastAnalyzeAt);

public sealed record DbRoleConnectionsDto(string Role, int Connections);

/// <summary>Lightweight PostgreSQL health signals. Counters are since the database statistics were last reset.</summary>
public sealed record DbMonitoringDto(
    int ActiveConnections, int IdleConnections, long TransactionsCommitted, long TransactionsRolledBack,
    double CacheHitRatio, long DeadRows);

public sealed record DbReportDto(
    string Version, long SizeBytes, IReadOnlyList<DbTableDto> Tables, IReadOnlyList<string> Migrations,
    IReadOnlyList<DbRoleConnectionsDto> Connections, DbMonitoringDto Monitoring);

/// <summary>A bounded, display-safe result of the database browser or read-only SQL console.</summary>
public sealed record DbQueryResultDto(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated, long ElapsedMs);

/// <summary>Body for the read-only SQL console. The server accepts one SELECT or WITH … SELECT statement only.</summary>
public sealed record DbQueryRequest(string Sql);

/// <summary>Explicit acknowledgement required before clearing derived pipeline data.</summary>
public sealed record DbReprocessRequest(string Confirmation);

/// <summary>Explicit acknowledgement for erasing all operational data while retaining the deployable schema and configuration.</summary>
public sealed record DbClearRequest(string Confirmation);

public sealed record LogFileDto(string Name, string Service, long Bytes, DateTimeOffset ModifiedAt);

public sealed record LogTailDto(string File, IReadOnlyList<string> Lines, bool Truncated, long Bytes);
