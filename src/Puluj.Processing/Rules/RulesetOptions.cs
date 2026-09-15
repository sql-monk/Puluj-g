namespace Puluj.Processing.Rules;

/// <summary>Plan §8.3 (P08): which rule-set version the parser pins and whether the shadow comparison runs.</summary>
public sealed class RulesetOptions
{
    public const string Section = "Parsing";

    /// <summary>Canary override: pin this version instead of the active one. Unknown or unpublished → warning, the active set is used (unless <see cref="RulesetPinAllowDraft"/>).</summary>
    public int? RulesetPin { get; set; }
    /// <summary>Allow the pin to name a draft/shadow version (canary of an unpublished set).</summary>
    public bool RulesetPinAllowDraft { get; set; }
    /// <summary>Run the shadow rule set (state `shadow`) beside the live one in the parser stage and record disagreements.</summary>
    public bool ShadowEnabled { get; set; } = true;
    /// <summary>Bound on shadow disagreement rows per shadow version per hour; beyond it only the counters in stage outputs are kept.</summary>
    public int ShadowMaxRowsPerHour { get; set; } = 5000;
    /// <summary>How often the active/shadow version pointers are re-read (publish/rollback propagation), independent of the 10-minute index refresh.</summary>
    public int RulesetPollSeconds { get; set; } = 30;
}
