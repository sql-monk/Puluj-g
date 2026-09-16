using System.Text.Json.Nodes;
using Puluj.Domain.Enums;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Stages;
using Puluj.Processing.Tests.Support;
using Puluj.Processing.Text;
using Puluj.Processing.Writers;

namespace Puluj.Processing.Tests.Writers;

/// <summary>P09: a contract fact materializes back into the same legacy Target the fact was mapped from (the comparable subset: every column, not rule-only knowledge).</summary>
public class TargetMaterializerTests
{
    private static readonly string[] Texts =
    [
        "Шахеди на Сумщині курсом на Полтавщину.",
        "БпЛА на Полтавщині у південному напрямку.",
        "Вибухи у Харкові.",
        "Повітряна тривога в Харківській області.",
        "Відбій повітряної тривоги в Харківській області.",
        "Балістика!\nДніпро — в укриття!",
        "Шахеди:\nСумщина.\nЧернігівщина.",
        "Ймовірно 5 шахедів на півночі Київщини.",
    ];

    [Fact]
    public void Fact_to_target_to_fact_round_trips_every_column()
    {
        var indexes = new StaticIndexes();
        var parser = new RuleParser(indexes);
        var builder = new TargetBuilder(indexes);
        var normalizer = new Normalizer();
        var raw = new Puluj.Domain.Entities.RawMessage { RawMessageId = 4711, SourceId = 3, SourceMessageId = "x", SourceMessageKey = "x", SourceRevision = "0", Hash = "h", PublishedAt = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero), ReceivedAt = DateTimeOffset.UtcNow };
        var source = new Puluj.Domain.Entities.Source { SourceId = 3, Code = "s", Name = "s", TrustLevel = 0.9 };
        var checkedFacts = 0;
        foreach (var text in Texts)
        {
            var n = normalizer.Normalize(text);
            foreach (var parsed in parser.Parse(n, new ParseContext(3, n.Language, null)))
            {
                var original = builder.Build(parsed, raw, source, parsed.ParserVersion, parsed.Method, n.Language);
                var fact = FactMapper.ToFact(original, EventKindIndex.Empty, indexes.Gazetteer, parsed, n.Language, RuleParser.Version);
                fact["observation_id"] = Guid.CreateVersion7().ToString();

                var materialized = TargetMaterializer.FromFact(fact, raw.RawMessageId, raw.SourceId);
                Assert.Equal(fact["observation_id"]!.GetValue<string>(), materialized.ObservationId!.Value.ToString());
                Assert.Equal(original.ObservedAt, materialized.ObservedAt);
                // The contract carries coordinates rounded to 6 decimals (~0.1 m): the row keeps that precision, never less.
                Assert.Equal(original.Location is null, materialized.Location is null);
                if (original.Location is not null)
                {
                    Assert.Equal(original.Location.Coordinate.X, materialized.Location!.Coordinate.X, 1e-6);
                    Assert.Equal(original.Location.Coordinate.Y, materialized.Location.Coordinate.Y, 1e-6);
                }

                // The same mapper on the materialized row (no ParsedFact: rule-only keys are absent on both sides).
                var again = FactMapper.ToFact(materialized, EventKindIndex.Empty, indexes.Gazetteer, null, n.Language, RuleParser.Version);
                var expected = FactMapper.ToFact(original, EventKindIndex.Empty, indexes.Gazetteer, null, n.Language, RuleParser.Version);
                Assert.Equal(FactMapper.Canonical(expected), FactMapper.Canonical(again));
                checkedFacts++;
            }
        }
        Assert.True(checkedFacts >= 6, $"{checkedFacts} facts checked");
    }

    [Fact]
    public void Category_and_enum_fallbacks_are_safe_on_a_minimal_fact()
    {
        var fact = new JsonObject
        {
            ["event_kind_code"] = "fire.reported",
            ["category"] = "incident",
            ["effective_at"] = "2026-09-15T10:00:00Z",
            ["location"] = new JsonObject { ["kind"] = "city", ["place_id"] = 12 },
            ["confidence"] = "medium",
            ["attributes"] = new JsonObject { ["legacy_event_type"] = "Unknown" },
        };
        var t = TargetMaterializer.FromFact(fact, 1, 2);
        Assert.Equal("incident", TargetMaterializer.Category(fact));
        Assert.Equal(EventType.Unknown, t.EventType);
        Assert.Equal(LocationKind.City, t.LocationKind);
        Assert.Equal(12, t.LocationPlaceId);
        Assert.Equal(ConfidenceLevel.Medium, t.Confidence);
        Assert.Null(t.Location);
        Assert.Null(t.ObservationId);
        Assert.Equal(IdentificationMethod.Rule, t.IdentificationMethod);
    }
}
