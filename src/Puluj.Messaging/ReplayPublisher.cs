using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Processing;

namespace Puluj.Messaging;

/// <summary>
/// The replay job runner (role `replay`, ADR-0005/plan §11, P14): for a `running` replay run it publishes the raw messages of the
/// scope into the `replay` lane as `raw.stored{is_new:false}` events carrying the run's `processing_run_id` — through the outbox,
/// batch by batch, with the checkpoint written in the same transaction (a crash repeats at most one unpublished batch; the stages'
/// per-run uniqueness makes a repeat a `noop`). One publisher replica works a run at a time (`FOR UPDATE SKIP LOCKED`); pause/cancel
/// are read before every batch; a batch waits while the replay lane holds more than `Replay:MaxInFlight` expected deliveries
/// (backpressure: a replay never floods the queues live shares the broker with). Nothing here touches live results.
/// </summary>
public sealed class ReplayPublisher(
    IDbContextFactory<PulujDbContext> factory,
    OutboxWriter outbox,
    IOptions<ReplayOptions> options,
    ILogger<ReplayPublisher> logger,
    string? instance = null) : BackgroundService
{
    private readonly string _producer = $"replay@{instance ?? Environment.MachineName.ToLowerInvariant()}";

    /// <summary>Batches published by this process (evidence counter for tests).</summary>
    public long Batches => Volatile.Read(ref _batches);
    private long _batches;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool progressed;
            try
            {
                progressed = await PublishOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Replay publisher pass failed; retrying after {Delay}", options.Value.PollInterval);
                progressed = false;
            }
            if (!progressed)
            {
                await Task.Delay(options.Value.PollInterval, ct);
            }
        }
    }

    /// <summary>One pass: lease a running run, publish one batch (or mark the scope done). True when something was published.</summary>
    public async Task<bool> PublishOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await RunService.LeaseRunningReplayAsync(conn, tx, ct);
        if (run is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        // Backpressure on the lane, not on the run: any replay backlog (this run or another) holds the next batch back.
        long inFlight;
        await using (var pending = new NpgsqlCommand("SELECT count(*) FROM processing.deliveries WHERE outcome IS NULL AND lane = 'replay' AND expected_at >= @since", conn, tx))
        {
            pending.Parameters.AddWithValue("since", run.CreatedAt);
            inFlight = (long)(await pending.ExecuteScalarAsync(ct))!;
        }
        if (inFlight > o.MaxInFlight)
        {
            logger.LogDebug("Replay run {Run}: {InFlight} replay deliveries pending (> {Max}); waiting", run.RunId, inFlight, o.MaxInFlight);
            await tx.RollbackAsync(ct);
            return false;
        }
        var batch = await RunService.NextBatchAsync(conn, tx, run, o.BatchSize, ct);
        if (batch.Count == 0)
        {
            await RunService.SaveCheckpointAsync(conn, tx, run.RunId, run.Checkpoint with { Done = true, Failures = 0, Error = null }, null, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Replay run {Run}: scope published ({Published} raw)", run.RunId, run.Checkpoint.Published);
            return false;
        }
        try
        {
            foreach (var raw in batch)
            {
                var envelope = RawStoredEnvelope.Create(_producer, run.RunId, outbox.Runs.PipelineVersion, "replay", raw.RawMessageId, raw.SourceId, raw.SourceCode, raw.SourceMessageId,
                    raw.PublishedAt, raw.ReceivedAt, raw.ReceivedAt, raw.Hash, isNew: false, raw.Text, raw.HasPayload, raw.Url);
                await outbox.EnqueueAsync(conn, tx, envelope, ct);
            }
            var last = batch[^1];
            var checkpoint = run.Checkpoint with { Published = run.Checkpoint.Published + batch.Count, LastRawMessageId = last.RawMessageId, LastPublishedAt = last.PublishedAt, Error = null, Failures = 0 };
            if (!await RunService.SaveCheckpointAsync(conn, tx, run.RunId, checkpoint, null, ct))
            {
                await tx.RollbackAsync(ct); // paused/cancelled meanwhile (the lease row was locked, so this is defensive): nothing published
                return false;
            }
            await tx.CommitAsync(ct);
            Interlocked.Increment(ref _batches);
            logger.LogInformation("Replay run {Run}: published {Count} raw (checkpoint {Published}/{Total}, last raw {Last})", run.RunId, batch.Count, checkpoint.Published, checkpoint.Total, last.RawMessageId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(ct);
            // The batch is neither published nor checkpointed. A transient failure is retried on the next pass with the checkpoint intact;
            // after MaxBatchFailures in a row the run is `failed` (compare-and-set on running) for the operator to resume once the cause is fixed.
            var failures = run.Checkpoint.Failures + 1;
            var fatal = failures >= o.MaxBatchFailures;
            await using var db2 = await factory.CreateDbContextAsync(ct);
            var conn2 = (NpgsqlConnection)db2.Database.GetDbConnection();
            await conn2.OpenAsync(ct);
            await using var tx2 = await conn2.BeginTransactionAsync(ct);
            await RunService.SaveCheckpointAsync(conn2, tx2, run.RunId, run.Checkpoint with { Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message, Failures = failures }, fatal ? RunService.Failed : null, ct);
            await tx2.CommitAsync(ct);
            logger.LogError(ex, "Replay run {Run}: batch failed ({Failures}/{Max}){State}", run.RunId, failures, o.MaxBatchFailures, fatal ? "; state = failed" : "; retrying");
            return false;
        }
    }
}
