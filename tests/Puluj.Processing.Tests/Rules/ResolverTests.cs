using System.Text.Json;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Rules;
using Puluj.Infrastructure.Seeding;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Rules;
using Puluj.Processing.Tests.Support;
using Puluj.Processing.Text;

namespace Puluj.Processing.Tests.Rules;

/// <summary>Resolver semantics (tie-break, veto, scope), the new-kind path through RuleParser and TargetBuilder, validator and evaluator.</summary>
public class ResolverTests
{
    private static readonly Normalizer Normalizer = new();

    private static RuleDefinition Rule(string code, string kind, int priority, string[] stems, string[]? negative = null, string lang = "*", string[]? sources = null, bool enabled = true, int window = 5) =>
        new(code, kind, lang, [new PatternDefinition("stems", stems, window)], negative is null ? null : [new PatternDefinition("stems", negative, window)], priority, sources, null, null, null, enabled);

    private static Segment Seg(string text) => Normalizer.Normalize(text).Segments[0];

    private static Resolution? Resolve(RulesetIndex set, string text, bool hasTarget = false, string lang = "uk", string? source = null) =>
        EventKindResolver.Resolve(set, Seg(text), hasTarget, false, lang, source);

    [Fact]
    public void Higher_priority_wins_then_earliest_match_then_rule_code()
    {
        var set = RulesetIndex.FromDefinitions(9, "draft",
        [
            Rule("b.late", "fire.reported", 10, ["пожеж"]),
            Rule("a.early", "impact.explosion.reported", 10, ["вибух"]),
            Rule("z.top", "infrastructure.outage", 20, ["світл"]),
        ]);
        Assert.Equal("z.top", Resolve(set, "Вибух, пожежа і немає світла.")!.RuleCode); // priority
        Assert.Equal("b.late", Resolve(set, "Пожежа після вибуху.")!.RuleCode); // same priority: the earliest match wins
        Assert.Equal("a.early", Resolve(set, "Вибух і пожежа.")!.RuleCode);
        var tie = RulesetIndex.FromDefinitions(9, "draft", [Rule("k.two", "fire.reported", 10, ["вибух"]), Rule("k.one", "impact.explosion.reported", 10, ["вибух"])]);
        Assert.Equal("k.one", Resolve(tie, "Вибух.")!.RuleCode); // same priority, same position → rule code
    }

    [Fact]
    public void Negative_pattern_vetoes_only_that_rule()
    {
        var set = RulesetIndex.FromDefinitions(9, "draft",
        [
            Rule("fire", "fire.reported", 20, ["пожеж"], negative: ["пожежн", "небезпек"]),
            Rule("info", "civil_defence.notice", 10, ["небезпек"]),
        ]);
        Assert.Equal("fire", Resolve(set, "Пожежа на складі.")!.RuleCode);
        Assert.Equal("info", Resolve(set, "Оголошено пожежну небезпеку.")!.RuleCode);
    }

    [Fact]
    public void Language_source_scope_disabled_rule_and_disabled_kind_filter()
    {
        var set = RulesetIndex.FromDefinitions(9, "draft",
        [
            Rule("ru.only", "impact.explosion.reported", 30, ["взрыв"], lang: "ru"),
            Rule("scoped", "fire.reported", 20, ["пожеж"], sources: ["tg-a"]),
            Rule("off", "infrastructure.outage", 10, ["світл"], enabled: false),
            Rule("kind.off", "evacuation.notice", 10, ["евакуац"]),
        ], kindEnabled: code => code != "evacuation.notice");
        Assert.Null(Resolve(set, "Взрыв в городе.", lang: "uk"));
        Assert.Equal("ru.only", Resolve(set, "Взрыв в городе.", lang: "ru")!.RuleCode);
        Assert.Null(Resolve(set, "Пожежа.", source: null));
        Assert.Null(Resolve(set, "Пожежа.", source: "tg-b"));
        Assert.Equal("scoped", Resolve(set, "Пожежа.", source: "tg-a")!.RuleCode);
        Assert.Null(Resolve(set, "Немає світла."));
        Assert.Null(Resolve(set, "Евакуація району."));
    }

