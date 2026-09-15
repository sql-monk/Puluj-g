using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Indexes;
using Puluj.Processing.Llm;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Processing.Stages;

/// <summary>
/// The `parser` subscription (plan §4): `message.normalized` → `parse.completed` (rules or the structured adapter) or,
/// when the rules found nothing in a text that reads like a target report, `parse.completed{needs_llm}` plus the
/// `llm.requested` command for the LLM worker (P06). Pure recognition: targets are built in memory and mapped to
/// contract facts, nothing is written to `targets` or `air_alerts` (the finalizer/fact writer and the alert worker own
/// those); the transaction only records the stage and the outgoing events. `RuleParser` only — never the LLM parser.
/// </summary>
public sealed class ParserHandler(
    IDbContextFactory<PulujDbContext> factory,
    INormalizer normalizer,
    RuleParser rules,
    TargetBuilder builder,
    AlertsInUaStructuredAdapter structured,
    IndexProvider indexes,
    IOptionsMonitor<LlmOptions> llm,
    IOptionsMonitor<Rules.RulesetOptions> rulesetOptions,
    TimeProvider clock) : IDeliveryHandler
{
    public const string Subscription = "parser";
    public const string Stage = "parse";
    public const string EventType = "parse.completed";
    public const string LlmEventType = "llm.requested";
    public const string SchemaVersion = "1.0";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Prepared(RawMessage Raw, Guid AttemptId, string Outcome, string Method, string StageVersion, JsonArray Facts, string? FallbackReason,
        JsonObject? Error, JsonObject Versions, string? Hash, long DurationMs, DateTimeOffset StartedAt, int LegacyTargets, Shadow? Shadow = null);

    /// <summary>Shadow comparison (P08): per-segment disagreements of the shadow rule set with the live one, computed outside the transaction.</summary>
    private sealed record Shadow(int LiveVersion, int ShadowVersion, int Segments, IReadOnlyList<ShadowRow> Disagreements, string? Error);

    private sealed record ShadowRow(int SegmentIndex, string? LiveKind, string? ShadowKind, string? LiveRule, string? ShadowRule);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", "message.normalized without payload");
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", "message.normalized without raw_message_id");
        var textKind = payload["text_kind"]?.GetValue<string>() ?? throw new PermanentDeliveryException("invalid_payload", "message.normalized without text_kind");
        var normalizationVersion = payload["normalization_version"]?.GetValue<string>();
        if (normalizationVersion != Normalizer.Version)
        {
            // A rolling deploy: the normalizer that produced this may be newer than this parser. Retry — another replica may match.
            throw new InvalidOperationException($"normalization_version {normalizationVersion} vs this build {Normalizer.Version}");
        }
        await indexes.Ready.WaitAsync(ct);
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct) ?? throw new InvalidOperationException($"raw_messages {rawId} not found");
        var source = raw.Source!;
        var attemptId = Guid.CreateVersion7();
        var kinds = indexes.EventKinds;
        var ruleset = indexes.Rules; // one snapshot per job (pinned); the same one is cited in versions and used for every segment
        var versions = new JsonObject
        {
            ["normalization"] = Normalizer.Version,
            ["rules"] = RuleParser.Version,
            ["ruleset_id"] = ruleset.Id,
            ["catalog_policy"] = kinds.PolicyVersion.ToString(),
        };

        switch (textKind)
        {
            case "structured":
            {
                var target = structured.Extract(raw, source);
                kinds.Stamp([target]);
                var facts = new JsonArray(FactMapper.ToFact(target, kinds, indexes.Gazetteer, null, null, AlertsInUaStructuredAdapter.Version));
                return new Prepared(raw, attemptId, "facts", "structured", AlertsInUaStructuredAdapter.Version, facts, null, null, versions, null, sw.ElapsedMilliseconds, startedAt, 1);
            }
            case "text":
            {
                var normalized = normalizer.Normalize(raw.RawText ?? "");
                var hash = StageSupport.Sha256(normalized.Text);
                var expected = payload["normalized_text_hash"]?.GetValue<string>();
                if (expected is not null && !string.Equals(expected, hash, StringComparison.Ordinal))
                {
                    // Same normalization version, different text: the raw row changed or the normalizer is not deterministic. Not retryable.
                    var error = new JsonObject { ["code"] = "normalization_drift", ["message"] = $"normalized text hash {hash} differs from message.normalized {expected}", ["retryable"] = false };
                    return new Prepared(raw, attemptId, "failed", "rules", RuleParser.Version, [], null, error, versions, hash, sw.ElapsedMilliseconds, startedAt, 0);
                }
                var ctx = new ParseContext(source.SourceId, normalized.Language, RawMessageProcessor.HomeRegionOf(source, normalizer, indexes), raw.PublishedAt, raw.RawMessageId, source.Code);
                var parsed = rules.Parse(normalized, ctx, ruleset).Facts;
                var targets = parsed.Select(f => (Fact: f, Target: builder.Build(f, raw, source, f.ParserVersion, f.Method, normalized.Language))).ToList();
                kinds.Stamp(targets.Select(t => t.Target));
                var facts = new JsonArray(targets.Select(t => (JsonNode)FactMapper.ToFact(t.Target, kinds, indexes.Gazetteer, t.Fact, normalized.Language, RuleParser.Version)).ToArray());
                var shadow = ShadowCompare(ruleset, normalized, ctx, parsed);
                if (facts.Count > 0)
                {
                    return new Prepared(raw, attemptId, "facts", "rules", RuleParser.Version, facts, null, null, versions, hash, sw.ElapsedMilliseconds, startedAt, targets.Count, shadow);
                }
                var (outcome, reason) = FallbackDecision(normalized, envelope.Lane, raw.PublishedAt);
                return new Prepared(raw, attemptId, outcome, "rules", RuleParser.Version, facts, reason, null, versions, hash, sw.ElapsedMilliseconds, startedAt, 0, shadow);
            }
            default:
                return new Prepared(raw, attemptId, "unsupported", "none", "empty", [], "no_text_no_payload", null, versions, null, sw.ElapsedMilliseconds, startedAt, 0);
        }
    }

    /// <summary>
    /// Runs the shadow rule set (state `shadow`) over the same segments and keeps only the segments where the kind or the
    /// rule differs. Never throws: an error is reported in the stage outputs and the live result is unaffected.
    /// </summary>
    private Shadow? ShadowCompare(Rules.RulesetIndex live, NormalizedMessage normalized, ParseContext ctx, IReadOnlyList<ParsedFact> liveFacts)
    {
        var shadowSet = indexes.ShadowRules;
        if (shadowSet is null || !rulesetOptions.CurrentValue.ShadowEnabled || live.Version is not int liveVersion || shadowSet.Version is not int shadowVersion)
        {
            return null;
        }
        try
        {
            var shadowFacts = rules.Parse(normalized, ctx, shadowSet).Facts;
            var liveBySegment = liveFacts.GroupBy(f => f.SegmentIndex).ToDictionary(g => g.Key, g => g.First());
            var shadowBySegment = shadowFacts.GroupBy(f => f.SegmentIndex).ToDictionary(g => g.Key, g => g.First());
            var rows = new List<ShadowRow>();
            foreach (var segment in normalized.Segments)
            {
                var l = liveBySegment.GetValueOrDefault(segment.Index);
                var s = shadowBySegment.GetValueOrDefault(segment.Index);
                var liveKind = l is null ? null : l.EventKindCode ?? Puluj.Domain.EventKindLegacyMap.ToCode(l.EventType);
                var shadowKind = s is null ? null : s.EventKindCode ?? Puluj.Domain.EventKindLegacyMap.ToCode(s.EventType);
                if (liveKind != shadowKind || l?.RuleCode != s?.RuleCode)
                {
                    rows.Add(new ShadowRow(segment.Index, liveKind, shadowKind, l?.RuleCode, s?.RuleCode));
                }
            }
            return new Shadow(liveVersion, shadowVersion, normalized.Segments.Count, rows, null);
        }
        catch (Exception ex)
        {
            return new Shadow(liveVersion, shadowVersion, normalized.Segments.Count, [], ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task RecordShadowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, Guid runId, Shadow shadow, CancellationToken ct)
    {
        try
        {
            await using (var sp = new NpgsqlCommand("SAVEPOINT shadow", conn, tx))
            {
                await sp.ExecuteNonQueryAsync(ct);
            }
            var cap = rulesetOptions.CurrentValue.ShadowMaxRowsPerHour;
            await using (var count = new NpgsqlCommand("SELECT count(*) FROM event_kind_rule_shadow WHERE shadow_version = @v AND created_at > now() - interval '1 hour'", conn, tx))
            {
                count.Parameters.AddWithValue("v", shadow.ShadowVersion);
                if ((long)(await count.ExecuteScalarAsync(ct))! >= cap)
                {
                    return; // bounded: the counters in the stage outputs remain
                }
            }
            foreach (var row in shadow.Disagreements)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO event_kind_rule_shadow (raw_message_id, run_id, live_version, shadow_version, segment_index, live_kind, shadow_kind, live_rule, shadow_rule, created_at)
                    VALUES (@raw, @run, @live, @shadow, @segment, @lk, @sk, @lr, @sr, now())
                    """, conn, tx);
                insert.Parameters.AddWithValue("raw", rawId);
                insert.Parameters.AddWithValue("run", runId);
                insert.Parameters.AddWithValue("live", shadow.LiveVersion);
                insert.Parameters.AddWithValue("shadow", shadow.ShadowVersion);
                insert.Parameters.AddWithValue("segment", row.SegmentIndex);
                insert.Parameters.AddWithValue("lk", (object?)row.LiveKind ?? DBNull.Value);
                insert.Parameters.AddWithValue("sk", (object?)row.ShadowKind ?? DBNull.Value);
                insert.Parameters.AddWithValue("lr", (object?)row.LiveRule ?? DBNull.Value);
                insert.Parameters.AddWithValue("sr", (object?)row.ShadowRule ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using var release = new NpgsqlCommand("RELEASE SAVEPOINT shadow", conn, tx);
            await release.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT shadow", conn, tx);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The rules found nothing. `needs_llm` only when the text reads like a target report, the model is enabled, the lane
    /// is live and the post is fresh (history and replay stay with the rules, as the legacy pipeline does); otherwise
    /// `no_facts` with the reason the fallback was skipped, so the parse statistics can tell the cases apart.
    /// </summary>
    private (string Outcome, string? Reason) FallbackDecision(NormalizedMessage normalized, string lane, DateTimeOffset publishedAt)
    {
        if (!LlmParser.LooksLikeTargetReport(normalized))
        {
            return ("no_facts", null);
        }
        var o = llm.CurrentValue;
        if (!o.Enabled)
        {
            return ("no_facts", "llm_disabled");
        }
        if (lane != "live")
        {
            return ("no_facts", "llm_skipped_lane");
        }
        if (o.MaxMessageAgeHours > 0 && clock.GetUtcNow() - publishedAt > TimeSpan.FromHours(o.MaxMessageAgeHours))
        {
            return ("no_facts", "llm_skipped_stale");
        }
        return ("needs_llm", "target_report_without_rule_match");
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var p = (Prepared)state!;
        var completedId = Guid.CreateVersion7();
        var llmRequestId = p.Outcome == "needs_llm" ? Guid.CreateVersion7() : (Guid?)null;
        var outputs = new JsonObject
        {
            ["attempt_id"] = p.AttemptId.ToString(),
            ["outcome"] = p.Outcome,
            ["method"] = p.Method,
            ["facts_count"] = p.Facts.Count,
            ["legacy_targets"] = p.LegacyTargets,
            ["fallback_reason"] = p.FallbackReason,
            ["duration_ms"] = p.DurationMs,
            ["caused_by"] = envelope.EventId.ToString(),
            ["parse_completed_event_id"] = completedId.ToString(),
            ["llm_request_id"] = llmRequestId?.ToString(),
        };
        if (p.Shadow is { } shadowInfo)
        {
            outputs["shadow"] = new JsonObject
            {
                ["live_version"] = shadowInfo.LiveVersion,
                ["shadow_version"] = shadowInfo.ShadowVersion,
                ["segments"] = shadowInfo.Segments,
                ["disagreements"] = shadowInfo.Disagreements.Count,
                ["error"] = shadowInfo.Error,
            };
        }
        var stageId = await StageSupport.InsertStageResultAsync(conn, tx, p.Raw.RawMessageId, envelope.ProcessingRunId, Stage, p.StageVersion, p.Outcome, outputs, p.Versions, p.StartedAt, Producer, ct);
        if (stageId is null)
        {
            return DeliveryResult.Noop("stage already recorded for this raw/run");
        }
        if (p.Shadow is { Disagreements.Count: > 0 } shadowRows)
        {
            // Shadow rows never endanger the live result (P08 review B3): a savepoint isolates their insert, and the cap
            // bounds a draft that disagrees on everything; only the first recording of the stage writes them (stageId above).
            await RecordShadowAsync(conn, tx, p.Raw.RawMessageId, envelope.ProcessingRunId, shadowRows, ct);
        }

        var payload = new JsonObject
        {
            ["raw_message_id"] = p.Raw.RawMessageId,
            ["attempt_id"] = p.AttemptId.ToString(),
            ["outcome"] = p.Outcome,
            ["method"] = p.Method,
            ["versions"] = p.Versions.DeepClone(),
            ["facts"] = p.Facts.DeepClone(),
            ["duration_ms"] = p.DurationMs,
        };
        if (p.FallbackReason is not null)
        {
            payload["fallback_reason"] = p.FallbackReason;
        }
        if (p.Error is not null)
        {
            payload["error"] = p.Error.DeepClone();
        }
        var completed = StageSupport.Child(envelope, EventType, SchemaVersion, Producer, clock.GetUtcNow(), payload);
        completed.EventId = completedId;
        var outgoing = new List<Envelope> { completed };

        if (llmRequestId is Guid requestId)
        {
            var o = llm.CurrentValue;
            var request = StageSupport.Child(envelope, LlmEventType, SchemaVersion, Producer, clock.GetUtcNow(), new JsonObject
            {
                ["raw_message_id"] = p.Raw.RawMessageId,
                ["request_id"] = requestId.ToString(),
                ["model"] = o.Model,
                ["prompt_version"] = o.PromptVersion,
                ["fencing_token"] = 1,
                ["deadline_at"] = FactMapper.Iso(clock.GetUtcNow().AddSeconds(Math.Max(30, o.TimeoutSeconds * 3))),
                ["budget"] = new JsonObject { ["max_output_tokens"] = 1024 },
                ["input"] = new JsonObject { ["normalized_text_hash"] = p.Hash, ["normalization_version"] = Normalizer.Version },
                ["rules_context"] = new JsonObject { ["outcome"] = "needs_llm", ["reason"] = p.FallbackReason, ["attempt_id"] = p.AttemptId.ToString() },
            });
            request.EventId = requestId;
            outgoing.Add(request);
        }
        return new DeliveryResult("completed", null, outgoing) { StageResultId = stageId };
    }
}
