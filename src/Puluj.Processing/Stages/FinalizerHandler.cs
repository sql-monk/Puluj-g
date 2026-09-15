using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;

namespace Puluj.Processing.Stages;

/// <summary>
/// The `finalizer` subscription (plan §4, ADR-0005): turns `parse.completed`, `llm.completed` and `llm.failed` into exactly
/// one canonical, immutable extraction per (raw message, run) — `processing.extractions` + `processing.observations` —
/// and always publishes `message.analysis.completed` (also for no_facts/unsupported/needs_review/failed) plus
/// `observations.recorded` when there are facts. State machine per (raw, run): a rules/structured result finalizes at once;
/// `needs_llm` records `awaiting_llm` (stage `finalize`) and waits for `llm.completed` / a final `llm.failed`; a late LLM
/// result (fencing token below the current lease token, ADR-0004 W8) or any event after the extraction exists is a
/// `noop` receipt. Queues have no mutual order: whichever terminal input arrives first wins the unique insert.
/// `targets` are not written here — the legacy loop keeps that projection during the compatibility window (§8.1).
/// </summary>
public sealed class FinalizerHandler(IDbContextFactory<PulujDbContext> factory, TimeProvider clock, ILogger<FinalizerHandler> logger) : IDeliveryHandler
{
    public const string Subscription = "finalizer";
    public const string Stage = "finalize";
    public const string StageVersion = "finalizer-1";
    public const string ObservationsEventType = "observations.recorded";
    public const string AnalysisEventType = "message.analysis.completed";
    public const string SchemaVersion = "1.0";

    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Timings(DateTimeOffset? ReceivedAt, DateTimeOffset? StoredAt, DateTimeOffset? NormalizedAt, DateTimeOffset? ParsedAt);

    private sealed record Prepared(long RawMessageId, string Action, string? LateReason, string Method, string Outcome, JsonArray Facts, JsonObject Versions,
        JsonObject? Error, Guid? LlmRequestId, Timings Timings, DateTimeOffset StartedAt);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var payload = envelope.Payload ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without payload");
        var rawId = envelope.RawMessageId ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without raw_message_id");

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var timings = await TimingsAsync(conn, rawId, envelope.ProcessingRunId, ct);
        var versions = payload["versions"]?.DeepClone()?.AsObject() ?? new JsonObject();