    [Fact]
    public void Header_hint_skips_the_rule_but_lower_rules_still_apply()
    {
        var hint = JsonDocument.Parse("""{"header_if_target_without_level": true}""").RootElement;
        var set = RulesetIndex.FromDefinitions(9, "draft",
        [
            new RuleDefinition("alert", "alert.air_raid.started", "*", [new PatternDefinition("stems", ["тривог"])], null, 20, null, hint),
            Rule("ppo", "air_defence.activity", 10, ["ппо"]),
        ]);
        var seg = Seg("Тривога, шахеди на Київ, ППО працює.");
        Assert.Equal("ppo", EventKindResolver.Resolve(set, seg, hasTarget: true, hasLevel: false, "uk", null)!.RuleCode);
        Assert.Equal("alert", EventKindResolver.Resolve(set, seg, hasTarget: false, hasLevel: false, "uk", null)!.RuleCode);
        Assert.Equal("alert", EventKindResolver.Resolve(set, seg, hasTarget: true, hasLevel: true, "uk", null)!.RuleCode);
    }

    [Fact]
    public void Window_bounds_the_distance_between_first_and_last_stem()
    {
        var set = RulesetIndex.FromDefinitions(9, "draft", [Rule("w", "infrastructure.outage", 10, ["відключен", "світл"], window: 2)]);
        Assert.NotNull(Resolve(set, "Відключення світла у місті."));
        Assert.Null(Resolve(set, "Відключення у місті на годину світла."));
        Assert.Equal((0, 1), (Resolve(set, "Відключення світла у місті.")!.TokenStart, Resolve(set, "Відключення світла у місті.")!.TokenEnd));
    }

    // ---- the new-kind path: RuleParser → TargetBuilder without an enum member ----

