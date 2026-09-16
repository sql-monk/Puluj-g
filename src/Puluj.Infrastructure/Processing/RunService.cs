using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Puluj.Contracts;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Processing;

/// <summary>
/// Run and generation orchestration (ADR-0005, plan §11, P14): a replay run is a job with a scope (sources, time window), its own
/// isolated generation and a resumable checkpoint; the state machine is `created → running ↔ paused → verified → promoted → rolled_back`,
/// `cancelled` from running/paused, `failed` from the publisher. Every transition is a compare-and-set on `processing.runs.state`
/// (a lost race is a <see cref="RunConflictException"/>, never a silent overwrite) and leaves a `messaging.control_audit` row.
/// Promote is the atomic switch of the active generation (one transaction: old inactive, new active, run promoted); rollback
/// switches back. Nothing is deleted: the results of both generations stay (ADR-0005 «results are never overwritten»).
/// </summary>
public sealed class RunService(IDbContextFactory<PulujDbContext> factory, OutboxWriter outbox, IOptions<ReplayOptions> options, TimeProvider clock, ILogger<RunService> logger)
{
    public const string Created = "created", Running = "running", Paused = "paused", Verified = "verified", Promoted = "promoted", RolledBack = "rolled_back",
        Cancelled = "cancelled", Failed = "failed", Completed = "completed", Superseded = "superseded";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>The initial live generation (`UUIDv5(generation:live)`, ADR-0010 п.8) — mirrored from `IncidentStateWriter.LiveGeneration` (Processing is not referenced here).</summary>
    public static readonly Guid LiveGeneration = SourceIdentity.NameBasedGuid(new Guid("0f7d3c2a-5b61-4e0c-9a8e-7c1d2b3e4f50"), "generation:live");

    public sealed record ReplayScope(int[]? SourceIds, DateTimeOffset From, DateTimeOffset To, string[]? Stages);