        switch (envelope.EventType)
        {
            case ParserHandler.EventType:
            {
                var outcome = payload["outcome"]?.GetValue<string>() ?? throw new PermanentDeliveryException("invalid_payload", "parse.completed without outcome");
                var method = payload["method"]?.GetValue<string>() ?? "rules";
                var facts = payload["facts"]?.DeepClone()?.AsArray() ?? [];
                return outcome switch
                {
                    "needs_llm" => new Prepared(rawId, "await", null, method, "awaiting_llm", [], versions, null, null, timings, startedAt),
                    "facts" => new Prepared(rawId, "finalize", null, method, "completed", facts, versions, null, null, timings, startedAt),
                    "no_facts" => new Prepared(rawId, "finalize", null, method, "no_facts", [], versions, null, null, timings, startedAt),
                    "unsupported" => new Prepared(rawId, "finalize", null, "none", "unsupported", [], versions, null, null, timings, startedAt),
                    "needs_review" => new Prepared(rawId, "finalize", null, method, "needs_review", facts, versions, null, null, timings, startedAt),
                    _ => new Prepared(rawId, "finalize", null, method, "failed", [], versions, payload["error"]?.DeepClone()?.AsObject() ?? Error("parse_failed", "parser reported failure"), null, timings, startedAt),
                };
            }
            case LlmWorkerHandler.CompletedEventType:
            case LlmWorkerHandler.FailedEventType:
            {
                var requestId = Guid.TryParse(payload["request_id"]?.GetValue<string>(), out var rid) ? rid : throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without request_id");
                var token = payload["fencing_token"]?.GetValue<int>() ?? 0;
                var current = await CurrentTokenAsync(conn, LlmWorkerHandler.JobKey(requestId), ct);
                if (token < current)
                {
                    return new Prepared(rawId, "late", $"fencing token {token} < current {current}", "llm", "late", [], versions, null, requestId, timings, startedAt);
                }
                if (payload["model"]?.GetValue<string>() is { } model)
                {
                    versions["model"] = model;
                }
                if (payload["prompt_version"]?.GetValue<string>() is { } prompt)
                {
                    versions["prompt"] = prompt;
                }
                if (envelope.EventType == LlmWorkerHandler.FailedEventType)
                {
                    var final = payload["final"]?.GetValue<bool>() ?? true;
                    return final
                        ? new Prepared(rawId, "finalize", null, "llm", "failed", [], versions, payload["error"]?.DeepClone()?.AsObject() ?? Error("llm_failed", "llm worker gave up"), requestId, timings, startedAt)
                        : new Prepared(rawId, "ignore", "non-final llm.failed: still awaiting", "llm", "awaiting_llm", [], versions, null, requestId, timings, startedAt);
                }
                var llmOutcome = payload["outcome"]?.GetValue<string>() ?? "needs_review";
                var llmFacts = payload["facts"]?.DeepClone()?.AsArray() ?? [];
                var outcome = llmOutcome switch { "facts" => "completed", "no_facts" => "no_facts", _ => "needs_review" };
                return new Prepared(rawId, "finalize", null, "llm", outcome, llmFacts, versions, null, requestId, timings, startedAt);
            }
            default:
                throw new PermanentDeliveryException("unknown_event", $"finalizer does not handle {envelope.EventType}");
        }
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        var p = (Prepared)state!;
        if (p.Action is "late" or "ignore")
        {
            return DeliveryResult.Noop(p.LateReason ?? p.Action);
        }
        // Everything after the canonical extraction exists is a repeat (a second parse.completed, a late LLM answer, a replayed event).
        if (await ExtractionExistsAsync(conn, tx, p.RawMessageId, envelope.ProcessingRunId, ct))
        {
            return DeliveryResult.Noop("extraction already recorded for this raw/run");
        }
        if (p.Action == "await")
        {
            var awaiting = await UpsertStageAsync(conn, tx, p.RawMessageId, envelope.ProcessingRunId, "awaiting_llm", new JsonObject { ["caused_by"] = envelope.EventId.ToString() }, p.Versions, p.StartedAt, ct);
            return new DeliveryResult("completed", "awaiting llm") { StageResultId = awaiting };
        }

        var extractionId = Guid.CreateVersion7();
        var facts = new JsonArray();
        foreach (var fact in p.Facts)
        {
            var o = fact!.DeepClone().AsObject();
            o["observation_id"] = Guid.CreateVersion7().ToString();
            facts.Add(o);
        }
        var inserted = await InsertExtractionAsync(conn, tx, extractionId, p.RawMessageId, envelope.ProcessingRunId, p, facts, ct);
        if (!inserted)
        {
            return DeliveryResult.Noop("extraction already recorded for this raw/run (concurrent finalizer)");
        }
        foreach (var fact in facts)
        {
            await InsertObservationAsync(conn, tx, extractionId, p.RawMessageId, envelope.ProcessingRunId, fact!.AsObject(), ct);
        }
        var stageId = await UpsertStageAsync(conn, tx, p.RawMessageId, envelope.ProcessingRunId, p.Outcome,
            new JsonObject { ["extraction_result_id"] = extractionId.ToString(), ["method"] = p.Method, ["fact_count"] = facts.Count, ["caused_by"] = envelope.EventId.ToString(), ["llm_request_id"] = p.LlmRequestId?.ToString() },
            p.Versions, p.StartedAt, ct);

