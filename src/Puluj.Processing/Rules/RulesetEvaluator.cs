using Puluj.Domain;
using Puluj.Processing.Parsing;
using Puluj.Processing.Text;

namespace Puluj.Processing.Rules;

/// <summary>One golden case of `data/corpus/kinds.json`: the kind expected per segment (null = no kind).</summary>
public sealed record KindCase(string Id, string Text, List<ExpectedSegment> Expected, bool? Ambiguous = null);

public sealed record ExpectedSegment(int Segment, string? Kind);

public sealed record KindCorpusFile(List<KindCase> Cases);

public sealed record KindMetrics(string Kind, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed record Mismatch(string CaseId, int Segment, string? Expected, string? Actual, string? Rule, bool Ambiguous);

public sealed record EvaluationReport(string RulesetId, int Cases, int Segments, int Correct, IReadOnlyList<KindMetrics> ByKind, IReadOnlyList<Mismatch> Mismatches)
{
    public double Accuracy => Segments == 0 ? 1 : (double)Correct / Segments;
}

/// <summary>What a rule set says about a segment, for corpus evaluation and preview: the catalog kind (a plain target sighting counts as target.observed) and the rule.</summary>
public sealed record SegmentVerdict(int Segment, string? Kind, string? Rule, string? Target);

/// <summary>
/// Plan §8.3 quality gate (P08): runs the parser with a given rule-set version over the golden kind corpus and reports
/// per-kind precision/recall/F1 plus every mismatch. Never touches the database or the live rule set; the report is
/// evidence for the reviewer, not a switch.
/// </summary>
public sealed class RulesetEvaluator(RuleParser parser, INormalizer normalizer)
{
    public EvaluationReport Evaluate(RulesetIndex ruleset, IReadOnlyList<KindCase> cases, string? sourceCode = null)
    {
        var tp = new Dictionary<string, int>(StringComparer.Ordinal);
        var fp = new Dictionary<string, int>(StringComparer.Ordinal);
        var fn = new Dictionary<string, int>(StringComparer.Ordinal);
        var mismatches = new List<Mismatch>();
        var segments = 0;
        var correct = 0;
        foreach (var c in cases)
        {
            var verdicts = Verdicts(ruleset, c.Text, sourceCode).ToDictionary(v => v.Segment);
            foreach (var e in c.Expected)
            {
                segments++;
                var actual = verdicts.GetValueOrDefault(e.Segment);
                if (actual?.Kind == e.Kind)
                {
                    correct++;
                    if (e.Kind is not null)
                    {
                        tp[e.Kind] = tp.GetValueOrDefault(e.Kind) + 1;
                    }
                    continue;
                }
                if (e.Kind is not null)
                {
                    fn[e.Kind] = fn.GetValueOrDefault(e.Kind) + 1;
                }
                if (actual?.Kind is { } a)
                {
                    fp[a] = fp.GetValueOrDefault(a) + 1;
                }
                mismatches.Add(new Mismatch(c.Id, e.Segment, e.Kind, actual?.Kind, actual?.Rule, c.Ambiguous ?? false));
            }
        }
        var kinds = tp.Keys.Concat(fp.Keys).Concat(fn.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new KindMetrics(k, tp.GetValueOrDefault(k), fp.GetValueOrDefault(k), fn.GetValueOrDefault(k))).ToList();
        return new EvaluationReport(ruleset.Id, cases.Count, segments, correct, kinds, mismatches);
    }

    /// <summary>Per-segment verdicts of one text under one rule set (the first fact of a segment decides, as the corpus cases do).</summary>
    public IReadOnlyList<SegmentVerdict> Verdicts(RulesetIndex ruleset, string text, string? sourceCode = null)
    {
        var normalized = normalizer.Normalize(text);
        var facts = parser.Parse(normalized, new ParseContext(0, normalized.Language, null, null, null, sourceCode), ruleset).Facts;
        return facts.GroupBy(f => f.SegmentIndex).OrderBy(g => g.Key)
            .Select(g => g.First())
            .Select(f => new SegmentVerdict(f.SegmentIndex, f.EventKindCode ?? EventKindLegacyMap.ToCode(f.EventType), f.RuleCode, f.Target?.Ref.Code))
            .ToList();
    }
}

/// <summary>Per-segment diff of a candidate rule set against a baseline over given texts (authoring preview, P08).</summary>
public sealed record PreviewSegment(int Segment, string Text, SegmentVerdict? Baseline, SegmentVerdict? Candidate)
{
    public bool Changed => Baseline?.Kind != Candidate?.Kind || Baseline?.Rule != Candidate?.Rule;
}

public sealed record PreviewText(string Text, IReadOnlyList<PreviewSegment> Segments);

public sealed class RulesetPreview(RulesetEvaluator evaluator, INormalizer normalizer)
{
    public IReadOnlyList<PreviewText> Compare(RulesetIndex baseline, RulesetIndex candidate, IEnumerable<string> texts, string? sourceCode = null)
    {
        var result = new List<PreviewText>();
        foreach (var text in texts)
        {
            var normalized = normalizer.Normalize(text);
            var b = evaluator.Verdicts(baseline, text, sourceCode).ToDictionary(v => v.Segment);
            var c = evaluator.Verdicts(candidate, text, sourceCode).ToDictionary(v => v.Segment);
            result.Add(new PreviewText(text, normalized.Segments.Select(s => new PreviewSegment(s.Index, s.Text, b.GetValueOrDefault(s.Index), c.GetValueOrDefault(s.Index))).ToList()));
        }
        return result;
    }
}
