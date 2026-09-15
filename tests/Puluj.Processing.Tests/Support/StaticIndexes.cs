using Puluj.Processing.Indexes;

namespace Puluj.Processing.Tests.Support;

public sealed class StaticIndexes : IIndexes
{
    public TaxonomyIndex Taxonomy => TestIndexes.Taxonomy;
    public GazetteerIndex Gazetteer => TestIndexes.Gazetteer;
    public EventKindIndex EventKinds { get; init; } = EventKindIndex.Empty;
    public Puluj.Processing.Rules.RulesetIndex Rules { get; init; } = Puluj.Processing.Rules.RulesetIndex.Builtin;
    public Puluj.Processing.Rules.RulesetIndex? ShadowRules { get; init; }
}