    private static RulesetIndex V2()
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/taxonomy/event-rules-v2-draft.json"));
        var draft = JsonSerializer.Deserialize<RulesetFile>(stream, SeedFiles.Json)!;
        return RulesetIndex.FromDefinitions(2, "draft", RulesetParityTests.V1.Rules.Select(ToDefinition).Concat(draft.Rules));
    }

    private static RuleDefinition ToDefinition(CompiledRule r) =>
        new(r.Code, r.KindCode, r.Language, r.Positive.Select(p => new PatternDefinition("stems", p.Stems, p.Window)).ToList(),
            r.Negative.Select(p => new PatternDefinition("stems", p.Stems, p.Window)).ToList(), r.Priority, null,
            r.HeaderIfTargetWithoutLevel ? JsonDocument.Parse("""{"header_if_target_without_level": true}""").RootElement : null, null, r.RuleVersion, true);

    private static EventKindIndex Kinds()
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/taxonomy/event-kinds.json"));
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var id = 1;
        return new EventKindIndex(doc.RootElement.GetProperty("kinds").EnumerateArray()
            .Select(k => new EventKind { EventKindId = id++, Code = k.GetProperty("code").GetString()!, NameUk = "x", Category = Enum.Parse<EventKindCategory>(k.GetProperty("category").GetString()!, true) })
            .ToList());
    }

    [Fact]
    public void Kind_without_enum_member_survives_parser_and_builder()
    {
        var kinds = Kinds();
        var indexes = new StaticIndexes { Rules = V2(), EventKinds = kinds };
        var parser = new RuleParser(indexes);
        var n = Normalizer.Normalize("У Харкові після удару виникла пожежа на складі.");
        var result = parser.Parse(n, new ParseContext(1, n.Language, null), indexes.Rules);
        var fact = Assert.Single(result.Facts);
        Assert.Equal("fire.reported", fact.EventKindCode);
        Assert.Equal(EventType.Unknown, fact.EventType); // documented legacy fallback
        Assert.Equal("fire.pozhezh", fact.RuleCode);
        Assert.Equal("v2", fact.RulesetId);
        Assert.NotNull(fact.RuleSpan);
        Assert.Equal("Харків", fact.Current!.Place.Name);

        var raw = new RawMessage { SourceId = 1, SourceMessageId = "x", SourceMessageKey = "x", SourceRevision = "1", Hash = "h", RawText = n.Text, PublishedAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var source = new Source { SourceId = 1, Code = "s", Name = "s", TrustLevel = 0.9 };
        var target = new TargetBuilder(indexes).Build(fact, raw, source, fact.ParserVersion, fact.Method, n.Language);
        Assert.Equal(kinds.ByCode("fire.reported")!.EventKindId, target.EventKindId);
        Assert.Equal(EventType.Unknown, target.EventType);
        Assert.Equal("fire.pozhezh", target.ParserMetadata!.RootElement.GetProperty("ruleCode").GetString());
        Assert.Equal("v2", target.ParserMetadata!.RootElement.GetProperty("rulesetVersion").GetString());

    }

    [Fact]
    public void New_kind_line_under_a_target_header_is_not_an_inherited_sighting()
    {
        var indexes = new StaticIndexes { Rules = V2(), EventKinds = Kinds() };
        var parser = new RuleParser(indexes);
        var n = Normalizer.Normalize("Шахеди:\nПожежа у Броварах.\nБровари.");
        var facts = parser.Parse(n, new ParseContext(1, n.Language, null), indexes.Rules).Facts;
        Assert.Equal(2, facts.Count);
        Assert.Equal("fire.reported", facts[0].EventKindCode);
        Assert.Null(facts[0].Target);
        Assert.Equal(EventType.TargetObserved, facts[1].EventType); // the bare place line inherits the header target as before
        Assert.Equal("SHAHED", facts[1].Target!.Ref.Code);
    }

    [Fact]
    public void Target_with_new_kind_keeps_target_observed_in_the_legacy_column()
    {
        var indexes = new StaticIndexes { Rules = V2(), EventKinds = Kinds() };
        var n = Normalizer.Normalize("Над Київщиною збито 5 шахедів.");
        var fact = Assert.Single(new RuleParser(indexes).Parse(n, new ParseContext(1, n.Language, null), indexes.Rules).Facts);
        Assert.Equal("air_defence.interception.reported", fact.EventKindCode);
        Assert.Equal(EventType.TargetObserved, fact.EventType);
        Assert.Equal("SHAHED", fact.Target!.Ref.Code);
        Assert.Equal(5, fact.Count);
    }

    [Fact]
    public void Snapshot_passed_to_parse_is_the_one_cited()
    {
        var indexes = new StaticIndexes { Rules = V2() };
        var n = Normalizer.Normalize("Вибухи у Дніпрі.");
        var result = new RuleParser(indexes).Parse(n, new ParseContext(1, n.Language, null), RulesetParityTests.V1);
        Assert.Same(RulesetParityTests.V1, result.Ruleset);
        Assert.Equal("v1", Assert.Single(result.Facts).RulesetId);
    }

    // ---- validator / evaluator ----

    [Fact]
    public void Validator_reports_each_error_class()
    {
        var kinds = new Dictionary<string, bool> { ["fire.reported"] = true, ["evacuation.notice"] = false, ["impact.explosion.reported"] = true };
        var report = RulesetValidator.Validate(
        [
            Rule("dup", "fire.reported", 10, ["пожеж"]),
            Rule("dup", "fire.reported", 10, ["горить"]),
            Rule("bad kind", "nope.kind", 10, ["x y"]),
            new RuleDefinition("lang", "fire.reported", "de", [new PatternDefinition("regex", ["a"])], null, 5),
            new RuleDefinition("empty", "fire.reported", "*", [], null, 5, []),
            Rule("nfc", "fire.reported", 5, ["ё"]),
            Rule("upper", "fire.reported", 5, ["Пожеж"]),
            Rule("cancel", "fire.reported", 5, ["вогонь"], negative: ["вогонь"]),
            Rule("tie.a", "fire.reported", 7, ["дим"]),
            Rule("tie.b", "impact.explosion.reported", 7, ["дим"]),
            Rule("disabled.kind", "evacuation.notice", 3, ["евакуац"]),
            new RuleDefinition("conf", "fire.reported", "*", [new PatternDefinition("stems", ["полум"])], null, 4, null, null, 0.5m),
            Rule("win", "fire.reported", 2, ["пожеж", "склад"], window: 99),
        ], kinds, new HashSet<string> { "tg-a" });
        Assert.False(report.Ok);
        var codes = report.Errors.Select(e => e.Code).ToHashSet();
        foreach (var expected in new[] { "duplicate_rule_code", "rule_code", "unknown_kind", "stem_whitespace", "unknown_language", "unsupported_pattern_type", "no_positive_patterns", "empty_source_scope", "stem_not_nfc", "stem_upper", "negation_cancels_positive", "conflicting_rules", "window_range" })
        {
            Assert.Contains(expected, codes);
        }
        var warnings = report.Warnings.Select(w => w.Code).ToHashSet();
        Assert.Contains("kind_disabled", warnings);
        Assert.Contains("confidence_modifier_unused", warnings);
        Assert.Contains("priority_tie", warnings);
    }

    [Fact]
    public void Validator_accepts_v1_and_v2_draft()
    {
        var kinds = Kinds();
        var enabled = Enumerable.Range(0, 0).ToDictionary(i => i.ToString(), i => true);
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/taxonomy/event-kinds.json"));
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var k in doc.RootElement.GetProperty("kinds").EnumerateArray())
        {
            enabled[k.GetProperty("code").GetString()!] = true;
        }
        var v1 = RulesetValidator.Validate(RulesetParityTests.V1.Rules.Select(ToDefinition).ToList(), enabled, null);
        Assert.True(v1.Ok, string.Join("; ", v1.Errors.Select(e => e.Message)));
        var v2 = RulesetValidator.Validate(V2().Rules.Select(ToDefinition).ToList(), enabled, null);
        Assert.True(v2.Ok, string.Join("; ", v2.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Evaluator_scores_v1_and_v2_on_the_kind_corpus()
    {
        using var stream = File.OpenRead(Path.Combine(TestIndexes.RepoRoot, "data/corpus/kinds.json"));
        var corpus = JsonSerializer.Deserialize<KindCorpusFile>(stream, SeedFiles.Json)!;
        var indexes = new StaticIndexes { EventKinds = Kinds() };
        var evaluator = new RulesetEvaluator(new RuleParser(indexes), Normalizer);

        var v1 = evaluator.Evaluate(RulesetParityTests.V1, corpus.Cases);
        Assert.Equal("v1", v1.RulesetId);
        Assert.All(corpus.Cases.Where(c => c.Id.StartsWith("legacy-")), c => Assert.DoesNotContain(v1.Mismatches, m => m.CaseId == c.Id));
        Assert.Contains(v1.ByKind, k => k.Kind == "fire.reported" && k.Recall == 0); // v1 knows no fires

        var v2 = evaluator.Evaluate(V2(), corpus.Cases);
        var unexpected = v2.Mismatches.Where(m => !m.Ambiguous).ToList();
        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected.Select(m => $"{m.CaseId}[{m.Segment}]: expected {m.Expected}, got {m.Actual} ({m.Rule})")));
        foreach (var kind in new[] { "fire.reported", "infrastructure.outage", "air_defence.interception.reported" })
        {
            var m = Assert.Single(v2.ByKind, k => k.Kind == kind);
            Assert.True(m.Recall >= 0.99, $"{kind} recall {m.Recall}");
        }
        // Quality evidence (plan §8.3 gate: agreed precision/recall on the labelled sample) when a directory is given.
        if (Environment.GetEnvironmentVariable("PULUJ_EVIDENCE_DIRECTORY") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            File.WriteAllText(Path.Combine(dir, "P08-quality-report.json"), JsonSerializer.Serialize(new
            {
                generatedAt = DateTimeOffset.UtcNow,
                corpus = "data/corpus/kinds.json",
                cases = corpus.Cases.Count,
                rulesets = new[] { Summarize(v1), Summarize(v2) },
            }, options));
        }
    }

    private static object Summarize(EvaluationReport r) => new
    {
        ruleset = r.RulesetId,
        r.Segments,
        r.Correct,
        accuracy = Math.Round(r.Accuracy, 3),
        byKind = r.ByKind.Select(k => new { k.Kind, tp = k.TruePositives, fp = k.FalsePositives, fn = k.FalseNegatives, precision = Math.Round(k.Precision, 3), recall = Math.Round(k.Recall, 3), f1 = Math.Round(k.F1, 3) }),
        mismatches = r.Mismatches.Select(m => new { m.CaseId, m.Segment, m.Expected, m.Actual, m.Rule, m.Ambiguous }),
    };
}
