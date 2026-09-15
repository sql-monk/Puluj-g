using System.Text.Json;

namespace Puluj.Domain.Entities.Processing;

/// <summary>
/// One processing run (ADR-0005): a set of pinned versions on one lane. P03 keeps a single open run per lane so every
/// envelope carries a `processing_run_id`; state machine, replay generations, checkpoints and promote are P14.
/// </summary>
public class ProcessingRun
{
    public Guid RunId { get; set; }
    public required string Lane { get; set; }
    /// <summary>live | history | replay</summary>
    public required string Kind { get; set; }
    /// <summary>created | running | paused | verified | promoted | rolled_back | cancelled | failed | completed | superseded</summary>
    public required string State { get; set; }
    public Guid? GenerationId { get; set; }
    public Guid? SupersedesRunId { get; set; }
    public Guid? ReplaysRunId { get; set; }
    public JsonDocument? Versions { get; set; }
    public JsonDocument? Scope { get; set; }
    public JsonDocument? Checkpoint { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Set of domain results that can be made active (ADR-0005). One active generation; promote/rollback are P14.</summary>
public class ProcessingGeneration
{
    public Guid GenerationId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }
    public string? VerifiedBy { get; set; }
}

/// <summary>Result of one stage for one raw message in one run (ADR-0006 `processing.stage_results`); writers are P05/P06.</summary>
public class StageResult
{
    public long StageResultId { get; set; }
    public long RawMessageId { get; set; }
    public Guid RunId { get; set; }
    public required string Stage { get; set; }
    public required string StageVersion { get; set; }
    public required string Outcome { get; set; }
    public JsonDocument? Outputs { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Worker { get; set; }
    public JsonDocument? Versions { get; set; }
}
