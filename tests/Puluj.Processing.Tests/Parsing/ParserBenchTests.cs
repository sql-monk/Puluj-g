using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using InfraDi = Puluj.Infrastructure.DependencyInjection;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Text;
using Xunit;
using Xunit.Abstractions;

namespace Puluj.Processing.Tests.Parsing;

/// <summary>
/// Parser throughput on real messages from the local database (skipped when there is none): the rule parser must
/// stay in the low milliseconds per message, or a history rebuild takes days.
/// </summary>
public sealed class ParserBenchTests(ITestOutputHelper output)
{
    private const string EnvVar = "PULUJ_BENCH_CONNECTION";

    private sealed class DbIndexes(TaxonomyIndex taxonomy, GazetteerIndex gazetteer) : IIndexes
    {
        public TaxonomyIndex Taxonomy => taxonomy;
        public GazetteerIndex Gazetteer => gazetteer;
        public EventKindIndex EventKinds => EventKindIndex.Empty;
        public Puluj.Processing.Rules.RulesetIndex Rules => Puluj.Processing.Rules.RulesetIndex.Builtin;
    }

    [Fact]
    public async Task Rule_parser_on_real_messages()
    {
        var connection = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(connection))
        {
            return; // no database in this run
        }
        var options = new DbContextOptionsBuilder<PulujDbContext>();
        InfraDi.ConfigureDbContext(options, connection);
        await using var db = new PulujDbContext(options.Options);
        var sw = Stopwatch.StartNew();
        var taxonomy = await IndexProvider.LoadTaxonomyAsync(db, CancellationToken.None);
        var gazetteer = await IndexProvider.LoadGazetteerAsync(db, CancellationToken.None);
        output.WriteLine($"indexes loaded in {sw.ElapsedMilliseconds} ms ({gazetteer.Count} places)");
        // PULUJ_BENCH_IDS="1,2,3" measures exactly those messages instead of the newest 300.
        var ids = (Environment.GetEnvironmentVariable("PULUJ_BENCH_IDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
        var texts = await db.RawMessages.AsNoTracking()
            .Where(r => r.RawText != null && r.RawText.Length > 20 && (ids.Length == 0 || ids.Contains(r.RawMessageId)))
            .OrderByDescending(r => r.RawMessageId).Take(300)
            .Select(r => new { r.RawMessageId, r.RawText, r.SourceId })
            .ToListAsync();
        var parser = new RuleParser(new DbIndexes(taxonomy, gazetteer));
        var normalizer = new Normalizer();
        var ctx = new ParseContext(texts[0].SourceId, "uk", null);
        // Warm-up, then the measured pass.
        foreach (var t in texts.Take(20))
        {
            parser.Parse(normalizer.Normalize(t.RawText!), ctx);
        }
        var timings = new List<(long Id, long Normalize, long Parse, int Facts, int Length)>();
        foreach (var t in texts)
        {
            var s1 = Stopwatch.StartNew();
            var n = normalizer.Normalize(t.RawText!);
            var normalizeMs = s1.ElapsedMilliseconds;
            var facts = parser.Parse(n, new ParseContext(t.SourceId, n.Language, null));
            timings.Add((t.RawMessageId, normalizeMs, s1.ElapsedMilliseconds - normalizeMs, facts.Count, t.RawText!.Length));
        }
        output.WriteLine($"messages={timings.Count} normalize avg={timings.Average(x => x.Normalize):F1} ms parse avg={timings.Average(x => x.Parse):F1} ms max={timings.Max(x => x.Parse)} ms");
        foreach (var t in timings.OrderByDescending(x => x.Parse).Take(5))
        {
            output.WriteLine($"  slow: #{t.Id} {t.Parse} ms, {t.Length} chars, {t.Facts} facts");
        }
    }
}
