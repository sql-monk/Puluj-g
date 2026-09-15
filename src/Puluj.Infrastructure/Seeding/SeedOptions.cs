namespace Puluj.Infrastructure.Seeding;

public sealed class SeedOptions
{
    public const string Section = "Seed";
    /// <summary>Directory containing taxonomy/, sources.json, gazetteer/. Relative paths resolve against the content root.</summary>
    public string DataDirectory { get; set; } = "data";
    public bool SeedTaxonomy { get; set; } = true;
    public bool SeedSources { get; set; } = true;
    public bool SeedGazetteer { get; set; } = true;
    public bool SeedEventKinds { get; set; } = true;
    /// <summary>Plan §8.3 bootstrap of the rule catalog (v1 from taxonomy/event-rules.json) when no version exists yet.</summary>
    public bool SeedEventKindRules { get; set; } = true;
    /// <summary>Plan §8.2 backfill of targets.event_kind_id after seeding; idempotent, batched, safe to leave on.</summary>
    public bool BackfillEventKinds { get; set; } = true;
}
