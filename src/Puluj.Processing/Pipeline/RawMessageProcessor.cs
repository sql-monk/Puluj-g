using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Notifications;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Indexes;
using Puluj.Processing.Parsing;
using Puluj.Processing.Structured;
using Puluj.Processing.Text;

namespace Puluj.Processing.Pipeline;

/// <summary>Runs after targets of one RawMessage are saved (deduplication, correlation). Implemented in the correlation stage.</summary>
public interface ITargetSink
{
    /// <summary>Runs inside the message transaction; events added to <paramref name="events"/> are published after commit.</summary>
    Task OnTargetsAsync(PulujDbContext db, IReadOnlyList<Target> targets, Source source, ICollection<PulujEvent> events, CancellationToken ct);
}

/// <summary>
/// Processes one RawMessage end-to-end (spec §4): structured payload or text -> facts -> targets -> sink.
/// All writes for a message happen in one transaction that holds the raw_messages row lock from its first statement:
/// every status decision (process, skip, fail) is made under that lock, so an expired-claim sweep or another instance
/// that re-claimed the row waits and then sees what this transaction decided. Parsing runs in parallel across workers
/// and instances. Derived reads and writes, including SQL trigger effects, run under Store through commit.
/// Messages without facts only update their raw row. Failures are recorded in ProcessingError and retried up to MaxAttempts;
/// a transient database failure (a deadlock with a writer that does not hold the store lock, a serialization failure)
/// is retried without counting, up to MaxTransientRetries per message.
/// </summary>
public sealed class RawMessageProcessor(
    IDbContextFactory<PulujDbContext> factory,
    INormalizer normalizer,
    IParser parser,
    IIndexes indexes,
    TargetBuilder builder,
    AlertsInUaHandler alertsHandler,
    IEnumerable<ITargetSink> sinks,
    INotifyPublisher notifier,
    IOptions<ProcessingOptions> options,
    ProcessorIdentity identity,
    PulujMetrics metrics,
    ProcessingStats stats,
    TimeProvider clock,
    ILogger<RawMessageProcessor> logger)
{
    private const string Savepoint = "message";
    private readonly ConcurrentDictionary<long, int> _transientRetries = new();
    /// <summary>Once per process: a message without a catalog kind is expected only until the seeder ran (plan 8.2).</summary>
    private int _kindWarningLogged;

    /// <summary>
    /// Processes the message if it is Pending (a direct call: tests, a dev scenario) or InProgress and claimed by this
    /// instance (the processing loop); anything else — taken by another instance, already done — is skipped with 0.
    /// </summary>
    public async Task<int> ProcessAsync(long rawMessageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // NO KEY UPDATE, not FOR UPDATE: the same fence against claims, sweeps and other instances (their UPDATEs wait),
        // but a row referencing this message from another connection while it is being processed — the LLM audit row,
        // written outside this transaction so it survives a rollback — only needs KEY SHARE for its FK check, and
        // FOR UPDATE would make that insert wait for this transaction, which is itself waiting for the parse.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT raw_message_id FROM raw_messages WHERE raw_message_id = {rawMessageId} FOR NO KEY UPDATE", ct);
        var raw = await db.RawMessages.Include(r => r.Source).FirstOrDefaultAsync(r => r.RawMessageId == rawMessageId, ct);
        if (raw is null || !OwnedByMe(raw))
        {
            return 0;
        }
        var source = raw.Source!;
        var sw = Stopwatch.StartNew();
        await tx.CreateSavepointAsync(Savepoint, ct);
        try
        {
            List<Target> targets;
            long parsedMs, lockedMs;
            if (AlertsInUaHandler.CanHandle(raw))
            {
                parsedMs = 0;
                // The handler already reads and modifies AirAlerts; locking after it is too late.
                await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store), ct);
                lockedMs = sw.ElapsedMilliseconds;
                targets = await alertsHandler.HandleAsync(db, raw, source, ct);
            }
            else if (!string.IsNullOrWhiteSpace(raw.RawText))
            {
                targets = await ParseTextAsync(raw, source, ct);
                parsedMs = sw.ElapsedMilliseconds;
                if (targets.Count > 0)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store), ct);
                }
                lockedMs = sw.ElapsedMilliseconds;
            }
            else
            {
                raw.ProcessingStatus = ProcessingStatus.Skipped;
                raw.ProcessedAt = clock.GetUtcNow();
                Stamp(raw);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                metrics.RawProcessed(identity.Name, "skipped");
                stats.Outcome("skipped", raw.RawMessageId);
                return 0;
            }

            // Most incoming posts yield no facts. They only update their own raw row, so neither correlation nor
            // alerts can observe them and they must not wait behind a message that does have derived state to write.
            if (targets.Count == 0)
            {
                raw.ProcessingStatus = ProcessingStatus.Processed;
                raw.ProcessedAt = clock.GetUtcNow();
                raw.Attempts++;
                Stamp(raw);
                raw.ProcessingMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                _transientRetries.TryRemove(raw.RawMessageId, out _);
                metrics.ParserUnmatched(source.Code);
                metrics.ProcessingStage("parse", parsedMs);
                metrics.ProcessingStage("lock", 0);
                metrics.ProcessingStage("store", sw.ElapsedMilliseconds - parsedMs);
                metrics.RawProcessed(identity.Name, "processed");
                stats.Outcome("processed", raw.RawMessageId);
                stats.Record("parse", parsedMs);
                stats.Record("lock", 0);
                stats.Record("store", sw.ElapsedMilliseconds - parsedMs);
                stats.Record("total", sw.ElapsedMilliseconds);
                logger.LogInformation("RawMessage {Id} ({Source}): no target(s) in {Ms} ms", raw.RawMessageId, source.Code, sw.ElapsedMilliseconds);
                return 0;
            }

            // Plan §8.2 compatibility window: every target carries the catalog kind next to the legacy enum. One catalog
            // snapshot per message; an empty catalog (not seeded yet) leaves the column NULL for the backfill, never a guess.
            var kinds = indexes.EventKinds;
            if (kinds.Stamp(targets) < targets.Count && Interlocked.Exchange(ref _kindWarningLogged, 1) == 0)
            {
                logger.LogWarning("RawMessage {Id}: {Count} target(s) without an event kind (catalog has {Kinds} kinds); further occurrences are not logged, the backfill fills them", raw.RawMessageId, targets.Count(t => t.EventKindId is null), kinds.Count);
            }
            db.Targets.AddRange(targets);
            raw.ProcessingStatus = ProcessingStatus.Processed;
            raw.ProcessedAt = clock.GetUtcNow();
            raw.Attempts++;
            Stamp(raw);
            await db.SaveChangesAsync(ct);

            var events = new List<PulujEvent>();
            foreach (var sink in sinks)
            {
                await sink.OnTargetsAsync(db, targets, source, events, ct);
            }
            raw.ProcessingMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue); // up to here: without the commit and NOTIFY
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var totalMs = sw.ElapsedMilliseconds;
            var storeMs = totalMs - lockedMs;
            _transientRetries.TryRemove(raw.RawMessageId, out _);
            // Lock wait close to store time means the store lock is the ceiling: workers spend their time waiting for it.
            logger.LogDebug("RawMessage {Id}: parse {ParseMs} ms, lock wait {LockMs} ms, store + sinks {SinkMs} ms", raw.RawMessageId, parsedMs, lockedMs - parsedMs, storeMs);
            metrics.ProcessingStage("parse", parsedMs);
            metrics.ProcessingStage("lock", lockedMs - parsedMs);
            metrics.ProcessingStage("store", storeMs);
            stats.Record("parse", parsedMs);
            stats.Record("lock", lockedMs - parsedMs);
            stats.Record("store", storeMs);
            stats.Record("total", totalMs);
            foreach (var evt in events)
            {
                await notifier.PublishAsync(evt, ct);
            }

            foreach (var o in targets)
            {
                metrics.TargetCreated(source.Code, o.IdentificationMethod.ToString());
            }
            metrics.RawProcessed(identity.Name, "processed");
            stats.Outcome("processed", raw.RawMessageId);
            logger.LogInformation("RawMessage {Id} ({Source}): {Count} target(s) in {Ms} ms", raw.RawMessageId, source.Code, targets.Count, sw.ElapsedMilliseconds);
            return targets.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(db, tx, raw, ex);
            return 0;
        }
    }

    private bool OwnedByMe(RawMessage raw) =>
        raw.ProcessingStatus == ProcessingStatus.Pending
        || (raw.ProcessingStatus == ProcessingStatus.InProgress && raw.ClaimedBy == identity.Name);

    /// <summary>Provenance: which instance handled the message. A direct call on a Pending row claims it here.</summary>
    private void Stamp(RawMessage raw)
    {
        if (raw.ClaimedBy != identity.Name)
        {
            raw.ClaimedBy = identity.Name;
            raw.ClaimedAt = clock.GetUtcNow();
        }
    }

    private async Task<List<Target>> ParseTextAsync(RawMessage raw, Source source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var normalized = normalizer.Normalize(raw.RawText!);
        var normalizeMs = sw.ElapsedMilliseconds;
        var ctx = new ParseContext(source.SourceId, normalized.Language, HomeRegionOf(source, normalizer, indexes), raw.PublishedAt, raw.RawMessageId, source.Code);
        var facts = await parser.ParseAsync(normalized, ctx, ct);
        var parseMs = sw.ElapsedMilliseconds - normalizeMs;
        var targets = facts.Select(f => builder.Build(f, raw, source, f.ParserVersion, f.Method, normalized.Language)).ToList();
        logger.LogDebug("RawMessage {Id}: normalize {NormalizeMs} ms, rules {RulesMs} ms, build {BuildMs} ms", raw.RawMessageId, normalizeMs, parseMs, sw.ElapsedMilliseconds - normalizeMs - parseMs);
        return targets;
    }

    /// <summary>Source config may name a home region ("Київська область") or give a place id; used to disambiguate settlement names. Shared with the parser stage (P05).</summary>
    internal static int? HomeRegionOf(Source source, INormalizer normalizer, IIndexes indexes)
    {
        if (source.Config is null)
        {
            return null;
        }
        var root = source.Config.RootElement;
        if (root.TryGetProperty("homeRegionPlaceId", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            return idEl.GetInt32();
        }
        if (root.TryGetProperty("homeRegion", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && nameEl.GetString() is { Length: > 0 } name)
        {
            var n = normalizer.Normalize(name);
            if (n.Segments.Count > 0)
            {
                var match = new PlaceMatcher(indexes.Gazetteer)
                    .Match(n.Segments[0], new ParseContext(0, "uk", null), new HashSet<int>(), new HashSet<(int, int)>())
                    .FirstOrDefault();
                return match is null ? null : (indexes.Gazetteer.RegionOf(match.Place) ?? match.Place).PlaceId;
            }
        }
        return null;
    }

    /// <summary>
    /// Rolls the message's work back to the savepoint and, still under the row lock, counts the attempt: back to Pending
    /// for another try by any instance, or Failed after MaxAttempts. The attempt count cannot be lost to a concurrent
    /// writer because nobody else can touch the row until this commits.
    /// </summary>
    private async Task RecordFailureAsync(PulujDbContext db, IDbContextTransaction tx, RawMessage raw, Exception ex)
    {
        var transient = TransientCause(ex);
        if (transient is not null && _transientRetries.AddOrUpdate(raw.RawMessageId, 1, (_, n) => n + 1) > options.Value.MaxTransientRetries)
        {
            transient = null; // keeps colliding: from here on it is a failure like any other
        }
        var attempts = transient is null ? raw.Attempts + 1 : raw.Attempts;
        var failed = transient is null && attempts >= options.Value.MaxAttempts;
        var now = clock.GetUtcNow();
        if (transient is null)
        {
            metrics.ProcessingError("process");
            logger.LogError(ex, "RawMessage {Id} failed", raw.RawMessageId);
        }
        else
        {
            metrics.ProcessingError("transient");
            logger.LogWarning("RawMessage {Id}: transient database failure {SqlState} ({Message}); back to Pending, attempt not counted", raw.RawMessageId, transient.SqlState, transient.MessageText);
        }
        try
        {
            await tx.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            db.ChangeTracker.Clear();
            if (transient is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE raw_messages SET processing_status = {(int)ProcessingStatus.Pending}, claimed_by = NULL, claimed_at = NULL WHERE raw_message_id = {raw.RawMessageId}",
                    CancellationToken.None);
            }
            else if (failed)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE raw_messages SET attempts = {attempts}, processing_status = {(int)ProcessingStatus.Failed}, processed_at = {now}, claimed_by = {identity.Name}, claimed_at = coalesce(claimed_at, {now}) WHERE raw_message_id = {raw.RawMessageId}",
                    CancellationToken.None);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE raw_messages SET attempts = {attempts}, processing_status = {(int)ProcessingStatus.Pending}, claimed_by = NULL, claimed_at = NULL WHERE raw_message_id = {raw.RawMessageId}",
                    CancellationToken.None);
            }
            db.ProcessingErrors.Add(new ProcessingError
            {
                RawMessageId = raw.RawMessageId,
                SourceId = raw.SourceId,
                Stage = transient is null ? "process" : "transient",
                Message = transient is null ? ex.Message : $"{transient.SqlState}: {transient.MessageText}",
                Exception = transient is null ? ex.ToString() : null,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            var outcome = transient is not null ? "retried_transient" : failed ? "failed" : "retried";
            metrics.RawProcessed(identity.Name, outcome);
            stats.Outcome(outcome, raw.RawMessageId);
        }
        catch (Exception inner)
        {
            // The transaction is disposed by the caller (rollback): the row stays InProgress and the lease sweep returns it.
            logger.LogError(inner, "Could not record processing error for RawMessage {Id}", raw.RawMessageId);
        }
    }

    /// <summary>The PostgreSQL error behind the exception when it is one another try can fix (40001, 40P01, 55P03, connection loss).</summary>
    internal static PostgresException? TransientCause(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                return pg.IsTransient ? pg : null;
            }
        }
        return null;
    }

}
