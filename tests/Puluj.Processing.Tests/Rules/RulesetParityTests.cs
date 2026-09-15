using System.Text.Json;
using Puluj.Domain;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Rules;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing.Parsing;
using Puluj.Processing.Rules;
using Puluj.Processing.Tests.Support;
using Puluj.Processing.Text;

namespace Puluj.Processing.Tests.Rules;

/// <summary>
/// P08 parity gate: rule-set v1 (`data/taxonomy/event-rules.json`) resolved by <see cref="EventKindResolver"/> must behave
/// exactly like the frozen <see cref="EventTypeMatcher"/> — on the golden corpus (whole facts, field by field), on
/// hand-picked competing segments and on 10 000 random token sequences built from the phrase vocabulary.
/// </summary>
public class RulesetParityTests
{
    public static RulesetIndex V1 { get; } = LoadV1();

    private static RulesetIndex LoadV1()
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/taxonomy/event-rules.json"));
        var file = JsonSerializer.Deserialize<RulesetFile>(stream, SeedFiles.Json)!;
        return RulesetIndex.FromDefinitions(1, "published", file.Rules);
    }

    private static readonly Lazy<List<string>> CorpusTexts = new(() =>
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/corpus/cases.json"));
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("text").GetString()!).ToList();
    });

    [Fact]
    public void Seed_v1_carries_every_matcher_phrase_in_order_with_distinct_priorities()
    {
        Assert.Equal(24, V1.Rules.Count);
        Assert.Equal(24, V1.Rules.Select(r => r.Priority).Distinct().Count());
        Assert.All(V1.Rules, r => Assert.Equal("*", r.Language));
        Assert.All(V1.Rules, r => Assert.Null(r.Sources));
        Assert.Equal("event:відбій_тривог", V1.Rules[0].Code); // first phrase = highest priority
        Assert.Equal("event:влучанн", V1.Rules[^1].Code);
        Assert.All(V1.Rules.Where(r => r.KindCode == "alert.air_raid.started"), r => Assert.True(r.HeaderIfTargetWithoutLevel));
        Assert.All(V1.Rules.Where(r => r.KindCode != "alert.air_raid.started"), r => Assert.False(r.HeaderIfTargetWithoutLevel));
    }

    [Fact]
    public void Corpus_facts_are_identical_under_builtin_and_v1()
    {
        var builtin = new RuleParser(new StaticIndexes());
        var v1 = new RuleParser(new StaticIndexes { Rules = V1 });
        var normalizer = new Normalizer();
        foreach (var text in CorpusTexts.Value.Concat(Competing))
        {
            var n = normalizer.Normalize(text);
            var ctx = new ParseContext(1, n.Language, null);
            var a = builtin.Parse(n, ctx);
            var b = v1.Parse(n, ctx, V1).Facts;
            Assert.True(a.Count == b.Count, $"'{text}': {a.Count} vs {b.Count} facts");
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(Describe(a[i]), Describe(b[i]));
                Assert.Equal("v1", b[i].RulesetId);
                Assert.Equal(EventKindLegacyMap.ToCode(a[i].EventType), b[i].EventKindCode ?? EventKindLegacyMap.ToCode(b[i].EventType));
            }
        }
    }

    /// <summary>Segments where phrase order decides: "відбій загрози" (AlertCancelled by phrase 4 before TargetCancelled by phrase 7), header alerts, multiple phrases.</summary>
    private static readonly string[] Competing =
    [
        "Відбій загрози для Київщини.",
        "Тривога! Шахеди на Київ, ППО працює.",
        "Повітряна тривога, жовтий рівень: дронова загроза.",
        "Тривога у Сумах.",
        "Вибухи у Харкові, працює ППО, збито дрон.",
        "Загрозу знято, чисто.",
        "Отбой тревоги в области.",
        "Загроза балістики минула, відбій.",
        "БпЛА на Чернігівщині, курсом на Київщину, пуск ракет з Курська.",
        "Шахеди:",
        "Дніпро — в укриття!",
    ];

    [Fact]
    public void Random_token_sequences_resolve_identically()
    {
        var rng = new Random(20260916);
        string[] vocabulary =
        [
            "відбій", "тривог", "тривога", "тривоги", "повітрян", "повітряна", "отбой", "тревог", "тревоги", "загроз", "загроза", "загрозу", "минул", "минула", "знято",
            "відсутн", "відсутня", "не", "фіксу", "фіксується", "чисто", "оголошен", "оголошено", "воздушн", "воздушная", "робот", "робота", "працю", "працює", "ппо", "сил",
            "силами", "збит", "збито", "вибух", "вибухи", "взрыв", "взрывы", "прильот", "приліт", "прильоти", "влучанн", "влучання", "пуск", "запуск", "зліт", "злет", "взлет",
            "шахед", "шахеди", "ракета", "київ", "у", "на", "в", "область", "область", "район", "рівень", "жовтий", "червоний", "дрон", "курсом", "москв", "тривогааааа", "чистота",
            "відбійний", "збитий", "загрозливий", "непередбачувано",
        ];
        var normalizer = new Normalizer();
        var mismatches = new List<string>();
        for (var n = 0; n < 10_000; n++)
        {
            var length = rng.Next(1, 12);
            var words = Enumerable.Range(0, length).Select(_ => vocabulary[rng.Next(vocabulary.Length)]);
            var text = string.Join(' ', words) + (rng.Next(4) == 0 ? ":" : ".");
            var normalized = normalizer.Normalize(text);
            if (normalized.Segments.Count == 0)
            {
                continue;
            }
            var segment = normalized.Segments[0];
            foreach (var hasTarget in new[] { false, true })
            {
                var hasLevel = AlertLevelExtractor.Extract(segment) != AirAlertLevel.Unknown;
                var (type, rule, launch) = EventTypeMatcher.Match(segment, hasTarget);
                var r = EventKindResolver.Resolve(V1, segment, hasTarget, hasLevel, normalized.Language, null);
                var expectedType = rule is null ? (hasTarget ? EventType.TargetObserved : EventType.Unknown) : type;
                var actualType = r?.Legacy ?? (hasTarget ? EventType.TargetObserved : EventType.Unknown);
                if (expectedType != actualType || rule != r?.RuleCode || launch != LaunchDetector.Detect(segment.Tokens))
                {
                    mismatches.Add($"'{text}' target={hasTarget}: matcher {type}/{rule}/{launch} vs v1 {actualType}/{r?.RuleCode}/{LaunchDetector.Detect(segment.Tokens)}");
                }
            }
        }
        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches.Take(20)));
    }

    private static string Describe(ParsedFact f) =>
        $"{f.EventType}|{f.Target?.Ref.Code}|{f.Target?.Hedged}|{f.Count}|{f.CountIsApproximate}|{f.IsLaunch}|{f.AlertLevel}|{string.Join(",", f.Rules)}|" +
        $"{string.Join(",", f.Places.Select(p => p.Place.PlaceId + ":" + p.Role))}|{f.Direction?.Degrees}|{f.SegmentIndex}";
}
