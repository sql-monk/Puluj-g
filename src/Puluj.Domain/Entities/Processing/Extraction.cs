using System.Text.Json;

namespace Puluj.Domain.Entities.Processing;

/// <summary>
/// The canonical, immutable extraction result of one raw message in one run (plan §4, §8.1, ADR-0005): exactly one row
/// per (raw_message_id, run_id) — the finalizer inserts it with ON CONFLICT DO NOTHING and never updates it. Rules, LLM
/// and structured results do not compete: whichever completes the state machine first is canonical; a later or fenced-
/// out result is a no-op. Facts are kept as the contract JSON of `observations.recorded`. `targets` stays the legacy
/// projection written by the old pipeline during the compatibility window.
/// </summary>
public class Extraction
{
    public Guid ExtractionId { get; set; }
    public long RawMessageId { get; set; }
    public Guid RunId { get; set; }
    /// <summary>Always 1 in a run: a re-extraction is a new run (ADR-0005), not a new version in place.</summary>
    public int ExtractionVersion { get; set; } = 1;
    /// <summary>rules | structured | llm | mixed | none</summary>
    public required string Method { get; set; }
    /// <summary>completed | no_facts | unsupported | needs_review | failed</summary>
    public required string Outcome { get; set; }
    public JsonDocument? Versions { get; set; }
    public required JsonDocument Facts { get; set; }
    public JsonDocument? Error { get; set; }
    public Guid[] LlmRequestIds { get; set; } = [];
    public required string FinalizedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One fact of an extraction (contract `$defs/observation`); the domain workers (P09/P10) read these, not `targets`.</summary>
public class Observation
{
    public Guid ObservationId { get; set; }
    public Guid ExtractionId { get; set; }
    public Extraction? Extraction { get; set; }
    public long RawMessageId { get; set; }
    public Guid RunId { get; set; }
    public required string EventKindCode { get; set; }
    /// <summary>target | alert | incident | info</summary>
    public required string Category { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public required JsonDocument Payload { get; set; }
    /// <summary>`targets.target_id` during the compatibility window; null until the cutover links them (P09/P14).</summary>
    public long? LegacyTargetId { get; set; }
}
