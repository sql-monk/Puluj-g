namespace Puluj.Processing.Indexes;

/// <summary>Current taxonomy/gazetteer snapshots. Implemented by IndexProvider (database-backed) and by test fixtures.</summary>
public interface IIndexes
{
    TaxonomyIndex Taxonomy { get; }
    GazetteerIndex Gazetteer { get; }
    /// <summary>Plan §8.2 event catalog snapshot; take it once per message so a refresh cannot change kinds mid-message.</summary>
    EventKindIndex EventKinds { get; }
    /// <summary>Plan §8.3 rule-set snapshot pinned per message (P08); <see cref="Rules.RulesetIndex.Builtin"/> before the catalog is seeded.</summary>
    Rules.RulesetIndex Rules { get; }
}
