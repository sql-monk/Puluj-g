using System.Text.Json;

namespace Puluj.Domain.Entities;

/// <summary>
/// Plan §8.3 (P08). One immutable version of the event-kind rule set. Exactly one version is <see cref="IsActive"/>
/// for new jobs; <c>draft</c> is the only state whose rules may still change; <c>shadow</c> runs beside the active
/// set on live traffic without touching results; rollback activates an older published version (a new audit entry,
/// never a rewrite of stored results).
/// </summary>
public class EventKindRuleset
{
    public const string Draft = "draft";
    public const string Shadow = "shadow";
    public const string Published = "published";
    public const string Superseded = "superseded";

    /// <summary>Monotonic version number; the identity of the set (evidence cites <c>v{Version}</c>).</summary>
    public int Version { get; set; }
    public string State { get; set; } = Draft;
    public bool IsActive { get; set; }
    public int? ParentVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public JsonDocument? Notes { get; set; }
    public List<EventKindRule> Rules { get; set; } = [];
}

/// <summary>
/// One rule of one rule-set version: ordered stem patterns that must all appear (in order, within a token window) in a
/// segment, negative patterns that veto the rule, and the kind it names. Rows of a non-draft version are never updated:
/// every change is a new draft version. <see cref="RuleCode"/> is the stable key of the rule across versions;
/// <see cref="RuleVersion"/> grows when the rule's behaviour changes.
/// </summary>
public class EventKindRule
{
    public long RuleId { get; set; }
    public int RulesetVersion { get; set; }
    public required string RuleCode { get; set; }
    public int EventKindId { get; set; }
    /// <summary><c>uk</c>, <c>ru</c>, <c>en</c> or <c>*</c> (any). Language comes from the normalizer's heuristic, so <c>*</c> is the safe choice.</summary>
    public string Language { get; set; } = "*";
    /// <summary><c>{"sources": ["code", ...]}</c>; null = every source.</summary>
    public JsonDocument? SourceScope { get; set; }
    /// <summary>Array of <c>{"type":"stems","stems":[...],"window":5}</c>; the rule fires when any pattern matches.</summary>
    public required JsonDocument PositivePatterns { get; set; }
    /// <summary>Same shape; any match vetoes the rule on that segment.</summary>
    public JsonDocument? NegativePatterns { get; set; }
    public int Priority { get; set; }
    /// <summary><c>{"header_if_target_without_level": true}</c> — the rule is skipped when the segment names a target and no alert level (a header, not a fact).</summary>
    public JsonDocument? ExtractionHints { get; set; }
    /// <summary>Stored for future use; not applied in P08 (v1 = 0).</summary>
    public decimal ConfidenceModifier { get; set; }
    public int RuleVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public required string Actor { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class EventKindRulesetAudit
{
    public const string Created = "created";
    public const string RulesReplaced = "rules_replaced";
    public const string Validated = "validated";
    public const string ShadowStarted = "shadow_started";
    public const string ShadowStopped = "shadow_stopped";
    public const string PublishedAction = "published";
    public const string RolledBack = "rolled_back";

    public long AuditId { get; set; }
    public int Version { get; set; }
    public required string Action { get; set; }
    public required string Actor { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset At { get; set; }
    public JsonDocument? Details { get; set; }
}

/// <summary>One segment where the shadow rule set disagreed with the live one (kind or rule); only disagreements are stored.</summary>
public class EventKindRuleShadow
{
    public long ShadowId { get; set; }
    public long RawMessageId { get; set; }
    public Guid RunId { get; set; }
    public int LiveVersion { get; set; }
    public int ShadowVersion { get; set; }
    public int SegmentIndex { get; set; }
    public string? LiveKind { get; set; }
    public string? ShadowKind { get; set; }
    public string? LiveRule { get; set; }
    public string? ShadowRule { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