    /// <summary>Creates a replay run in state `created` with a fresh, inactive generation; nothing is published yet.</summary>
    public async Task<Guid> CreateReplayAsync(ReplayScope scope, string actor, string reason, CancellationToken ct)
    {
        Require(actor, reason);
        scope = scope with { From = scope.From.ToUniversalTime(), To = scope.To.ToUniversalTime() };
        if (scope.To <= scope.From)
        {
            throw new ArgumentException("scope.to must be after scope.from");
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var runId = Guid.CreateVersion7();
        var generation = Guid.CreateVersion7();
        await using (var open = new NpgsqlCommand("SELECT run_id FROM processing.runs WHERE kind = 'replay' AND state IN ('created', 'running', 'paused', 'verified')", conn, tx))
        {
            if (await open.ExecuteScalarAsync(ct) is Guid other)
            {
                throw new RunConflictException($"replay run {other} is still open (one replay run at a time: the replay lane and its counters belong to it)");
            }
        }
        long total, ceiling;
        await using (var count = new NpgsqlCommand($"SELECT count(*), coalesce(max(raw_message_id), 0) FROM raw_messages WHERE published_at >= @from AND published_at <= @to{SourceFilter(scope)}", conn, tx))
        {
            count.Parameters.AddWithValue("from", scope.From);
            count.Parameters.AddWithValue("to", scope.To);
            AddSources(count, scope);
            await using var reader = await count.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            total = reader.GetInt64(0);
            ceiling = reader.GetInt64(1);
        }
        Guid? liveRun;
        await using (var live = new NpgsqlCommand("SELECT run_id FROM processing.runs WHERE lane = 'live' AND state = 'running' AND kind = 'live' LIMIT 1", conn, tx))
        {
            liveRun = await live.ExecuteScalarAsync(ct) as Guid?;
        }
        await Exec(conn, tx, "INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES (@g, false, now())", ct, ("g", generation));
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO processing.runs (run_id, lane, kind, state, generation_id, replays_run_id, versions, scope, checkpoint, created_by, created_at, updated_at)
            VALUES (@id, 'replay', 'replay', 'created', @g, @replays, @versions, @scope, @checkpoint, @by, now(), now())
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", runId);
            insert.Parameters.AddWithValue("g", generation);
            insert.Parameters.AddWithValue("replays", (object?)liveRun ?? DBNull.Value);
            insert.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { pipeline_version = outbox.Runs.PipelineVersion, topology_version = outbox.Registry.TopologyVersion }) });
            insert.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { source_ids = scope.SourceIds, from = scope.From, to = scope.To, stages = scope.Stages }, Json) });
            insert.Parameters.Add(new NpgsqlParameter("checkpoint", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new Checkpoint(0, total, 0, null, false, null, ceiling), Json) });
            insert.Parameters.AddWithValue("by", actor);
            try
            {
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                throw new RunConflictException("another replay run was opened concurrently (one replay run at a time)");
            }
        }
        await SubscriptionAdmin.AuditAsync(conn, tx, "run:create", null, "replay", actor, reason, new { runId, generation, scope, total }, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Replay run {Run} created by {Actor}: generation {Generation}, {Total} raw in scope ({Reason})", runId, actor, generation, total, reason);
        return runId;
    }

    public Task StartAsync(Guid runId, string actor, string reason, CancellationToken ct) => TransitionAsync(runId, [Created], Running, "run:start", actor, reason, ct);
    public Task PauseAsync(Guid runId, string actor, string reason, CancellationToken ct) => TransitionAsync(runId, [Running], Paused, "run:pause", actor, reason, ct);
    /// <summary>Back to running from paused, or from failed once the operator fixed the cause (the checkpoint is intact).</summary>
    public Task ResumeAsync(Guid runId, string actor, string reason, CancellationToken ct) => TransitionAsync(runId, [Paused, Failed], Running, "run:resume", actor, reason, ct);
    /// <summary>Cancel from any non-terminal, not-yet-promoted state (a verified run the operator decides against is cancelled, freeing the single replay slot).</summary>
    public Task CancelAsync(Guid runId, string actor, string reason, CancellationToken ct) => TransitionAsync(runId, [Created, Running, Paused, Verified, Failed], Cancelled, "run:cancel", actor, reason, ct, finished: true);

    /// <summary>
    /// Delta catchup (plan §11.5): extends the scope's `to` to the watermark (now − `Replay:WatermarkLag` by default) and re-opens the
    /// checkpoint, so the publisher picks up the raw messages that arrived while the replay ran. Allowed while running/paused/verified
    /// (verified goes back to running: the new delta has to be processed and verified again) — and only **before** the promote: after it
    /// the promoted generation is live's, and the replay lane has no projection to announce what it writes there (review B2/Q6); what live
    /// processed between the last catchup and the promote stays in the previous generation (ADR-0005: replay that window again if needed).
    /// </summary>
    public async Task<DateTimeOffset> CatchUpAsync(Guid runId, DateTimeOffset? watermark, string actor, string reason, CancellationToken ct)
    {
        Require(actor, reason);
        var mark = (watermark ?? clock.GetUtcNow() - options.Value.WatermarkLag).ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await LoadForUpdateAsync(conn, tx, runId, ct);
        if (run.State is not (Running or Paused or Verified))
        {
            throw new RunConflictException($"run {runId} is {run.State}: catchup needs running, paused or verified (before the promote)");
        }
        var scope = run.Scope!;
        var to = scope["to"]!.GetValue<DateTimeOffset>();
        if (mark <= to)
        {
            throw new RunConflictException($"watermark {mark:O} is not after the scope end {to:O}");
        }
        scope["to"] = mark;
        scope["catchup_from"] = to;
        // Delta = ingestion delta (review B3): everything stored after the last ceiling whose published_at falls into the (extended) window —
        // including a late collector's raw with an old published_at — is published by the same keyset walk.
        var (added, ceiling) = await DeltaAsync(conn, tx, scope, run.Checkpoint.IngestCeiling, ct);
        var checkpoint = run.Checkpoint with { Done = false, Total = run.Checkpoint.Total + added, IngestCeiling = ceiling };
        var state = run.State == Verified ? Running : run.State;
        await using (var update = new NpgsqlCommand("UPDATE processing.runs SET scope = @scope, checkpoint = @cp, state = @state, updated_at = now() WHERE run_id = @id", conn, tx))
        {
            update.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = scope.ToJsonString() });
            update.Parameters.Add(new NpgsqlParameter("cp", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(checkpoint, Json) });
            update.Parameters.AddWithValue("state", state);
            update.Parameters.AddWithValue("id", runId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await SubscriptionAdmin.AuditAsync(conn, tx, "run:catchup", null, "replay", actor, reason, new { runId, from = to, to = mark, state }, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Replay run {Run}: catchup {From:O} → {To:O} by {Actor}", runId, to, mark, actor);
        return mark;
    }

    /// <summary>
    /// Verify (plan §11.5): the scope is published, every delivery of the run's events is terminal, nothing is quarantined; the counts report
    /// is stored in the run's scope (`verification`) for the operator. Fails with the first unmet condition (409 in the API).
    /// </summary>
    public async Task<JsonObject> VerifyAsync(Guid runId, string actor, string reason, CancellationToken ct)
    {
        Require(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await LoadForUpdateAsync(conn, tx, runId, ct);
        if (run.State is not (Running or Paused))
        {
            throw new RunConflictException($"run {runId} is {run.State}: verify needs running or paused");
        }
        if (!run.Checkpoint.Done)
        {
            throw new RunConflictException($"run {runId}: scope not fully published ({run.Checkpoint.Published}/{run.Checkpoint.Total})");
        }
        var report = await ReportAsync(conn, tx, run, ct);
        if (report["pending_deliveries"]!.GetValue<long>() > 0)
        {
            throw new RunConflictException($"run {runId}: {report["pending_deliveries"]} replay deliveries still expected (or unconfirmed in the outbox)");
        }
        if (report["quarantined"]!.GetValue<long>() > 0)
        {
            throw new RunConflictException($"run {runId}: {report["quarantined"]} quarantined deliveries — retry or waive them first");
        }
        if (report["unanalyzed"]!.GetValue<long>() > 0)
        {
            throw new RunConflictException($"run {runId}: {report["unanalyzed"]} published raw messages without a terminal analysis");
        }
        var scope = run.Scope!;
        scope["verification"] = report;
        await using (var update = new NpgsqlCommand("UPDATE processing.runs SET state = 'verified', scope = @scope, updated_at = now() WHERE run_id = @id AND state = @from", conn, tx))
        {
            update.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = scope.ToJsonString() });
            update.Parameters.AddWithValue("id", runId);
            update.Parameters.AddWithValue("from", run.State);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                throw new RunConflictException($"run {runId} changed state concurrently");
            }
        }
        await Exec(conn, tx, "UPDATE processing.generations SET verified_by = @by WHERE generation_id = @g", ct, ("by", actor), ("g", run.GenerationId!.Value));
        await SubscriptionAdmin.AuditAsync(conn, tx, "run:verify", null, "replay", actor, reason, new { runId, report }, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Replay run {Run} verified by {Actor}: {Report}", runId, actor, report.ToJsonString());
        return report;
    }

    /// <summary>
    /// Atomic switch: the run's generation becomes the active one, the previous active is remembered for rollback; only from `verified`.
    /// A partial scope (active incidents outside the replayed window would vanish from the read side) is refused unless <paramref name="force"/>
    /// — the deviation from plan §11.5 «explicit replacement scope» is the operator's, audited decision.
    /// </summary>
    public async Task<Guid> PromoteAsync(Guid runId, string actor, string reason, CancellationToken ct, bool force = false)
    {
        Require(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await LoadForUpdateAsync(conn, tx, runId, ct);
        if (run.State != Verified)
        {
            throw new RunConflictException($"run {runId} is {run.State}: promote needs verified");
        }
        var outside = run.Scope?["verification"]?["active_incidents_outside_scope"]?.GetValue<long>() ?? 0;
        if (outside > 0 && !force)
        {
            throw new RunConflictException($"run {runId}: {outside} active incidents lie outside the replayed scope and would disappear — narrow the scope, replay wider, or promote with force");
        }
        var generation = run.GenerationId!.Value;
        // Live writers hold the pointer's advisory lock shared while they stamp a generation: this exclusive lock waits for them and blocks
        // new ones, so no incident lands in a generation that is deactivated in the same instant (review B2).
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@k))", ct, ("k", "generation:active"));
        Guid? previous;
        await using (var deactivate = new NpgsqlCommand("UPDATE processing.generations SET is_active = false WHERE is_active AND generation_id <> @g RETURNING generation_id", conn, tx))
        {
            deactivate.Parameters.AddWithValue("g", generation);
            previous = await deactivate.ExecuteScalarAsync(ct) as Guid?;
        }
        await Exec(conn, tx, "UPDATE processing.generations SET is_active = true, promoted_at = now(), rolled_back_at = NULL WHERE generation_id = @g", ct, ("g", generation));
        var scope = run.Scope!;
        scope["promoted_from"] = previous?.ToString();
        await using (var update = new NpgsqlCommand("UPDATE processing.runs SET state = 'promoted', scope = @scope, updated_at = now() WHERE run_id = @id AND state = 'verified'", conn, tx))
        {
            update.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = scope.ToJsonString() });
            update.Parameters.AddWithValue("id", runId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await SubscriptionAdmin.AuditAsync(conn, tx, "run:promote", null, "replay", actor, reason, new { runId, generation, previous, force, outside }, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Replay run {Run} promoted by {Actor}: active generation {Generation} (was {Previous}): {Reason}", runId, actor, generation, previous, reason);
        return generation;
    }

    /// <summary>Rollback: the previous active generation (remembered at promote) is active again; only from `promoted`.</summary>
    public async Task<Guid?> RollbackAsync(Guid runId, string actor, string reason, CancellationToken ct)
    {
        Require(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await LoadForUpdateAsync(conn, tx, runId, ct);
        if (run.State != Promoted)
        {
            throw new RunConflictException($"run {runId} is {run.State}: rollback needs promoted");
        }
        var generation = run.GenerationId!.Value;
        var previous = Guid.TryParse(run.Scope?["promoted_from"]?.GetValue<string>(), out var p) ? p : (Guid?)null;
        if (previous is null)
        {
            // Promoted over nothing (fresh database): the initial live generation is the read path to restore — created if it never existed.
            await Exec(conn, tx, "INSERT INTO processing.generations (generation_id, is_active, created_at) VALUES (@g, false, now()) ON CONFLICT (generation_id) DO NOTHING", ct, ("g", LiveGeneration));
            previous = LiveGeneration;
        }
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@k))", ct, ("k", "generation:active"));
        // What live wrote into the promoted generation since the promote is invisible after the rollback — counted for the operator (ADR-0005: replay that window again).
        long writtenSincePromote;
        await using (var since = new NpgsqlCommand("SELECT count(*) FROM incidents i WHERE i.generation_id = @g AND i.created_at >= (SELECT promoted_at FROM processing.generations WHERE generation_id = @g)", conn, tx))
        {
            since.Parameters.AddWithValue("g", generation);
            writtenSincePromote = (long)(await since.ExecuteScalarAsync(ct))!;
        }
        await Exec(conn, tx, "UPDATE processing.generations SET is_active = false, rolled_back_at = now() WHERE generation_id = @g", ct, ("g", generation));
        await Exec(conn, tx, "UPDATE processing.generations SET is_active = true WHERE generation_id = @g", ct, ("g", previous.Value));
        await Exec(conn, tx, "UPDATE processing.runs SET state = 'rolled_back', updated_at = now(), finished_at = now() WHERE run_id = @id AND state = 'promoted'", ct, ("id", runId));
        await SubscriptionAdmin.AuditAsync(conn, tx, "run:rollback", null, "replay", actor, reason, new { runId, generation, restored = previous, incidentsWrittenSincePromote = writtenSincePromote }, ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Replay run {Run} rolled back by {Actor}: active generation {Previous} again: {Reason}", runId, actor, previous, reason);
        return previous;
    }

    /// <summary>The runs, newest first, with their generation state (admin list).</summary>
    public async Task<IReadOnlyList<RunDto>> ListAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ProcessingRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        var generations = await db.ProcessingGenerations.AsNoTracking().ToDictionaryAsync(g => g.GenerationId, ct);
        return rows.Select(r =>
        {
            var g = r.GenerationId is { } id ? generations.GetValueOrDefault(id) : null;
            var cp = r.Checkpoint is null ? null : JsonSerializer.Deserialize<Checkpoint>(r.Checkpoint.RootElement.GetRawText(), Json);
            return new RunDto(r.RunId, r.Lane, r.Kind, r.State, r.GenerationId, g?.IsActive ?? false, g?.PromotedAt, g?.RolledBackAt, g?.VerifiedBy, r.SupersedesRunId, r.ReplaysRunId,
                r.Versions?.RootElement.Clone(), r.Scope?.RootElement.Clone(),
                cp is null ? null : new RunCheckpointDto(cp.Published, cp.Total, cp.LastRawMessageId, cp.LastPublishedAt, cp.Done, cp.Error),
                r.CreatedBy, r.CreatedAt, r.UpdatedAt, r.FinishedAt);
        }).ToList();
    }

    // ---- publisher side ----

    /// <summary>
    /// Resumable position of a replay run (ADR-0005 contract): `LastRawMessageId` — keyset over the primary key (ingestion order, the order live
    /// saw the messages); `IngestCeiling` — the highest raw id in scope (raised by a catchup); `Done` — the scope is published; `Failures` —
    /// consecutive batch failures (the run is `failed` after `Replay:MaxBatchFailures`).
    /// </summary>
    public sealed record Checkpoint(long Published, long Total, long LastRawMessageId, DateTimeOffset? LastPublishedAt, bool Done, string? Error, long IngestCeiling = 0, int Failures = 0);

    public sealed record RunRow(Guid RunId, string State, Guid? GenerationId, JsonObject? Scope, Checkpoint Checkpoint, DateTimeOffset CreatedAt);

    /// <summary>Locks one running replay run for the publisher (`FOR UPDATE SKIP LOCKED`: one publisher replica per run); null when none.</summary>
    public static async Task<RunRow?> LeaseRunningReplayAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_id, state, generation_id, scope::text, checkpoint::text, created_at FROM processing.runs
            WHERE kind = 'replay' AND state = 'running' AND NOT coalesce((checkpoint->>'done')::boolean, false)
            ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Raw messages of the scope after the checkpoint, in ingestion (primary key) order, bounded by the ingest ceiling (review B3/N3).</summary>
    public static async Task<List<(long RawMessageId, int SourceId, string SourceCode, string SourceMessageId, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt, string Hash, string? Text, bool HasPayload, string? Url)>> NextBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, RunRow run, int batchSize, CancellationToken ct)
    {
        var scope = run.Scope!;
        var sources = scope["source_ids"] is JsonArray a && a.Count > 0 ? a.Select(x => x!.GetValue<int>()).ToArray() : null;
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT r.raw_message_id, r.source_id, s.code, r.source_message_id, r.published_at, r.received_at, r.hash, r.raw_text, r.raw_payload IS NOT NULL, r.url
            FROM raw_messages r JOIN sources s ON s.source_id = r.source_id
            WHERE r.raw_message_id > @last_id AND r.raw_message_id <= @ceiling
              AND r.published_at >= @from AND r.published_at <= @to{(sources is null ? "" : " AND r.source_id = ANY(@sources)")}
            ORDER BY r.raw_message_id
            LIMIT @batch
            """, conn, tx);
        cmd.Parameters.AddWithValue("from", scope["from"]!.GetValue<DateTimeOffset>());
        cmd.Parameters.AddWithValue("to", scope["to"]!.GetValue<DateTimeOffset>());
        if (sources is not null)
        {
            cmd.Parameters.AddWithValue("sources", sources);
        }
        cmd.Parameters.AddWithValue("last_id", run.Checkpoint.LastRawMessageId);
        cmd.Parameters.AddWithValue("ceiling", run.Checkpoint.IngestCeiling);
        cmd.Parameters.AddWithValue("batch", batchSize);
        var rows = new List<(long, int, string, string, DateTimeOffset, DateTimeOffset, string, string?, bool, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    /// <summary>
    /// Writes the checkpoint (same transaction as the batch's outbox rows: published and checkpointed together, or neither). Compare-and-set on
    /// `running`: a concurrent pause/cancel wins and the publisher's write is dropped (returns false). An optional state moves the run to `failed`.
    /// </summary>
    public static async Task<bool> SaveCheckpointAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid runId, Checkpoint checkpoint, string? state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"UPDATE processing.runs SET checkpoint = @cp, updated_at = now(){(state is null ? "" : ", state = @state")} WHERE run_id = @id AND state = 'running'", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("cp", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(checkpoint, Json) });
        cmd.Parameters.AddWithValue("id", runId);
        if (state is not null)
        {
            cmd.Parameters.AddWithValue("state", state);
        }
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---- internals ----

    private async Task TransitionAsync(Guid runId, string[] from, string to, string action, string actor, string reason, CancellationToken ct, bool finished = false)
    {
        Require(actor, reason);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await LoadForUpdateAsync(conn, tx, runId, ct);
        if (!from.Contains(run.State, StringComparer.Ordinal))
        {
            throw new RunConflictException($"run {runId} is {run.State}: {action} needs {string.Join("|", from)}");
        }
        await using (var update = new NpgsqlCommand($"UPDATE processing.runs SET state = @to, updated_at = now(){(finished ? ", finished_at = now()" : "")} WHERE run_id = @id AND state = @from", conn, tx))
        {
            update.Parameters.AddWithValue("to", to);
            update.Parameters.AddWithValue("id", runId);
            update.Parameters.AddWithValue("from", run.State);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                throw new RunConflictException($"run {runId} changed state concurrently");
            }
        }
        await SubscriptionAdmin.AuditAsync(conn, tx, action, null, "replay", actor, reason, new { runId, from = run.State, to }, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Run {Run}: {From} → {To} by {Actor}: {Reason}", runId, run.State, to, actor, reason);
    }

    private static async Task<RunRow> LoadForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT run_id, state, generation_id, scope::text, checkpoint::text, created_at FROM processing.runs WHERE run_id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : throw new KeyNotFoundException($"run {runId} not found");
    }

    private static RunRow Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3))?.AsObject(),
        reader.IsDBNull(4) ? new Checkpoint(0, 0, 0, null, false, null) : JsonSerializer.Deserialize<Checkpoint>(reader.GetString(4), Json)!,
        reader.GetFieldValue<DateTimeOffset>(5));

    /// <summary>Counts for the verification report: scope vs published, stage outcomes, observations, incidents of the run's generation vs the active one in the same window.</summary>
    private static async Task<JsonObject> ReportAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunRow run, CancellationToken ct)
    {
        var report = new JsonObject
        {
            ["published"] = run.Checkpoint.Published,
            ["total"] = run.Checkpoint.Total,
        };
        // The replay lane belongs to the one open replay run (create refuses a second): its receipts and outbox rows since the run was created
        // are the run's (review N2, N12); leftovers of earlier, terminal runs are outside that window and do not block it.
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM processing.deliveries WHERE lane = 'replay' AND outcome IS NULL AND expected_at >= @since),
                   (SELECT count(*) FROM processing.deliveries WHERE lane = 'replay' AND outcome = 'quarantined' AND expected_at >= @since),
                   (SELECT count(*) FROM messaging.outbox WHERE lane = 'replay' AND confirmed_at IS NULL AND created_at >= @since),
                   (SELECT count(*) FROM processing.deliveries d JOIN messaging.events e ON e.event_id = d.event_id WHERE e.processing_run_id = @run AND d.outcome IN ('completed', 'noop', 'waived'))
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("run", run.RunId);
            cmd.Parameters.AddWithValue("since", run.CreatedAt);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            report["pending_deliveries"] = reader.GetInt64(0) + reader.GetInt64(2);
            report["quarantined"] = reader.GetInt64(1);
            report["unconfirmed_outbox"] = reader.GetInt64(2);
            report["terminal_deliveries"] = reader.GetInt64(3);
        }
        var stages = new JsonObject();
        await using (var cmd = new NpgsqlCommand("SELECT stage || ':' || outcome, count(*) FROM processing.stage_results WHERE run_id = @run GROUP BY 1 ORDER BY 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("run", run.RunId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stages[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        report["stages"] = stages;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM processing.extractions WHERE run_id = @run), (SELECT count(*) FROM processing.observations WHERE run_id = @run),
                   (SELECT count(DISTINCT raw_message_id) FROM messaging.events WHERE processing_run_id = @run AND event_type = 'raw.stored')
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("run", run.RunId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            report["extractions"] = reader.GetInt64(0);
            report["observations"] = reader.GetInt64(1);
            report["distinct_raw"] = reader.GetInt64(2);
            report["unanalyzed"] = Math.Max(0, reader.GetInt64(2) - reader.GetInt64(0)); // every replayed raw must reach a terminal analysis (review Q5; distinct — a repeated batch is not a raw)
        }
        var scope = run.Scope!;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM incidents WHERE generation_id = @g),
                   (SELECT count(*) FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id WHERE g.is_active AND i.event_at >= @from AND i.event_at <= @to),
                   (SELECT count(*) FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id WHERE g.is_active AND (i.event_at < @from OR i.event_at > @to))
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("g", run.GenerationId!.Value);
            cmd.Parameters.AddWithValue("from", scope["from"]!.GetValue<DateTimeOffset>());
            cmd.Parameters.AddWithValue("to", scope["to"]!.GetValue<DateTimeOffset>());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            report["incidents_in_generation"] = reader.GetInt64(0);
            report["active_incidents_in_window"] = reader.GetInt64(1);
            report["active_incidents_outside_scope"] = reader.GetInt64(2); // a promote switches the whole generation: these would disappear from the read side
        }
        // Active incidents of the window whose raw messages produced no incident in the run's generation (typically LLM-only facts: the replay never calls the model, review N10).
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT count(DISTINCT i.incident_id)
            FROM incidents i JOIN processing.generations g ON g.generation_id = i.generation_id
            JOIN incident_observations io ON io.incident_id = i.incident_id JOIN processing.observations o ON o.observation_id = io.observation_id
            WHERE g.is_active AND i.event_at >= @from AND i.event_at <= @to
              AND NOT EXISTS (SELECT 1 FROM incident_observations io2 JOIN processing.observations o2 ON o2.observation_id = io2.observation_id WHERE io2.generation_id = @g AND o2.raw_message_id = o.raw_message_id)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("g", run.GenerationId!.Value);
            cmd.Parameters.AddWithValue("from", scope["from"]!.GetValue<DateTimeOffset>());
            cmd.Parameters.AddWithValue("to", scope["to"]!.GetValue<DateTimeOffset>());
            report["active_incidents_missing_in_generation"] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        report["verified_at"] = DateTimeOffset.UtcNow;
        return report;
    }

    /// <summary>Raw messages stored after the ceiling that fall into the (extended) window, and the new ceiling.</summary>
    private static async Task<(long Added, long Ceiling)> DeltaAsync(NpgsqlConnection conn, NpgsqlTransaction tx, JsonObject scope, long ceiling, CancellationToken ct)
    {
        var sources = scope["source_ids"] is JsonArray a && a.Count > 0 ? a.Select(x => x!.GetValue<int>()).ToArray() : null;
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*), coalesce(max(raw_message_id), @ceiling) FROM raw_messages WHERE raw_message_id > @ceiling AND published_at >= @from AND published_at <= @to{(sources is null ? "" : " AND source_id = ANY(@sources)")}", conn, tx);
        cmd.Parameters.AddWithValue("ceiling", ceiling);
        cmd.Parameters.AddWithValue("from", scope["from"]!.GetValue<DateTimeOffset>());
        cmd.Parameters.AddWithValue("to", scope["to"]!.GetValue<DateTimeOffset>());
        if (sources is not null)
        {
            cmd.Parameters.AddWithValue("sources", sources);
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), Math.Max(ceiling, reader.GetInt64(1)));
    }

    private static string SourceFilter(ReplayScope scope) => scope.SourceIds is { Length: > 0 } ? " AND source_id = ANY(@sources)" : "";

    private static void AddSources(NpgsqlCommand cmd, ReplayScope scope)
    {
        if (scope.SourceIds is { Length: > 0 })
        {
            cmd.Parameters.AddWithValue("sources", scope.SourceIds);
        }
    }

    private static void Require(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("actor and reason are required");
        }
        if (actor.Length > SubscriptionAdmin.MaxActor || reason.Length > SubscriptionAdmin.MaxReason)
        {
            throw new ArgumentException($"actor ≤ {SubscriptionAdmin.MaxActor} and reason ≤ {SubscriptionAdmin.MaxReason} characters");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>A state transition that is not allowed from the run's current state (or lost a race): 409 in the API.</summary>
public sealed class RunConflictException(string message) : InvalidOperationException(message);

/// <summary>`Replay` configuration (P14): publisher pacing and quotas.</summary>
public sealed class ReplayOptions
{
    public const string Section = "Replay";

    /// <summary>Raw messages published per batch (one transaction: outbox rows + checkpoint).</summary>
    public int BatchSize { get; set; } = 200;
    /// <summary>Idle wait between publisher passes (a running run with nothing left, or no run at all).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Backpressure: a batch is published only while the replay lane has at most this many expected deliveries without a receipt.</summary>
    public int MaxInFlight { get; set; } = 1000;
    /// <summary>Consecutive batch failures after which the run is marked `failed` (resume from the API once the cause is fixed); earlier failures are retried with the checkpoint intact.</summary>
    public int MaxBatchFailures { get; set; } = 5;
    /// <summary>Default watermark for a delta catchup: now minus this lag (raw messages still arriving at the collectors are not chased).</summary>
    public TimeSpan WatermarkLag { get; set; } = TimeSpan.FromSeconds(60);
}