        var branches = ExpectedBranches(facts);
        var outgoing = new List<Envelope>();
        var finalizedAt = clock.GetUtcNow();
        if (p.Outcome == "completed" && facts.Count > 0)
        {
            outgoing.Add(StageSupport.Child(envelope, ObservationsEventType, SchemaVersion, Producer, finalizedAt, new JsonObject
            {
                ["raw_message_id"] = p.RawMessageId,
                ["extraction_result_id"] = extractionId.ToString(),
                ["extraction_version"] = 1,
                ["method"] = p.Method,
                ["versions"] = p.Versions.DeepClone(),
                ["observations"] = facts.DeepClone(),
                ["expected_branches"] = new JsonArray(branches.Select(b => (JsonNode)b).ToArray()),
            }));
        }
        var analysis = new JsonObject
        {
            ["raw_message_id"] = p.RawMessageId,
            ["extraction_result_id"] = extractionId.ToString(),
            ["outcome"] = p.Outcome,
            ["method"] = p.Method,
            ["fact_count"] = facts.Count,
            ["expected_branches"] = new JsonArray((p.Outcome == "completed" ? branches : []).Select(b => (JsonNode)b).ToArray()),
            ["versions"] = p.Versions.DeepClone(),
            ["timings"] = new JsonObject
            {
                ["received_at"] = p.Timings.ReceivedAt is { } r ? FactMapper.Iso(r) : null,
                ["stored_at"] = p.Timings.StoredAt is { } s ? FactMapper.Iso(s) : null,
                ["normalized_at"] = p.Timings.NormalizedAt is { } n ? FactMapper.Iso(n) : null,
                ["parsed_at"] = p.Timings.ParsedAt is { } pa ? FactMapper.Iso(pa) : null,
                ["finalized_at"] = FactMapper.Iso(finalizedAt),
            },
        };
        if (p.Error is not null)
        {
            analysis["error"] = p.Error.DeepClone();
        }
        if (p.LlmRequestId is { } requestId)
        {
            analysis["llm_request_ids"] = new JsonArray(requestId.ToString());
        }
        outgoing.Add(StageSupport.Child(envelope, AnalysisEventType, SchemaVersion, Producer, finalizedAt, analysis));
        logger.LogInformation("Raw {Raw} run {Run}: extraction {Extraction} {Outcome} ({Method}, {Facts} fact(s)) from {Event}", p.RawMessageId, envelope.ProcessingRunId, extractionId, p.Outcome, p.Method, facts.Count, envelope.EventType);
        return new DeliveryResult("completed", null, outgoing) { StageResultId = stageId };
    }

    /// <summary>Domain branches of the completion manifest by observation category (info has no owner); order of first appearance.</summary>
    public static IReadOnlyList<string> ExpectedBranches(JsonArray facts)
    {
        var branches = new List<string>();
        foreach (var fact in facts)
        {
            var branch = fact?["category"]?.GetValue<string>() switch
            {
                "target" => "track-worker",
                "alert" => "alert-worker",
                "incident" => "incident-worker",
                _ => null,
            };
            if (branch is not null && !branches.Contains(branch))
            {
                branches.Add(branch);
            }
        }
        return branches;
    }

    private static JsonObject Error(string code, string message) => new() { ["code"] = code, ["message"] = message, ["retryable"] = false };

    // ---- SQL ----

    private static async Task<int> CurrentTokenAsync(NpgsqlConnection conn, string jobKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT coalesce(max(fencing_token), 0) FROM processing.attempts WHERE subscription_id = @s AND job_key = @k", conn);
        cmd.Parameters.AddWithValue("s", LlmWorkerHandler.JobSubscription);
        cmd.Parameters.AddWithValue("k", jobKey);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> ExtractionExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, Guid runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM processing.extractions WHERE raw_message_id = @raw AND run_id = @run", conn, tx);
        cmd.Parameters.AddWithValue("raw", rawId);
        cmd.Parameters.AddWithValue("run", runId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<bool> InsertExtractionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid extractionId, long rawId, Guid runId, Prepared p, JsonArray facts, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.extractions (extraction_id, raw_message_id, run_id, extraction_version, method, outcome, versions, facts, error, llm_request_ids, finalized_by, created_at)
            VALUES (@id, @raw, @run, 1, @method, @outcome, @versions, @facts, @error, @llm, @by, now())
            ON CONFLICT (raw_message_id, run_id) DO NOTHING
            RETURNING extraction_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", extractionId);
        cmd.Parameters.AddWithValue("raw", rawId);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("method", p.Method);
        cmd.Parameters.AddWithValue("outcome", p.Outcome);
        cmd.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = p.Versions.ToJsonString() });
        cmd.Parameters.Add(new NpgsqlParameter("facts", NpgsqlDbType.Jsonb) { Value = facts.ToJsonString() });
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Jsonb) { Value = (object?)p.Error?.ToJsonString() ?? DBNull.Value });
        cmd.Parameters.AddWithValue("llm", p.LlmRequestId is { } id ? new[] { id } : Array.Empty<Guid>());
        cmd.Parameters.AddWithValue("by", Producer);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task InsertObservationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid extractionId, long rawId, Guid runId, JsonObject fact, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.observations (observation_id, extraction_id, raw_message_id, run_id, event_kind_code, category, effective_at, payload)
            VALUES (@id, @extraction, @raw, @run, @kind, @category, @at, @payload)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.Parse(fact["observation_id"]!.GetValue<string>()));
        cmd.Parameters.AddWithValue("extraction", extractionId);
        cmd.Parameters.AddWithValue("raw", rawId);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("kind", fact["event_kind_code"]?.GetValue<string>() ?? "unknown");
        cmd.Parameters.AddWithValue("category", fact["category"]?.GetValue<string>() ?? "info");
        cmd.Parameters.AddWithValue("at", DateTimeOffset.TryParse(fact["effective_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : DateTimeOffset.UtcNow);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = fact.ToJsonString() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Stage `finalize`: inserted, or moved from `awaiting_llm`/`failed` to the new outcome; a terminal outcome is never downgraded.</summary>
    private async Task<long?> UpsertStageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawId, Guid runId, string outcome, JsonObject outputs, JsonObject versions, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.stage_results (raw_message_id, run_id, stage, stage_version, outcome, outputs, started_at, finished_at, worker, versions)
            VALUES (@raw, @run, @stage, @version, @outcome, @outputs, @started, now(), @worker, @versions)
            ON CONFLICT (raw_message_id, run_id, stage, stage_version) DO UPDATE
                SET outcome = EXCLUDED.outcome, outputs = EXCLUDED.outputs, finished_at = now(), worker = EXCLUDED.worker, versions = EXCLUDED.versions
                WHERE processing.stage_results.outcome IN ('awaiting_llm', 'failed') AND EXCLUDED.outcome <> 'awaiting_llm'
            RETURNING stage_result_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("raw", rawId);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("stage", Stage);
        cmd.Parameters.AddWithValue("version", StageVersion);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.Add(new NpgsqlParameter("outputs", NpgsqlDbType.Jsonb) { Value = outputs.ToJsonString() });
        cmd.Parameters.AddWithValue("started", startedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("worker", Producer);
        cmd.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = versions.ToJsonString() });
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    private static async Task<Timings> TimingsAsync(NpgsqlConnection conn, long rawId, Guid runId, CancellationToken ct)
    {
        DateTimeOffset? received = null, stored = null, normalized = null, parsed = null;
        await using (var raw = new NpgsqlCommand("SELECT received_at FROM raw_messages WHERE raw_message_id = @raw", conn))
        {
            raw.Parameters.AddWithValue("raw", rawId);
            if (await raw.ExecuteScalarAsync(ct) is DateTime dt)
            {
                received = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
        }
        await using (var stages = new NpgsqlCommand("SELECT stage, finished_at FROM processing.stage_results WHERE raw_message_id = @raw AND run_id = @run AND stage IN ('normalize', 'parse')", conn))
        {
            stages.Parameters.AddWithValue("raw", rawId);
            stages.Parameters.AddWithValue("run", runId);
            await using var reader = await stages.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }
                var at = reader.GetFieldValue<DateTime>(1);
                var value = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
                if (reader.GetString(0) == "normalize")
                {
                    normalized = value;
                }
                else
                {
                    parsed = value;
                }
            }
        }
        await using (var storedCmd = new NpgsqlCommand("SELECT min(o.created_at) FROM messaging.outbox o WHERE o.event_type = 'raw.stored' AND (o.envelope->>'raw_message_id')::bigint = @raw", conn))
        {
            storedCmd.Parameters.AddWithValue("raw", rawId);
            if (await storedCmd.ExecuteScalarAsync(ct) is DateTime st)
            {
                stored = new DateTimeOffset(DateTime.SpecifyKind(st, DateTimeKind.Utc));
            }
        }
        return new Timings(received, stored, normalized, parsed);
    }
}
