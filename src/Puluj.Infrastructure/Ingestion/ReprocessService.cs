using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Puluj.Infrastructure.Persistence;
using Puluj.Infrastructure.Settings;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>
/// Rebuilds everything derived from the raw messages: drops targets, tracks, revisions, links, alerts, processing
/// errors, and puts every raw message back to Pending. The processors then re-run the
/// pipeline over all of them in publication order (see ProcessingLoop), so the result is what live processing would
/// have produced had the messages arrived in that order. The raw messages themselves are never touched.
/// Plain DELETEs (not TRUNCATE) so the admin role, which has no TRUNCATE privilege, can run it too.
///
/// The raw table is fenced with an exclusive lock, but only for a moment: a waiting or held ACCESS EXCLUSIVE lock
/// stops the collectors' inserts, and a collector whose insert waits longer than its command timeout dies and
/// restarts. So the bulk of the work happens outside the fence:
///  1. processing is paused (<see cref="PausedKey"/>): the processors stop claiming, in-flight messages finish;
///  2. finished rows go back to Pending in batches, each its own short transaction, no table lock — the processors
///     only lock the rows they hold, and ingestion only inserts;
///  3. the fence: LOCK raw_messages (with a lock timeout and retries, so a long-running processor transaction cannot
///     keep ingestion queued behind the lock request), the few rows that changed since step 2, and the Store advisory
///     lock taken at session level so it survives the commit;
///  4. the derived tables are deleted while only Store is held: a processor that claims a Pending row meanwhile waits
///     for Store before writing anything derived and then writes into the emptied tables; ingestion is not blocked;
///  5. Store is released and processing resumes.
/// The pause is also the durable marker: if the service dies between 3 and 5 the pause stays with a reason that says
/// to run the reprocess again (raw rows are Pending, the deletes are idempotent). Lock order raw table/rows -> Store
/// is shared with processor and watchdog.
/// </summary>
public sealed class ReprocessService(IDbContextFactory<PulujDbContext> factory, SettingsStore settings, ILogger<ReprocessService> logger)
{
    /// <summary>Runtime status key: non-empty while processing must not pick up pending messages (a history load in progress).</summary>
    public const string PausedKey = "Processing:Paused";
    /// <summary>Pause reasons written by this service start with this; <see cref="OwnsPause"/> tells them from a history load's.</summary>
    public const string ReprocessPausePrefix = "reprocess:";
    internal const int BatchSize = 20_000;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly string LockTimeoutSql = "SET LOCAL lock_timeout = '" + (int)LockTimeout.TotalMilliseconds + "ms'";
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);
    private const int LockAttempts = 60; // ~6 minutes of trying before giving up on the fence

    /// <summary>Children before parents (no reliance on cascades). Literal statements: nothing here comes from input.</summary>
    private static readonly (string Table, string Statement)[] Deletes =
    [
        ("target_links", "DELETE FROM target_links"),
        ("track_targets", "DELETE FROM track_targets"),
        ("target_track_revisions", "DELETE FROM target_track_revisions"),
        ("target_tracks", "DELETE FROM target_tracks"),
        ("targets", "DELETE FROM targets"),
        ("air_alerts", "DELETE FROM air_alerts"),
        ("processing_errors", "DELETE FROM processing_errors"),
    ];

    /// <summary>Test seam: runs after the fence committed and before the derived tables are deleted, while Store is held.</summary>
    internal Func<CancellationToken, Task>? BeforeDerivedDeletes { get; set; }

    public async Task<int> ResetAsync(CancellationToken ct)
    {
        // A history load owns the pause while it runs and calls this at its end; then the pause is its to lift.
        var existing = await PausedAsync(ct);
        var ownsPause = existing is null || OwnsPause(existing);
        if (ownsPause)
        {
            await PauseAsync($"{ReprocessPausePrefix} resetting raw messages", ct);
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var reset = await ResetFinishedInBatchesAsync(ct);
            logger.LogInformation("Reprocess: {Rows} finished raw message(s) reset to Pending in batches ({Elapsed:0.0}s, no table lock)", reset, sw.Elapsed.TotalSeconds);

            await using var db = await factory.CreateDbContextAsync(ct);
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
            // One physical connection for the session-level Store lock and everything that runs under it.
            await db.Database.OpenConnectionAsync(ct);
            var fenced = await FenceAndTakeStoreAsync(db, ct);
            logger.LogInformation("Reprocess: fence held for {Elapsed}ms, {Rows} more row(s) reset; Store lock held for the derived deletes", fenced.FenceMs, fenced.Rows);
            try
            {
                if (BeforeDerivedDeletes is { } hook)
                {
                    await hook(ct);
                }
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                foreach (var (table, statement) in Deletes)
                {
                    var n = await db.Database.ExecuteSqlRawAsync(statement, ct);
                    logger.LogInformation("Reprocess: cleared {Table} ({Rows} rows)", table, n);
                }
                await tx.CommitAsync(ct);
            }
            finally
            {
                try
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.ReleaseSession(AdvisoryLocks.Store), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // A broken connection released the session lock on its own; anything else must not hide the real error.
                    logger.LogWarning(ex, "Reprocess: could not release the Store lock explicitly");
                }
            }
            var pending = await db.RawMessages.CountAsync(r => r.ProcessingStatus == Puluj.Domain.Enums.ProcessingStatus.Pending, ct);
            logger.LogInformation("Reprocess: {Count} raw message(s) are Pending for processing in publication order", pending);
            if (ownsPause)
            {
                await ResumeAsync(CancellationToken.None);
            }
            return pending;
        }
        catch (Exception ex) when (ownsPause)
        {
            // Raw rows may already be Pending while derived rows still exist: keep the processors away until the
            // operator runs the reprocess again (idempotent), which is allowed with this pause in place (OwnsPause).
            await PauseAsync($"{ReprocessPausePrefix} failed ({ex.GetType().Name}: {ex.Message}); run the reprocess again", CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Processed/Failed/Skipped rows back to Pending, <see cref="BatchSize"/> at a time. Rows a processor holds (InProgress,
    /// or locked) are left to the fence. Failed ones too: a parser fix is one of the reasons to reprocess.
    /// </summary>
    private async Task<long> ResetFinishedInBatchesAsync(CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            var n = await db.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE raw_messages SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
                WHERE raw_message_id IN (
                    SELECT raw_message_id FROM raw_messages
                    WHERE processing_status IN (1, 2, 3)
                    ORDER BY raw_message_id
                    LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
                """, ct);
            total += n;
            if (n < BatchSize)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// The short exclusive phase: LOCK raw_messages, reset whatever is not Pending, take Store for the session, commit.
    /// A lock request that cannot be granted within <see cref="LockTimeout"/> is withdrawn and retried, so the inserts
    /// queued behind it get through between attempts instead of timing out in the collectors.
    /// </summary>
    private async Task<(int Rows, long FenceMs)> FenceAndTakeStoreAsync(PulujDbContext db, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(LockTimeoutSql, ct);
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE raw_messages IN ACCESS EXCLUSIVE MODE", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable && attempt < LockAttempts)
            {
                await tx.RollbackAsync(CancellationToken.None);
                logger.LogInformation("Reprocess: raw_messages busy (attempt {Attempt}); retrying the fence in {Delay}s", attempt, LockRetryDelay.TotalSeconds);
                await Task.Delay(LockRetryDelay, ct);
                continue;
            }
            var sw = Stopwatch.StartNew();
            // Rows a processor finished after the batched pass (few), plus InProgress rows of instances that are gone.
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE raw_messages SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL WHERE processing_status <> 0", ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = 0", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.TakeSession(AdvisoryLocks.Store), ct);
            await tx.CommitAsync(ct);
            return (rows, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Holds every processor instance: pending messages stay in the database until <see cref="ResumeAsync"/>.</summary>
    public Task PauseAsync(string reason, CancellationToken ct) => settings.SetStatusAsync(PausedKey, reason, ct);

    public Task ResumeAsync(CancellationToken ct) => settings.SetStatusAsync(PausedKey, null, ct);

    public async Task<string?> PausedAsync(CancellationToken ct)
    {
        var v = await settings.GetAsync($"Runtime:{PausedKey}", ct);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>True for a pause this service wrote (a reset in progress or one that failed) — a reprocess may run over it.</summary>
    public static bool OwnsPause(string reason) => reason.StartsWith(ReprocessPausePrefix, StringComparison.Ordinal);
}
