using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Tests.Support;
using Puluj.Processing.Text;

namespace Puluj.Processing.Tests.Pipeline;

/// <summary>The bearing drawn on the map must follow the reported route, never the centre of an area that already contains the target.</summary>
public class TargetBuilderTests
{
    private static Target BuildFirst(string text) => BuildAll(text)[0];

    private static List<Target> BuildAll(string text)
    {
        var indexes = new StaticIndexes();
        var normalized = new Normalizer().Normalize(text);
        var facts = new RuleParser(indexes).Parse(normalized, new ParseContext(1, normalized.Language, null));
        Assert.NotEmpty(facts);
        var raw = new RawMessage { SourceId = 1, SourceMessageId = "t", SourceMessageKey = "t", SourceRevision = "0", PublishedAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow, RawText = text, Hash = "h" };
        var source = new Source { SourceId = 1, Code = "test", Name = "test", Type = SourceType.Telegram, TrustLevel = 0.9 };
        var builder = new TargetBuilder(indexes);
        return facts.Select(f => builder.Build(f, raw, source, "test", IdentificationMethod.Rule, normalized.Language)).ToList();
    }

    [Fact]
    public void Levelled_raion_alert_is_an_alert_over_the_raion_not_a_sighting()
    {
        var all = BuildAll("🟡 Броварський район — повітряна тривога, жовтий рівень: Дронова загроза (жовтий рівень)");
        var o = all[0];
        Assert.Equal(EventType.AirRaidAlert, o.EventType);
        Assert.Equal(AirAlertLevel.Yellow, o.AlertLevel);
        Assert.Equal(LocationKind.District, o.LocationKind);
        Assert.True(o.LocationAccuracyKm >= 25, $"accuracy {o.LocationAccuracyKm}");
        // The named cause must not become a separate "drone seen" fact.
        Assert.DoesNotContain(all, x => x.EventType == EventType.TargetObserved);
    }

    [Fact]
    public void Origin_region_to_city_gives_bearing_from_the_marker_along_the_route()
    {
        var o = BuildFirst("Реактивний БпЛА з Чернігівщини на Київ.");
        Assert.Equal(DirectionKind.TowardsPlace, o.DirectionKind);
        Assert.Equal(ConfidenceLevel.Low, o.DirectionConfidence); // from the centre of an oblast: a hint only
        // The marker sits at the Chernihiv oblast centre; Kyiv is south-west of it.
        Assert.InRange(o.DirectionDeg!.Value, 195, 250);
    }

    [Fact]
    public void Transit_area_containing_the_destination_points_the_vector_at_the_destination()
    {
        // Located in Kyiv oblast (transit), heading for Kyiv: the vector must point from the marker to the city.
        var o = BuildFirst("Реактивний БпЛА з Чернігівщини через Київське водосховище на Київ.");
        Assert.Equal(DirectionKind.TowardsPlace, o.DirectionKind);
        Assert.Equal(ConfidenceLevel.Low, o.DirectionConfidence);
        Assert.InRange(o.DirectionDeg!.Value, 5, 40);
    }

    [Fact]
    public void Destination_inside_the_current_region_is_only_a_low_confidence_hint()
    {
        var o = BuildFirst("БпЛА на Київщині курсом на Київ.");
        Assert.Equal(DirectionKind.TowardsPlace, o.DirectionKind);
        Assert.Equal(ConfidenceLevel.Low, o.DirectionConfidence);
        Assert.NotNull(o.DestinationPlaceId);
    }

    [Fact]
    public void Precise_current_position_to_destination_gives_bearing()
    {
        var o = BuildFirst("Герань-2 у Броварському районі, курс на Київ.");
        Assert.Equal(DirectionKind.TowardsPlace, o.DirectionKind);
        Assert.Equal(ConfidenceLevel.Medium, o.DirectionConfidence);
        // Brovary -> Kyiv is west-south-west.
        Assert.InRange(o.DirectionDeg!.Value, 230, 275);
    }
}

public class KyivDistrictTests
{
    private static IReadOnlyList<ParsedFact> Parse(string text, int? homeRegion = null)
    {
        var normalized = new Normalizer().Normalize(text);
        return new RuleParser(new StaticIndexes()).Parse(normalized, new ParseContext(1, normalized.Language, homeRegion));
    }

    [Fact]
    public void District_is_matched_when_the_message_names_kyiv()
    {
        var facts = Parse("Київ: БпЛА над Оболонським районом.");
        var place = Assert.Single(facts).Current;
        Assert.Equal("Оболонський район", place!.Place.Name);
    }

    [Fact]
    public void District_is_matched_when_the_source_is_about_kyiv()
    {
        var facts = Parse("🟡 Дніпровський район — повітряна тривога, жовтий рівень", homeRegion: 25);
        var place = Assert.Single(facts).Current;
        Assert.Equal("Дніпровський район", place!.Place.Name);
    }

    [Fact]
    public void Ambiguous_district_name_without_kyiv_context_is_not_a_kyiv_district()
    {
        // "Дніпровський район" also exists in Dnipropetrovsk and Kherson oblasts; without context it must not land in Kyiv.
        var facts = Parse("БпЛА у Дніпровському районі.");
        Assert.DoesNotContain(facts, f => f.Places.Any(p => p.Place.ParentId == 25));
    }

    [Fact]
    public void Neighbourhood_name_resolves_to_its_district()
    {
        var facts = Parse("Київ: вибухи на Троєщині.");
        var place = Assert.Single(facts).Current;
        Assert.Equal("Деснянський район", place!.Place.Name);
    }
}

public class UnitWordTests
{
    [Fact]
    public void Raion_word_is_not_a_settlement()
    {
        // GeoNames has a village "Ray" (stem "рай"); "район" must never resolve to it.
        var normalized = new Normalizer().Normalize("🟢 Бучанський район — відбій повітряної тривоги");
        var facts = new RuleParser(new StaticIndexes()).Parse(normalized, new ParseContext(1, normalized.Language, null));
        var fact = Assert.Single(facts);
        Assert.Equal(EventType.AlertCancelled, fact.EventType);
        Assert.All(fact.Places, p => Assert.NotEqual("Ray", p.Place.Name));
        Assert.Equal("Буча", fact.Current?.Place.Name);
    }
}
