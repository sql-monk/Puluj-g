using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Messaging;
using Puluj.Processing.Stages;

namespace Puluj.Processing.Analytics;

/// <summary>
/// The `message-analytics` subscription (plan §10, ADR-0013, P15): the lifecycle projection `analytics.message_lifecycle`,
/// one row per raw message per run. `raw.stored` upserts the root (counted once per raw across runs and edits — the primary key);
/// `message.analysis.completed` fills outcome/method/facts/versions/timings/error and the LLM cost of the run; `track.changed`/
/// `alert.changed`/`incident.changed` append the aggregate id and mark the branch done — `domain_completed_at` once every expected
/// branch reported. Every write is an idempotent `ON CONFLICT DO UPDATE` (a redelivery changes nothing); a late result (an event
/// that arrives after the root, or before it) lands in the same row. Branches that finish with a silent `noop` and events lost
/// to the projection are filled by the reconciliation over the receipts (Puluj.Analytics `LifecycleReconciliation`). The table's DDL
/// lives in the pipeline's own migrations (`AddMessageLifecycle`), so the `migrate` role creates it before any consumer starts.
/// </summary>
public sealed class MessageAnalyticsHandler(IDbContextFactory<PulujDbContext> factory, Puluj.Infrastructure.Messaging.Topology.TopologyRegistrar registrar, PulujMetrics metrics) : IDeliveryHandler
{
    public const string Subscription = "message-analytics";
    public const string RawStored = "raw.stored", AnalysisCompleted = "message.analysis.completed", TrackChanged = "track.changed", AlertChanged = "alert.changed", IncidentChanged = "incident.changed";
    public string SubscriptionId => Subscription;
    public string Producer { get; set; } = Subscription;

    private sealed record Root(long RawMessageId, int SourceId, string SourceMessageKey, string SourceRevision, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt, bool HasText, int TextLength, bool HasPayload, bool IsEdit);
    private sealed record Prepared(Root Root);

    public async Task<object?> PrepareAsync(Envelope envelope, CancellationToken ct)
    {
        if (envelope.RawMessageId is not { } rawId)
        {
            return null; // an aggregate change caused by an expiry command or an admin command: no root to attribute
        }
        var raw = await StageSupport.LoadRawAsync(factory, rawId, ct);
        if (raw is null)
        {
            throw new InvalidOperationException($"raw_messages {rawId} not found (yet?)"); // transient: the raw-writer's commit is visible before its raw.stored is relayed
        }
        return new Prepared(new Root(raw.RawMessageId, raw.SourceId, raw.SourceMessageKey, raw.SourceRevision, raw.PublishedAt, raw.ReceivedAt, !string.IsNullOrEmpty(raw.RawText), raw.RawText?.Length ?? 0, raw.RawPayload is not null, raw.SourceRevision != "0"));
    }

