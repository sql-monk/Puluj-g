using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;
using Puluj.Processing.Structured;
using Puluj.Processing.Text;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>Pure pieces of the parser stage without a database: the fact mapper is deterministic and complete, the structured adapter never guesses a place.</summary>
public sealed class StageMapperTests
{
    private sealed class EmptyIndexes : IIndexes
    {
        public TaxonomyIndex Taxonomy => TaxonomyIndex.Empty;
        public GazetteerIndex Gazetteer => GazetteerIndex.Empty;
        public EventKindIndex EventKinds => EventKindIndex.Empty;
        public Puluj.Processing.Rules.RulesetIndex Rules => Puluj.Processing.Rules.RulesetIndex.Builtin;
        public Puluj.Processing.Rules.RulesetIndex? ShadowRules => null;
    }

    private static Target SampleTarget() => new()
    {
        RawMessageId = 4711,
        SourceId = 3,
        SegmentIndex = 1,
        SegmentText = "Шахеди на Сумщині",
        ObservedAt = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero),
        EventType = EventType.TargetObserved,
        IdentificationMethod = IdentificationMethod.Rule,
        IdentificationSource = "шахеди",
        ParserVersion = "rule-0.1",
        TargetCategoryId = 1,
        TargetClassId = 3,
        ObjectCount = 2,
        ObjectCountIsApproximate = true,
        Confidence = ConfidenceLevel.Medium,
        ClassificationConfidence = ConfidenceLevel.Medium,
        LocationKind = LocationKind.Region,
        LocationPlaceId = 8,
        LocationAccuracyKm = 147.3644,
        DirectionKind = DirectionKind.TowardsPlace,
        DirectionDeg = 231.456,
        DirectionConfidence = ConfidenceLevel.Low,
        DestinationPlaceId = 24,
        ParserMetadata = JsonDocument.Parse("{\"rules\":[\"target\",\"place\"],\"z\":1,\"a\":2}"),
    };

    [Fact]
    public void Fact_mapping_is_deterministic_and_keeps_every_legacy_field()
    {
        var a = FactMapper.ToFact(SampleTarget(), EventKindIndex.Empty, GazetteerIndex.Empty, null, "uk", "rule-0.1");
        var b = FactMapper.ToFact(SampleTarget(), EventKindIndex.Empty, GazetteerIndex.Empty, null, "uk", "rule-0.1");
        Assert.Equal(FactMapper.Canonical(a), FactMapper.Canonical(b));
        Assert.Equal("target.observed", a["event_kind_code"]!.GetValue<string>());
        Assert.Equal("target", a["category"]!.GetValue<string>()); // fallback by legacy enum when the catalog is empty
        Assert.Equal("2026-09-15T10:00:00.0000000Z", a["effective_at"]!.GetValue<string>());
        Assert.Equal("region", a["location"]!["kind"]!.GetValue<string>());
        Assert.Equal(8, a["location"]!["place_id"]!.GetValue<int>());
        Assert.Equal(147.364, a["location"]!["accuracy_km"]!.GetValue<double>());
        Assert.Null(a["location"]!["text"]); // no gazetteer, no metadata title: no invented name
        Assert.Equal("medium", a["confidence"]!.GetValue<string>());
        Assert.Equal(1, a["evidence"]!["segment_index"]!.GetValue<int>());
        Assert.Equal("rule-0.1", a["evidence"]!["rule_version"]!.GetValue<string>());
        var attributes = a["attributes"]!.AsObject();
        foreach (var key in new[] { "legacy_event_type", "identification_method", "identification_source", "parser_version", "segment_index", "segment_text", "language", "alert_level",
                     "target_category_id", "target_class_id", "target_family_id", "target_model_id", "model_confidence", "classification_confidence", "object_count",
                     "object_count_is_approximate", "location_kind", "location_place_id", "location_accuracy_km", "origin_place_id", "destination_place_id", "direction_kind",
                     "direction_deg", "direction_confidence", "confidence", "event_kind_id", "parser_metadata" })
        {
            Assert.True(attributes.ContainsKey(key), key);
        }
        Assert.Equal(231.46, attributes["direction_deg"]!.GetValue<double>());
        Assert.True(attributes["object_count_is_approximate"]!.GetValue<bool>());
        Assert.Null(attributes["target_family_id"]);
        Assert.False(attributes.ContainsKey("hedged")); // rule-only knowledge: absent when there is no parsed fact
        // Canonical form sorts keys at every level (jsonb reorders them on the way back from the database).
        Assert.Equal("{\"a\":2,\"rules\":[\"target\",\"place\"],\"z\":1}", FactMapper.Canonical(attributes["parser_metadata"]));
    }

    [Fact]
    public void Structured_adapter_keeps_the_title_when_the_place_is_unknown_and_writes_nothing()
    {
        var handler = new AlertsInUaHandler(new EmptyIndexes(), new Normalizer(), NullLogger<AlertsInUaHandler>.Instance);
        var adapter = new AlertsInUaStructuredAdapter(handler);
        var raw = new RawMessage
        {
            RawMessageId = 5,
            SourceId = 7,
            SourceMessageId = "31:end",
            SourceMessageKey = "31:end",
            SourceRevision = "0",
            PublishedAt = new DateTimeOffset(2026, 9, 15, 10, 41, 0, TimeSpan.Zero),
            ReceivedAt = new DateTimeOffset(2026, 9, 15, 10, 41, 0, TimeSpan.Zero),
            Hash = "h",
            RawPayload = JsonDocument.Parse("{\"kind\":\"alert.finished\",\"at\":\"2026-09-15T10:41:00Z\",\"alert\":{\"id\":31,\"location_title\":\"Невідома громада\",\"location_type\":\"hromada\",\"alert_type\":\"air_raid\",\"started_at\":\"2026-09-15T09:58:00Z\",\"finished_at\":\"2026-09-15T10:41:00Z\"}}"),
        };
        Assert.True(AlertsInUaStructuredAdapter.CanHandle(raw));
        Assert.Equal("alerts_in_ua.alert.finished", AlertsInUaStructuredAdapter.StructuredKind(raw));
        var target = adapter.Extract(raw, new Source { SourceId = 7, Code = "alerts_in_ua", Name = "alerts" });
        Assert.Equal(EventType.AlertCancelled, target.EventType);
        Assert.Equal(LocationKind.Unknown, target.LocationKind);
        Assert.Null(target.LocationPlaceId);
        Assert.Equal(0, target.TargetId); // never saved
        var fact = FactMapper.ToFact(target, EventKindIndex.Empty, GazetteerIndex.Empty, null, null, AlertsInUaStructuredAdapter.Version);
        Assert.Equal("alert.air_raid.ended", fact["event_kind_code"]!.GetValue<string>());
        Assert.Equal("alert", fact["category"]!.GetValue<string>());
        Assert.Equal("unknown", fact["location"]!["kind"]!.GetValue<string>());
        Assert.Equal("Невідома громада", fact["location"]!["text"]!.GetValue<string>()); // the feed's name survives for the alert worker
        Assert.Equal("alert", fact["evidence"]!["structured_field"]!.GetValue<string>());
        Assert.Equal("2026-09-15T09:58:00.0000000Z", fact["attributes"]!["parser_metadata"]!["startedAt"]!.GetValue<string>());
        Assert.False(fact["attributes"]!["parser_metadata"]!["placeResolved"]!.GetValue<bool>());
    }
}
