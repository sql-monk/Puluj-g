using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Messaging;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Correlation;
using Puluj.Processing.Indexes;
using Puluj.Processing.Stages;
using Puluj.Processing.Structured;

namespace Puluj.Processing.Writers;

/// <summary>
/// P09 replacement of the legacy <see cref="TrackWatchdog"/> for the platform path (role `watchdog`): it owns no
/// state. Every sweep asks the owners to expire what looks stale — `track.expiry.requested` / `alert.expiry.requested`
/// commands through the outbox, each carrying the aggregate's `expected_revision`, the computed `expire_at` and the
/// `watermark` the decision was made with. The owner re-checks revision and time (ADR-0004 §6.2); a stale command is a
/// noop. The watermark is history-aware: while message-scoped events are still in flight (unconfirmed outbox rows,
/// deliveries without a receipt) older than 30 minutes, "now" is the oldest of them,
/// so a history load never expires the tracks of the messages still to come.
/// </summary>
public sealed class DomainWatchdog(
    IDbContextFactory<PulujDbContext> factory,
    OutboxWriter outbox,
    IndexProvider indexes,
    IOptionsMonitor<CorrelationOptions> options,
    TimeProvider clock,
    ILogger<DomainWatchdog> logger) : BackgroundService
{
    public const string Producer = "watchdog";
    public const string TrackCommand = "track.expiry.requested";
    public const string AlertCommand = "alert.expiry.requested";
    public static readonly Guid Namespace = new("6d4e0f1a-9c2b-4b1e-8f3a-2c9e7b5a1d33");
    public static readonly TimeSpan BacklogTolerance = TimeSpan.FromMinutes(30);

    /// <summary>(aggregate, revision) already commanded by this process: a track stays in the candidate set until the owner acts, and must not be commanded every sweep.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);

    public string Instance { get; set; } = Producer;

    /// <summary>Forget what was commanded (tests; a process restart does the same — the owner's revision check absorbs the one extra command).</summary>
    public void ResetMemo() => _sent.Clear();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await indexes.Ready.WaitAsync(ct);
        using var timer = new PeriodicTimer(options.CurrentValue.WatchdogInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Domain watchdog sweep failed");
            }
        }
    }

    public sealed record Sweep(DateTimeOffset Watermark, int TrackCommands, int AlertCommands, int Skipped);

    public async Task<Sweep> SweepAsync(CancellationToken ct)
    {
        var wall = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        var watermark = await WatermarkAsync(conn, wall, ct);
        var candidates = new List<(long TrackId, int Revision, DateTimeOffset ExpireAt, Guid? Cause, Guid? Correlation)>();
        await using (var q = new NpgsqlCommand("SELECT target_track_id, revision, last_seen_at, target_class_id, last_event_id, last_correlation_id FROM target_tracks WHERE status = @active", conn))
        {
            q.Parameters.AddWithValue("active", (int)TrackStatus.Active);
            await using var reader = await q.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var classId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                var window = indexes.Taxonomy.ClassProfile(classId)?.CorrelationWindowMinutes ?? 30;
                var closeAt = reader.GetFieldValue<DateTimeOffset>(2) + TimeSpan.FromMinutes(window * options.CurrentValue.CloseAfterWindows);
                if (closeAt <= watermark)
                {
                    candidates.Add((reader.GetInt64(0), reader.GetInt32(1), closeAt, reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
                }
            }
        }
        var alerts = new List<(long AlertId, int Revision, DateTimeOffset ExpireAt, Guid? Cause, Guid? Correlation)>();
        var staleBefore = watermark - TextAlertSink.MaxAge;
        await using (var q = new NpgsqlCommand("SELECT air_alert_id, revision, started_at, last_event_id, last_correlation_id FROM air_alerts WHERE ended_at IS NULL AND source_alert_id LIKE @prefix AND started_at < @before", conn))
        {
            q.Parameters.AddWithValue("prefix", TextAlertSink.KeyPrefix + "%");
            q.Parameters.AddWithValue("before", staleBefore);
            await using var reader = await q.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                alerts.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2) + TextAlertSink.MaxAge, reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4)));
            }
        }
        if (candidates.Count == 0 && alerts.Count == 0)
        {
            return new Sweep(watermark, 0, 0, 0);
        }

        var sentTracks = 0;
        var sentAlerts = 0;
        var skipped = 0;
        var sentNow = new List<string>(); // the memo is written only after the commit (a failed sweep must not poison it, review N4)
        await using var tx = await conn.BeginTransactionAsync(ct);
        var runId = await outbox.Runs.GetOpenRunAsync(conn, tx, "live", ct);
        foreach (var c in candidates)
        {
            var memo = $"track:{c.TrackId}:{c.Revision}";
            if (_sent.ContainsKey(memo))
            {
                skipped++;
                continue;
            }
            var payload = new JsonObject
            {
                ["track_id"] = c.TrackId,
                ["expected_revision"] = c.Revision,
                ["expire_at"] = FactMapper.Iso(c.ExpireAt),
                ["watermark"] = FactMapper.Iso(watermark),
                ["reason"] = "timeout",
            };
            if (await EnqueueOnceAsync(conn, tx, Command(TrackCommand, $"track:{c.TrackId}", c.Revision, c.Cause, c.Correlation, "track", payload, runId, wall, watermark), ct))
            {
                sentTracks++;
                sentNow.Add(memo);
            }
            else
            {
                skipped++;
            }
        }
        foreach (var a in alerts)
        {
            var memo = $"alert:{a.AlertId}:{a.Revision}";
            if (_sent.ContainsKey(memo))
            {
                skipped++;
                continue;
            }
            var payload = new JsonObject
            {
                ["alert_id"] = a.AlertId,
                ["expected_revision"] = a.Revision,
                ["expire_at"] = FactMapper.Iso(a.ExpireAt),
                ["watermark"] = FactMapper.Iso(watermark),
                ["reason"] = "max_age",
            };
            if (await EnqueueOnceAsync(conn, tx, Command(AlertCommand, $"alert:{a.AlertId}", a.Revision, a.Cause, a.Correlation, "alert", payload, runId, wall, watermark), ct))
            {
                sentAlerts++;
                sentNow.Add(memo);
            }
            else
            {
                skipped++;
            }
        }
        await tx.CommitAsync(ct);
        foreach (var memo in sentNow)
        {
            _sent[memo] = wall;
        }
        foreach (var (key, at) in _sent.Where(kv => wall - kv.Value > TimeSpan.FromHours(6)).ToList())
        {
            _sent.TryRemove(key, out _);
        }
        if (sentTracks + sentAlerts > 0)
        {
            logger.LogInformation("Watchdog asked to expire {Tracks} track(s) and {Alerts} text alert(s) at watermark {Watermark:O} ({Skipped} already commanded)", sentTracks, sentAlerts, watermark, skipped);
        }
        return new Sweep(watermark, sentTracks, sentAlerts, skipped);
    }

    /// <summary>Another watchdog replica may have written the same minute's command: the outbox is unique per event id, so check before inserting.</summary>
    private async Task<bool> EnqueueOnceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Envelope command, CancellationToken ct)
    {
        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM messaging.outbox WHERE event_id = @id)", conn, tx))
        {
            exists.Parameters.AddWithValue("id", command.EventId);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!)
            {
                return false;
            }
        }
        await outbox.EnqueueAsync(conn, tx, command, ct);
        return true;
    }

    /// <summary>
    /// Event time the sweep may trust: the wall clock, unless something older than <see cref="BacklogTolerance"/> is still
    /// in flight — an unconfirmed outbox row, a delivery without a receipt (its event is in the archive), or a legacy
    /// Pending/InProgress raw row — in which case the oldest of them.
    /// </summary>
    public static async Task<DateTimeOffset> WatermarkAsync(NpgsqlConnection conn, DateTimeOffset wall, CancellationToken ct)
    {
        DateTimeOffset? oldest = null;
        void Consider(object? value)
        {
            if (value is DateTime dt)
            {
                var at = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                oldest = oldest is null || at < oldest ? at : oldest;
            }
        }
        await using (var o = new NpgsqlCommand("SELECT min((envelope->>'occurred_at')::timestamptz) FROM messaging.outbox WHERE confirmed_at IS NULL", conn))
        {
            Consider(await o.ExecuteScalarAsync(ct));
        }
        // Message-scoped events (ingress.received has no raw id yet, but it is the upstream backlog); aggregate events/commands never hold the watermark,
        // nor does the analytics projection (P15): its lag is a reporting lag, not a domain stage — expiry must not wait for it.
        await using (var d = new NpgsqlCommand("SELECT min(e.occurred_at) FROM processing.deliveries d JOIN messaging.events e ON e.event_id = d.event_id WHERE d.outcome IS NULL AND d.subscription_id <> 'message-analytics' AND e.event_type IN ('ingress.received', 'raw.stored', 'message.normalized', 'parse.completed', 'llm.requested', 'llm.completed', 'llm.failed', 'observations.recorded', 'message.analysis.completed')", conn))
        {
            Consider(await d.ExecuteScalarAsync(ct));
        }
        // Not raw_messages.processing_status: on the platform path the raw-writer stores rows Pending and nothing advances
        // them (ADR-0009, cutover) — the backlog is what the outbox and the deliveries still owe, not that column.
        return oldest is { } p && p < wall - BacklogTolerance ? p : wall;
    }

    private Envelope Command(string type, string aggregateId, int revision, Guid? cause, Guid? correlation, string partitionKey, JsonObject payload, Guid runId, DateTimeOffset now, DateTimeOffset watermark)
    {
        // Deterministic per (aggregate, revision, minute): two sweeps of the same minute collapse in the outbox/inbox; later ones are new commands the owner judges by revision.
        var eventId = SourceIdentity.NameBasedGuid(Namespace, $"{type}:{aggregateId}:{revision}:{watermark:yyyyMMddHHmm}");
        return new Envelope
        {
            EventId = eventId,
            EventType = type,
            SchemaVersion = "1.0",
            Producer = Instance,
            OccurredAt = now,
            CorrelationId = correlation ?? SourceIdentity.NameBasedGuid(Namespace, aggregateId),
            CausationId = cause ?? SourceIdentity.NameBasedGuid(Namespace, "legacy:" + aggregateId), // aggregates the legacy loop created have no event behind them
            Traceparent = RawStoredEnvelope.CurrentTraceparent(),
            ProcessingRunId = runId,
            PipelineVersion = outbox.Runs.PipelineVersion,
            Lane = "live",
            AggregateId = aggregateId,
            AggregateRevision = revision,
            PartitionKey = partitionKey,
            Payload = payload,
        };
    }
}