    public async Task<DeliveryResult> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, object? state, CancellationToken ct)
    {
        if (state is not Prepared p)
        {
            metrics.WriterOutcome(Subscription, "noop_no_raw");
            return DeliveryResult.Noop("no_raw: the event names no raw message (expiry/admin command)");
        }
        var payload = envelope.Payload ?? new JsonObject();
        try
        {
            switch (envelope.EventType)
            {
                case RawStored:
                    await UpsertRootAsync(conn, tx, envelope, p.Root, storedAt: envelope.OccurredAt, ct);
                    metrics.WriterOutcome(Subscription, "root");
                    return new DeliveryResult("completed", "root");
                case AnalysisCompleted:
                    await UpsertRootAsync(conn, tx, envelope, p.Root, null, ct);
                    await ApplyAnalysisAsync(conn, tx, envelope, p.Root, payload, registrar.Registry, ct);
                    metrics.WriterOutcome(Subscription, "analysis");
                    return new DeliveryResult("completed", "analysis");
                case TrackChanged or AlertChanged or IncidentChanged:
                    await UpsertRootAsync(conn, tx, envelope, p.Root, null, ct);
                    await ApplyDomainAsync(conn, tx, envelope, p.Root, payload, ct);
                    metrics.WriterOutcome(Subscription, "domain");
                    return new DeliveryResult("completed", "domain");
                default:
                    metrics.WriterOutcome(Subscription, "noop_unbound");
                    return DeliveryResult.Noop($"unbound: {envelope.EventType} is not part of the lifecycle");
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // A database behind migration AddMessageLifecycle: a plain failure — retried like any transient error and quarantined after the
            // attempt limit, which is the right alarm for a deploy that skipped the migrate role (the projection is rebuildable from evidence).
            throw new InvalidOperationException("analytics.message_lifecycle does not exist (migration AddMessageLifecycle not applied)", ex);
        }
    }

    private static async Task UpsertRootAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Root root, DateTimeOffset? storedAt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO analytics.message_lifecycle (raw_message_id, run_id, source_id, source_message_key, source_revision, lane, published_at, received_at, stored_at, has_text, text_length, has_payload, is_edit,
                fact_count, unlocated_facts, timings_available, expected_branches, branches_done, incident_ids, track_ids, alert_ids, llm_calls, llm_input_tokens, llm_cache_tokens, llm_output_tokens, llm_cost_usd, llm_latency_ms, completion_available, source_of_truth, updated_at)
            VALUES (@raw, @run, @source, @key, @revision, @lane, @published, @received, @stored, @has_text, @text_length, @has_payload, @is_edit,
                0, 0, false, '{}', '{}', '{}', '{}', '{}', 0, 0, 0, 0, 0, 0, true, 'event', now())
            ON CONFLICT (raw_message_id, run_id) DO UPDATE SET
                stored_at = COALESCE(analytics.message_lifecycle.stored_at, EXCLUDED.stored_at),
                lane = CASE WHEN analytics.message_lifecycle.lane = 'legacy' THEN EXCLUDED.lane ELSE analytics.message_lifecycle.lane END,
                source_of_truth = 'event', updated_at = now()
            """, conn, tx);
        cmd.Parameters.AddWithValue("raw", root.RawMessageId);
        cmd.Parameters.AddWithValue("run", envelope.ProcessingRunId);
        cmd.Parameters.AddWithValue("source", root.SourceId);
        cmd.Parameters.AddWithValue("key", root.SourceMessageKey);
        cmd.Parameters.AddWithValue("revision", root.SourceRevision);
        cmd.Parameters.AddWithValue("lane", envelope.Lane);
        cmd.Parameters.AddWithValue("published", root.PublishedAt);
        cmd.Parameters.AddWithValue("received", root.ReceivedAt);
        cmd.Parameters.AddWithValue("stored", (object?)storedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("has_text", root.HasText);
        cmd.Parameters.AddWithValue("text_length", root.TextLength);
        cmd.Parameters.AddWithValue("has_payload", root.HasPayload);
        cmd.Parameters.AddWithValue("is_edit", root.IsEdit);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyAnalysisAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Root root, JsonObject payload, Puluj.Infrastructure.Messaging.Topology.TopologyRegistry registry, CancellationToken ct)
    {
        var outcome = payload["outcome"]?.GetValue<string>() ?? throw new PermanentDeliveryException("invalid_payload", "message.analysis.completed without outcome");
        var method = payload["method"]?.GetValue<string>() ?? "rules";
        var factCount = payload["fact_count"]?.GetValue<int>() ?? 0;
        // The manifest names the branches by category; only the ones that serve this lane are expected (a replay lane has no track/alert writers, P14) — the same rule the receipts follow.
        var branches = payload["expected_branches"] is JsonArray eb
            ? eb.Select(b => b?.GetValue<string>()).Where(b => b is not null && registry.Subscriptions.TryGetValue(b, out var sub) && sub.Lanes.Contains(envelope.Lane, StringComparer.Ordinal)).Select(b => b!).Distinct().ToArray()
            : [];
        var timings = payload["timings"]?.DeepClone();
        var versions = payload["versions"]?.DeepClone();
        var error = payload["error"]?.ToJsonString();

        int unlocated;
        await using (var facts = new NpgsqlCommand("SELECT count(*)::int FROM processing.observations WHERE raw_message_id = @raw AND run_id = @run AND (payload->'location' IS NULL OR payload->'location' = 'null'::jsonb)", conn, tx))
        {
            facts.Parameters.AddWithValue("raw", root.RawMessageId);
            facts.Parameters.AddWithValue("run", envelope.ProcessingRunId);
            unlocated = (int)(await facts.ExecuteScalarAsync(ct))!;
        }
        // Cost = every LLM call made for this raw in this run — retries, takeovers and late answers included (they were paid for), not only the ids the event names.
        int calls, latency;
        long input, cache, output;
        decimal cost;
        await using (var llm = new NpgsqlCommand(
            "SELECT count(*)::int, coalesce(sum(input_tokens), 0), coalesce(sum(coalesce(cache_read_input_tokens, 0) + coalesce(cache_creation_input_tokens, 0)), 0), coalesce(sum(output_tokens), 0), coalesce(sum(estimated_cost_usd), 0), coalesce(sum(duration_ms), 0)::int FROM llm_requests WHERE raw_message_id = @raw AND run_id = @run", conn, tx))
        {
            llm.Parameters.AddWithValue("raw", root.RawMessageId);
            llm.Parameters.AddWithValue("run", envelope.ProcessingRunId);
            await using var reader = await llm.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            calls = reader.GetInt32(0);
            input = reader.GetInt64(1);
            cache = reader.GetInt64(2);
            output = reader.GetInt64(3);
            cost = reader.GetDecimal(4);
            latency = reader.GetInt32(5);
        }
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE analytics.message_lifecycle SET
                analyzed_at = @at, analysis_outcome = @outcome, method = @method, fact_count = @facts, unlocated_facts = @unlocated,
                versions = @versions, timings = @timings, timings_available = @timings IS NOT NULL, error = @error,
                expected_branches = @branches,
                domain_completed_at = CASE WHEN cardinality(@branches) = 0 THEN @at WHEN @branches <@ branches_done THEN COALESCE(domain_completed_at, @at) ELSE domain_completed_at END,
                llm_calls = @calls, llm_input_tokens = @input, llm_cache_tokens = @cache, llm_output_tokens = @output, llm_cost_usd = @cost, llm_latency_ms = @latency,
                source_of_truth = 'event', updated_at = now()
            WHERE raw_message_id = @raw AND run_id = @run
            """, conn, tx);
        cmd.Parameters.AddWithValue("at", envelope.OccurredAt);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("method", method);
        cmd.Parameters.AddWithValue("facts", factCount);
        cmd.Parameters.AddWithValue("unlocated", unlocated);
        cmd.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = (object?)versions?.ToJsonString() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("timings", NpgsqlDbType.Jsonb) { Value = (object?)timings?.ToJsonString() ?? DBNull.Value });
        cmd.Parameters.AddWithValue("error", (object?)(error is { Length: > 2000 } ? error[..2000] : error) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("branches", branches);
        cmd.Parameters.AddWithValue("calls", calls);
        cmd.Parameters.AddWithValue("input", input);
        cmd.Parameters.AddWithValue("cache", cache);
        cmd.Parameters.AddWithValue("output", output);
        cmd.Parameters.AddWithValue("cost", cost);
        cmd.Parameters.AddWithValue("latency", latency);
        cmd.Parameters.AddWithValue("raw", root.RawMessageId);
        cmd.Parameters.AddWithValue("run", envelope.ProcessingRunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyDomainAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope envelope, Root root, JsonObject payload, CancellationToken ct)
    {
        var (column, branch, idKey) = envelope.EventType switch
        {
            TrackChanged => ("track_ids", "track-worker", "track_id"),
            AlertChanged => ("alert_ids", "alert-worker", "alert_id"),
            _ => ("incident_ids", "incident-worker", "incident_id"),
        };
        var id = payload[idKey]?.GetValue<long>() ?? throw new PermanentDeliveryException("invalid_payload", $"{envelope.EventType} without {idKey}");
        var generation = Guid.TryParse(payload["generation_id"]?.GetValue<string>(), out var g) ? g : (Guid?)null;
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE analytics.message_lifecycle SET
                {column} = CASE WHEN @id = ANY({column}) THEN {column} ELSE array_append({column}, @id) END,
                generation_id = COALESCE(@generation, generation_id),
                branches_done = CASE WHEN @branch = ANY(branches_done) THEN branches_done ELSE array_append(branches_done, @branch) END,
                domain_completed_at = CASE
                    WHEN analyzed_at IS NOT NULL AND expected_branches <@ (CASE WHEN @branch = ANY(branches_done) THEN branches_done ELSE array_append(branches_done, @branch) END) THEN COALESCE(domain_completed_at, @at)
                    ELSE domain_completed_at END,
                source_of_truth = 'event', updated_at = now()
            WHERE raw_message_id = @raw AND run_id = @run
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("generation", (object?)generation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("branch", branch);
        cmd.Parameters.AddWithValue("at", envelope.OccurredAt);
        cmd.Parameters.AddWithValue("raw", root.RawMessageId);
        cmd.Parameters.AddWithValue("run", envelope.ProcessingRunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The run id every legacy (pre-stage) row is filed under by the backfill: `UUIDv5(run:legacy)`.</summary>
    public static readonly Guid LegacyRun = SourceIdentity.NameBasedGuid(new Guid("0f7d3c2a-5b61-4e0c-9a8e-7c1d2b3e4f50"), "run:legacy");
}
