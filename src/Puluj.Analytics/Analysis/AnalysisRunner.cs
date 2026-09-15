using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Puluj.Analytics.Persistence;
using Puluj.Analytics.Text;

namespace Puluj.Analytics.Analysis;

/// <summary>
/// One run = drain everything above the watermark in batches, then refresh the track statistics. Each batch is one
/// transaction (index rows, pairs, watermark), so a crash replays the batch without side effects; the watermark is
/// re-read from the database before every batch, so a reset from the admin panel takes effect at the next batch.
/// A session-level advisory lock on a dedicated connection keeps two instances from running at once.
/// </summary>
public sealed class AnalysisRunner(
    IDbContextFactory<AnalyticsDbContext> factory,
    CopyDetector detector,
    IOptions<AnalyticsOptions> options,
    AnalyticsMetrics metrics,
    TimeProvider clock,
    ILogger<AnalysisRunner> logger)
{
    private const long LockKey = 0x414E414C59; // "ANALY"
    private static readonly TimeSpan StaleRun = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TrackFirstsEvery = TimeSpan.FromMinutes(5);

    private Dictionary<long, int>? _channels;

    public sealed record RunResult(bool Locked, int Scanned, int Fingerprinted, int Pairs, long Watermark, long Backlog);

    /// <summary>Raw messages above the watermark after the last run (for the gauge and the status page).</summary>
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
            return new RunResult(false, 0, 0, 0, 0, Backlog);
        }
        try
        {
            return await RunLockedAsync(ct);
        }
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
            _channels ??= await RawMessageReader.ChannelMapAsync(db, ct);
        }

        int scanned = 0, fingerprinted = 0, pairs = 0;
        var watermark = watermarkFrom;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await ProcessBatchAsync(runId, ct);
                if (batch is null)
                {
                    break;
                }
                scanned += batch.Scanned;
                fingerprinted += batch.Fingerprinted;
                pairs += batch.Pairs;
                watermark = batch.Watermark;
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            await RefreshTrackFirstsAsync(db, scanned > 0, ct);
            Backlog = await RawMessageReader.MaxIdAsync(db, ct) - watermark;
            metrics.Backlog = Backlog;
            var finished = clock.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE analytics.runs SET status = {(int)RunStatus.Ok}, finished_at = {finished}, updated_at = {finished}, watermark_to = {watermark},
                    messages_scanned = {scanned}, messages_fingerprinted = {fingerprinted}, pairs_found = {pairs}
                WHERE run_id = {runId}
                """, ct);
            metrics.RunFinished(sw.Elapsed);
            if (scanned > 0)
            {
                logger.LogInformation("Analytics run {Run}: {Scanned} message(s), {Fingerprinted} fingerprinted, {Pairs} pair(s) in {Ms} ms; backlog {Backlog}", runId, scanned, fingerprinted, pairs, sw.ElapsedMilliseconds, Backlog);
            }
            return new RunResult(true, scanned, fingerprinted, pairs, watermark, Backlog);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (!cancelled)
            {
                logger.LogError(ex, "Analytics run {Run} failed", runId);
            }
            try
            {
                await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
                var finished = clock.GetUtcNow();
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE analytics.runs SET status = {(int)RunStatus.Failed}, finished_at = {finished}, updated_at = {finished}, watermark_to = {watermark},
                        messages_scanned = {scanned}, messages_fingerprinted = {fingerprinted}, pairs_found = {pairs}, error = {(cancelled ? "зупинено" : Truncate(ex.ToString(), 4000))}
                    WHERE run_id = {runId}
                    """, CancellationToken.None);
            }
            catch (Exception inner)
            {
                logger.LogError(inner, "Could not record the failure of analytics run {Run}", runId);
            }
            throw;
        }
    }

    private sealed record BatchResult(int Scanned, int Fingerprinted, int Pairs, long Watermark);

    /// <summary>One transaction over the next batch; null when there is nothing above the watermark (yet).</summary>
    private async Task<BatchResult?> ProcessBatchAsync(long runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var watermark = await WatermarkAsync(db, ct);
        var now = clock.GetUtcNow();
        var rows = await RawMessageReader.BatchAsync(db, watermark, (now - O.SafetyLag).UtcDateTime, O.BatchSize, ct);
        if (rows.Count == 0)
        {
            return null;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int fingerprinted = 0, pairs = 0;
        var touched = new HashSet<(int SourceId, string PostKey)>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RawText))
            {
                continue; // structured payloads (alerts) have no text to compare
            }
            var message = await IndexAsync(db, row, now, ct);
            if (message.Fingerprint.Indexed)
            {
                fingerprinted++;
            }
            // Parsed events are correlatable even when there is no text at all.
            foreach (var match in await detector.FindAsync(db, message.Row, message.Fingerprint, ct))
            {
                var pair = detector.Pair(message.Row, match, now);
                await UpsertPairAsync(db, pair, ct);
                metrics.PairFound(pair.Kind);
                touched.Add((pair.CopySourceId, pair.CopyPostKey));
                pairs++;
            }
        }
        foreach (var (sourceId, postKey) in touched)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE analytics.copies c
                SET is_primary = (c.original_published_at, c.original_raw_message_id) = (
                    SELECT f.original_published_at, f.original_raw_message_id FROM analytics.copies f
                    WHERE f.copy_source_id = {sourceId} AND f.copy_post_key = {postKey}
                    ORDER BY f.original_published_at, f.original_raw_message_id LIMIT 1)
                WHERE c.copy_source_id = {sourceId} AND c.copy_post_key = {postKey}
                """, ct);
        }
        var newWatermark = rows[^1].RawMessageId;
        await SetStateAsync(db, AnalyticsState.WatermarkKey, newWatermark.ToString(CultureInfo.InvariantCulture), now, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE analytics.runs SET updated_at = {clock.GetUtcNow()}, watermark_to = {newWatermark},
                messages_scanned = messages_scanned + {rows.Count}, messages_fingerprinted = messages_fingerprinted + {fingerprinted}, pairs_found = pairs_found + {pairs}
            WHERE run_id = {runId}
            """, ct);
        await tx.CommitAsync(ct);
        metrics.MessagesIndexed(rows.Count);
        return new BatchResult(rows.Count, fingerprinted, pairs, newWatermark);
    }

    private sealed record Indexed(MessageFingerprint Row, TextFingerprint Fingerprint);

    private async Task<Indexed> IndexAsync(AnalyticsDbContext db, RawRow row, DateTimeOffset now, CancellationToken ct)
    {
        var fp = TextFingerprint.Of(row.RawText, O.MinTextLength);
        var (postKey, isEdit) = row.Post();
        if (row.ChannelId is { } channel)
        {
            _channels![channel] = row.SourceId;
        }
        int? forwardedSource = RawMessageReader.ForwardedChannelId(row.ForwardedFrom) is { } fwd && _channels!.TryGetValue(fwd, out var s) ? s : null;
        var ownForward = forwardedSource == row.SourceId; // a channel forwarding its own post is neither a copy nor an external forward
        if (ownForward)
        {
            forwardedSource = null;
        }
        var m = new MessageFingerprint
        {
            RawMessageId = row.RawMessageId,
            SourceId = row.SourceId,
            PostKey = postKey,
            IsEdit = isEdit,
            PublishedAt = new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAt, DateTimeKind.Utc)),
            ChannelId = row.ChannelId,
            ForwardedFrom = row.ForwardedFrom is { Length: > 128 } f ? f[..128] : row.ForwardedFrom,
            ForwardedSourceId = forwardedSource,
            ForwardedExternal = row.ForwardedFrom is not null && forwardedSource is null && !ownForward,
            TextLength = fp.Canonical.Length,
            ShingleCount = fp.Shingles.Count,
            // Legacy columns stay null. Semantic candidates are read from parsed targets, never text LSH bands.
            MinHash = null,
            Bands = null,
            IndexedAt = now,
        };
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.messages (raw_message_id, source_id, post_key, is_edit, published_at, channel_id, forwarded_from, forwarded_source_id, forwarded_external,
                                            text_length, shingle_count, min_hash, bands, indexed_at)
            VALUES ({m.RawMessageId}, {m.SourceId}, {m.PostKey}, {m.IsEdit}, {m.PublishedAt}, {m.ChannelId}, {m.ForwardedFrom}, {m.ForwardedSourceId}, {m.ForwardedExternal},
                    {m.TextLength}, {m.ShingleCount}, {m.MinHash}, {m.Bands}, {m.IndexedAt})
            ON CONFLICT (raw_message_id) DO NOTHING
            """, ct);
        return new Indexed(m, fp);
    }

    private static Task<int> UpsertPairAsync(AnalyticsDbContext db, MessageCopy p, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.copies (copy_source_id, copy_post_key, original_source_id, original_post_key, copy_raw_message_id, original_raw_message_id,
                                          copy_published_at, original_published_at, delay_seconds, jaccard, containment, kind, is_primary, found_at)
            VALUES ({p.CopySourceId}, {p.CopyPostKey}, {p.OriginalSourceId}, {p.OriginalPostKey}, {p.CopyRawMessageId}, {p.OriginalRawMessageId},
                    {p.CopyPublishedAt}, {p.OriginalPublishedAt}, {p.DelaySeconds}, {p.Jaccard}, {p.Containment}, {(int)p.Kind}, false, {p.FoundAt})
            ON CONFLICT (copy_source_id, copy_post_key, original_source_id, original_post_key) DO UPDATE SET
                copy_raw_message_id = EXCLUDED.copy_raw_message_id, original_raw_message_id = EXCLUDED.original_raw_message_id,
                copy_published_at = EXCLUDED.copy_published_at, original_published_at = EXCLUDED.original_published_at, delay_seconds = EXCLUDED.delay_seconds,
                jaccard = greatest(copies.jaccard, EXCLUDED.jaccard), containment = greatest(copies.containment, EXCLUDED.containment),
                kind = greatest(copies.kind, EXCLUDED.kind)
            """, ct);

    private async Task RefreshTrackFirstsAsync(AnalyticsDbContext db, bool changed, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var last = await db.State.AsNoTracking().Where(s => s.Key == AnalyticsState.TrackFirstsAtKey).Select(s => s.UpdatedAt).FirstOrDefaultAsync(ct);
        var age = now - last;
        if (!(changed && age > TrackFirstsEvery) && age < 2 * TrackFirstsEvery)
        {
            return;
        }
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
            """, ct);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
