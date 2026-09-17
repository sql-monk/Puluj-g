using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Analysis;

/// <summary>Drains the raw-message index and refreshes independent track-first analytics. Every batch commits its
/// index rows and watermark together, so a crash is safe to replay.</summary>
public sealed class AnalysisRunner(
    IDbContextFactory<AnalyticsDbContext> factory,
    IOptions<AnalyticsOptions> options,
    AnalyticsMetrics metrics,
    TimeProvider clock,
    ILogger<AnalysisRunner> logger)
{
    private const long LockKey = 0x414E414C59;
    private static readonly TimeSpan StaleRun = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TrackFirstsEvery = TimeSpan.FromMinutes(5);

    public sealed record RunResult(bool Locked, int Scanned, int Indexed, long Watermark, long Backlog);
    public long Backlog { get; private set; }
    private AnalyticsOptions O => options.Value;

    public async Task<RunResult> RunOnceAsync(CancellationToken ct)
    {
        await using var lockDb = await factory.CreateDbContextAsync(ct);
        await lockDb.Database.OpenConnectionAsync(ct);
        var locked = (await lockDb.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").ToListAsync(ct)).First();
        if (!locked)
        {
            logger.LogWarning("Another analytics instance holds the lock; skipping this run");
            return new RunResult(false, 0, 0, 0, Backlog);
        }
        try { return await RunLockedAsync(ct); }
        finally
        {
            await lockDb.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            await lockDb.Database.CloseConnectionAsync();
        }
    }

    private async Task<RunResult> RunLockedAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = clock.GetUtcNow();
        long runId;
        long watermarkFrom;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE analytics.runs SET status = {(int)RunStatus.Failed}, finished_at = {now}, error = 'перервано: прогін не оновлювався понад 10 хв (крах інстансу?)'
                WHERE status = {(int)RunStatus.Running} AND updated_at < {now - StaleRun}
                """, ct);
            watermarkFrom = await WatermarkAsync(db, ct);
            var run = new AnalysisRun { Instance = O.Name, StartedAt = now, UpdatedAt = now, Status = RunStatus.Running, WatermarkFrom = watermarkFrom, WatermarkTo = watermarkFrom };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            runId = run.RunId;
        }

        var scanned = 0;
        var indexed = 0;
        var watermark = watermarkFrom;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await ProcessBatchAsync(runId, ct);
                if (batch is null) break;
                scanned += batch.Scanned;
                indexed += batch.Indexed;
                watermark = batch.Watermark;
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            await RefreshTrackFirstsAsync(db, scanned > 0, ct);
            Backlog = await RawMessageReader.MaxIdAsync(db, ct) - watermark;
            metrics.Backlog = Backlog;
            var finished = clock.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE analytics.runs SET status = {(int)RunStatus.Ok}, finished_at = {finished}, updated_at = {finished}, watermark_to = {watermark},
                    messages_scanned = {scanned}, messages_fingerprinted = {indexed}
                WHERE run_id = {runId}
                """, ct);
            metrics.RunFinished(sw.Elapsed);
            if (scanned > 0)
                logger.LogInformation("Analytics run {Run}: {Scanned} message(s), {Indexed} indexed in {Ms} ms; backlog {Backlog}", runId, scanned, indexed, sw.ElapsedMilliseconds, Backlog);
            return new RunResult(true, scanned, indexed, watermark, Backlog);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (!cancelled) logger.LogError(ex, "Analytics run {Run} failed", runId);
            try
            {
                await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
                var finished = clock.GetUtcNow();
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE analytics.runs SET status = {(int)RunStatus.Failed}, finished_at = {finished}, updated_at = {finished}, watermark_to = {watermark},
                        messages_scanned = {scanned}, messages_fingerprinted = {indexed}, error = {(cancelled ? "зупинено" : Truncate(ex.ToString(), 4000))}
                    WHERE run_id = {runId}
                    """, CancellationToken.None);
            }
            catch (Exception inner) { logger.LogError(inner, "Could not record the failure of analytics run {Run}", runId); }
            throw;
        }
    }

    private sealed record BatchResult(int Scanned, int Indexed, long Watermark);

    private async Task<BatchResult?> ProcessBatchAsync(long runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var watermark = await WatermarkAsync(db, ct);
        var now = clock.GetUtcNow();
        var rows = await RawMessageReader.BatchAsync(db, watermark, (now - O.SafetyLag).UtcDateTime, O.BatchSize, ct);
        if (rows.Count == 0) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var indexed = 0;
        foreach (var row in rows)
            if (await IndexAsync(db, row, now, ct)) indexed++;
        var newWatermark = rows[^1].RawMessageId;
        await SetStateAsync(db, AnalyticsState.WatermarkKey, newWatermark.ToString(CultureInfo.InvariantCulture), now, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE analytics.runs SET updated_at = {clock.GetUtcNow()}, watermark_to = {newWatermark},
                messages_scanned = messages_scanned + {rows.Count}, messages_fingerprinted = messages_fingerprinted + {indexed}
            WHERE run_id = {runId}
            """, ct);
        await tx.CommitAsync(ct);
        metrics.MessagesIndexed(rows.Count);
        return new BatchResult(rows.Count, indexed, newWatermark);
    }

    private static async Task<bool> IndexAsync(AnalyticsDbContext db, RawRow row, DateTimeOffset now, CancellationToken ct)
    {
        var (postKey, isEdit) = row.Post();
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.messages (raw_message_id, source_id, post_key, is_edit, published_at, indexed_at)
            VALUES ({row.RawMessageId}, {row.SourceId}, {postKey}, {isEdit}, {new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAt, DateTimeKind.Utc))}, {now})
            ON CONFLICT (raw_message_id) DO NOTHING
            """, ct);
        return inserted > 0;
    }

    private async Task RefreshTrackFirstsAsync(AnalyticsDbContext db, bool changed, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var last = await db.State.AsNoTracking().Where(s => s.Key == AnalyticsState.TrackFirstsAtKey).Select(s => s.UpdatedAt).FirstOrDefaultAsync(ct);
        var age = now - last;
        if (!(changed && age > TrackFirstsEvery) && age < 2 * TrackFirstsEvery) return;
        var rows = await TrackFirstsBuilder.RebuildAsync(db, O.TrackFirstsDays, now, ct);
        await SetStateAsync(db, AnalyticsState.TrackFirstsAtKey, now.ToString("O"), now, ct);
        logger.LogDebug("track_firsts rebuilt: {Rows} row(s) over {Days} days", rows, O.TrackFirstsDays);
    }

    public static async Task<long> WatermarkAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var value = await db.State.AsNoTracking().Where(s => s.Key == AnalyticsState.WatermarkKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return long.TryParse(value, CultureInfo.InvariantCulture, out var w) ? w : 0;
    }

    public static Task<int> SetStateAsync(AnalyticsDbContext db, string key, string value, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.state (key, value, updated_at) VALUES ({key}, {value}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
