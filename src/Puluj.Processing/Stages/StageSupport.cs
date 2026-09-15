using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Puluj.Domain.Entities;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Processing.Stages;

/// <summary>What the stage handlers share: loading the raw row outside the transaction, the stage-result insert, child envelopes.</summary>
public static class StageSupport
{
    /// <summary>The raw row with its source, read on its own connection before any transaction (ADR-0004 §6.2 step 2).</summary>
    public static async Task<RawMessage?> LoadRawAsync(IDbContextFactory<PulujDbContext> factory, long rawMessageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RawMessages.AsNoTracking().Include(r => r.Source).FirstOrDefaultAsync(r => r.RawMessageId == rawMessageId, ct);
    }

    /// <summary>
    /// `processing.stage_results` insert; null when a row for `(raw, run, stage, version)` already exists — the delivery is
    /// a repeat (a second `raw.stored` for the same raw, a replayed upstream event) and the stage must not run twice.
    /// A row whose outcome is `failed` is the exception: a later delivery (a corrected upstream event, an admin retry)
    /// replaces it, so one drift does not lock the raw out of this run.
    /// </summary>
    public static async Task<long?> InsertStageResultAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long rawMessageId, Guid runId, string stage, string stageVersion,
        string outcome, JsonObject outputs, JsonObject versions, DateTimeOffset startedAt, string worker, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO processing.stage_results (raw_message_id, run_id, stage, stage_version, outcome, outputs, started_at, finished_at, worker, versions)
            VALUES (@raw, @run, @stage, @version, @outcome, @outputs, @started, now(), @worker, @versions)
            ON CONFLICT (raw_message_id, run_id, stage, stage_version) DO UPDATE
                SET outcome = EXCLUDED.outcome, outputs = EXCLUDED.outputs, started_at = EXCLUDED.started_at, finished_at = now(), worker = EXCLUDED.worker, versions = EXCLUDED.versions
                WHERE processing.stage_results.outcome = 'failed'
            RETURNING stage_result_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("raw", rawMessageId);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("stage", stage);
        cmd.Parameters.AddWithValue("version", stageVersion);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.Add(new NpgsqlParameter("outputs", NpgsqlDbType.Jsonb) { Value = outputs.ToJsonString() });
        cmd.Parameters.AddWithValue("started", startedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("worker", worker);
        cmd.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = versions.ToJsonString() });
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    /// <summary>A child event of <paramref name="parent"/>: same message identity, correlation, run, lane and pipeline version; caused by the parent.</summary>
    public static Envelope Child(Envelope parent, string eventType, string schemaVersion, string producer, DateTimeOffset occurredAt, JsonObject payload) => new()
    {
        EventType = eventType,
        SchemaVersion = schemaVersion,
        Producer = producer,
        OccurredAt = occurredAt.ToUniversalTime(),
        SourceId = parent.SourceId,
        SourceMessageKey = parent.SourceMessageKey,
        SourceRevision = parent.SourceRevision,
        RawMessageId = parent.RawMessageId,
        CorrelationId = parent.CorrelationId,
        CausationId = parent.EventId,
        Traceparent = parent.Traceparent,
        ProcessingRunId = parent.ProcessingRunId,
        PipelineVersion = parent.PipelineVersion,
        Lane = parent.Lane,
        Payload = payload,
    };

    public static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public static JsonNode? Json(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value);
}
