using System.Text.Json;

namespace Puluj.Domain.Entities;

/// <summary>A request to deliver one immutable raw message to the Entity Extractor.</summary>
public sealed class EntityDelivery
{
    public Guid DeliveryId { get; set; }
    public long RawMessageId { get; set; }
    public RawMessage? RawMessage { get; set; }
    public required string Origin { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public short? Result { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public ICollection<EntityDeliveryAttempt> DeliveryAttempts { get; set; } = [];
}

/// <summary>One HTTP attempt for an Entity Extractor delivery.</summary>
public sealed class EntityDeliveryAttempt
{
    public long DeliveryAttemptId { get; set; }
    public Guid DeliveryId { get; set; }
    public EntityDelivery? Delivery { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Outcome { get; set; }
    public int? StatusCode { get; set; }
    public short? Result { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>The current editable Python body for one extractor. There is deliberately no version history.</summary>
public sealed class EntityExtractorDefinition
{
    public long ExtractorId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public bool Enabled { get; set; }
    public int ExecutionOrder { get; set; }
    public int TimeoutMs { get; set; }
}

/// <summary>Definition of a concrete entity table, its fields, and optional map presentation.</summary>
public sealed class EntityDefinition
{
    public long EntityDefinitionId { get; set; }
    public required string EntityName { get; set; }
    public required string TableName { get; set; }
    public required JsonDocument Fields { get; set; }
    public JsonDocument? MapSettings { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Technical audit for processing one delivery in the Entity Extractor.</summary>
public sealed class EntityProcessingRun
{
    public long ProcessingRunId { get; set; }
    public Guid DeliveryId { get; set; }
    public EntityDelivery? Delivery { get; set; }
    public long RawMessageId { get; set; }
    public RawMessage? RawMessage { get; set; }
    public required string Status { get; set; }
    public Guid ClaimToken { get; set; }
    public short? Result { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public ICollection<EntityExtractorRun> ExtractorRuns { get; set; } = [];
}

/// <summary>Technical audit for one Python extractor in a processing run.</summary>
public sealed class EntityExtractorRun
{
    public long ExtractorRunId { get; set; }
    public long ProcessingRunId { get; set; }
    public EntityProcessingRun? ProcessingRun { get; set; }
    public long ExtractorId { get; set; }
    public EntityExtractorDefinition? Extractor { get; set; }
    public required string Status { get; set; }
    public int WritesCount { get; set; }
    public int DurationMs { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? Error { get; set; }
    public ICollection<EntityWriteAudit> EntityWrites { get; set; } = [];
}

/// <summary>Technical pointer to one entity row written by an extractor.</summary>
public sealed class EntityWriteAudit
{
    public long EntityWriteId { get; set; }
    public long ProcessingRunId { get; set; }
    public EntityProcessingRun? ProcessingRun { get; set; }
    public long? ExtractorRunId { get; set; }
    public EntityExtractorRun? ExtractorRun { get; set; }
    public long EntityDefinitionId { get; set; }
    public EntityDefinition? EntityDefinition { get; set; }
    public required string TableName { get; set; }
    public long EntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
