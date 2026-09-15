using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Seeding;

/// <summary>
/// Plan §8.2 batch backfill: fills targets.event_kind_id from the legacy event_type through <see cref="EventKindLegacyMap"/>.
/// Runs as the last seeder (after event_kinds exist) and from maintenance; idempotent (only NULL rows), rerunnable,
/// batched by target_id so no long transaction holds the table. Each batch is its own transaction under the Store
/// advisory lock — the P00 rule for every writer of derived rows — so it serialises with processors' store phase for
/// milliseconds, not for the whole run. Never touches event_type, evidence or derived state; the UPDATE fires no
/// trigger (trg_targets_duplicate is `UPDATE OF duplicate_of_target_id` only). Rows whose enum value has no mapping
/// stay NULL and are reported as unresolved rather than guessed.
/// </summary>
public sealed class EventKindBackfill(IOptions<SeedOptions> options, ILogger<EventKindBackfill> logger) : ISeeder
{
    public const int DefaultBatchSize = 5_000;

    public int Order => 90;

    public async Task SeedAsync(PulujDbContext db, CancellationToken ct)
    {
        if (!options.Value.BackfillEventKinds)
        {
            return;
        }
        var report = await RunAsync(db, DefaultBatchSize, ct);
        logger.LogInformation("Event kind backfill: {Total} targets, {Before} without kind before, {Updated} updated in {Batches} batch(es) ({Ms} ms), {Unresolved} unresolved",
            report.TotalTargets, report.NullBefore, report.Updated, report.Batches, report.ElapsedMs, report.NullAfter);
        foreach (var (eventType, count) in report.UnresolvedByEventType)
        {
            logger.LogWarning("Event kind backfill: {Count} target(s) with event_type {EventType} have no catalog mapping", count, eventType);
        }
    }

    /// <summary>Backfills every NULL event_kind_id the map can resolve; returns counts for the evidence report.</summary>
    public static async Task<BackfillReport> RunAsync(PulujDbContext db, int batchSize, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = await db.Targets.LongCountAsync(ct);
        var nullBefore = await db.Targets.LongCountAsync(t => t.EventKindId == null, ct);
        var updated = 0L;
        var batches = 0;
        if (nullBefore > 0)
        {
            // Only rows the map can resolve bound the scan: an unmapped enum value stays NULL forever and must not
            // widen the range on every start.
            var mapped = EventKindLegacyMap.All.Select(kv => kv.Key).ToArray();
            var pending = db.Targets.Where(t => t.EventKindId == null && mapped.Contains(t.EventType));
            if (!await pending.AnyAsync(ct))
            {
                return await ReportAsync(db, total, nullBefore, 0, 0, sw, ct);
            }
            var minId = await pending.MinAsync(t => t.TargetId, ct);
            var maxId = await pending.MaxAsync(t => t.TargetId, ct);
            var previousTimeout = db.Database.GetCommandTimeout();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            try
            {
                for (var from = minId; from <= maxId; from += batchSize)
                {
                    var to = Math.Min(from + batchSize - 1, maxId);
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.Database.ExecuteSqlInterpolatedAsync(AdvisoryLocks.Take(AdvisoryLocks.Store), ct);
                    updated += await db.Database.ExecuteSqlRawAsync(UpdateSql, [new NpgsqlParameter("from", from), new NpgsqlParameter("to", to)], ct);
                    await tx.CommitAsync(ct);
                    batches++;
                }
            }
            finally
            {
                db.Database.SetCommandTimeout(previousTimeout);
            }
        }
        return await ReportAsync(db, total, nullBefore, updated, batches, sw, ct);
    }

    private static async Task<BackfillReport> ReportAsync(PulujDbContext db, long total, long nullBefore, long updated, int batches, System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        var nullAfter = await db.Targets.LongCountAsync(t => t.EventKindId == null, ct);
        var unresolved = await db.Targets.Where(t => t.EventKindId == null)
            .GroupBy(t => t.EventType).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var perKind = await db.Targets.Where(t => t.EventKindId != null)
            .GroupBy(t => t.EventKind!.Code).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
        return new BackfillReport(total, nullBefore, updated, batches, nullAfter, sw.ElapsedMilliseconds,
            unresolved.ToDictionary(x => (int)x.Key, x => x.Count),
            perKind.ToDictionary(x => x.Key, x => x.Count, StringComparer.Ordinal));
    }

    /// <summary>The legacy → code CASE, generated from the map so SQL and C# cannot drift. Codes are compile-time constants, not user input.</summary>
    public static readonly string UpdateSql =
        "UPDATE targets t SET event_kind_id = k.event_kind_id FROM event_kinds k " +
        "WHERE t.event_kind_id IS NULL AND t.target_id BETWEEN @from AND @to AND k.code = CASE t.event_type " +
        string.Concat(EventKindLegacyMap.All.Select(kv => $"WHEN {(int)kv.Key} THEN '{kv.Value}' ")) + "END";

    public sealed record BackfillReport(
        long TotalTargets, long NullBefore, long Updated, int Batches, long NullAfter, long ElapsedMs,
        IReadOnlyDictionary<int, long> UnresolvedByEventType, IReadOnlyDictionary<string, long> PerKind)
    {
        /// <summary>Share of targets that carry a catalog kind after the run.</summary>
        public double Coverage => TotalTargets == 0 ? 1 : (double)(TotalTargets - NullAfter) / TotalTargets;
    }
}
