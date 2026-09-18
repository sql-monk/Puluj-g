namespace Puluj.Processing.Rules;

/// <summary>Which rule-set version the parser pins.</summary>
public sealed class RulesetOptions
{
    public const string Section = "Parsing";

    /// <summary>Canary override: pin this version instead of the active one. Unknown or unpublished → warning, the active set is used (unless <see cref="RulesetPinAllowDraft"/>).</summary>
    public int? RulesetPin { get; set; }
    /// <summary>Allow the pin to name a draft version for a canary run.</summary>
    public bool RulesetPinAllowDraft { get; set; }
    /// <summary>How often the active version pointer is re-read (publish/rollback propagation), independent of the 10-minute index refresh.</summary>
    public int RulesetPollSeconds { get; set; } = 30;
}
