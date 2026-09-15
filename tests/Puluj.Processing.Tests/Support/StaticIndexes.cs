using Puluj.Processing.Indexes;

namespace Puluj.Processing.Tests.Support;

public sealed class StaticIndexes : IIndexes
{
    public TaxonomyIndex Taxonomy => TestIndexes.Taxonomy;
    public GazetteerIndex Gazetteer => TestIndexes.Gazetteer;
    public EventKindIndex EventKinds => EventKindIndex.Empty;
}
