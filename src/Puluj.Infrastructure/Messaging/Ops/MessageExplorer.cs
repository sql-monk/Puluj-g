using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Infrastructure.Messaging.Ops;

/// <summary>
/// Message explorer (P13, plan §8.7): find a raw message and show its whole lifecycle on one card — the events that
/// carry its id (archived by the archive subscription), every expected delivery of every event with its receipt and
/// attempts, the extractions and observations, what was derived from them (tracks, alerts, incidents), open or
/// resolved quarantine rows, and the summary of who is still waiting, who completed and who failed. Bounded by
/// design: search ≤ 100 rows over ≤ 7 days of text search, a card ≤ 200 events × 20 attempts per delivery,
/// quarantine envelopes truncated (the full JSON stays in the table), all under a 10 s statement timeout.
/// </summary>
public sealed class MessageExplorer(IDbContextFactory<PulujDbContext> factory)
{
    public const int MaxLimit = 100, MaxEvents = 200, MaxAttempts = 20, MaxTextHours = 168, MaxHours = 720, EnvelopePreview = 2000;

    public async Task<IReadOnlyList<MessageSearchRowDto>> SearchAsync(string? q, int? sourceId, int? hours, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
        var span = Math.Clamp(hours ?? 24, 1, MaxHours);
        q = q?.Trim();
        if (q is { Length: > 200 })
        {
            q = q[..200];
        }
        var byId = long.TryParse(q, out var rawId);
        // A free-text search is the only unindexed path (ILIKE over raw_text): bounded to a week unless a key/id narrows it.
        if (!string.IsNullOrEmpty(q) && !byId && sourceId is null)
        {
            span = Math.Min(span, MaxTextHours);
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);
        await Exec(conn, tx, "SET LOCAL statement_timeout = '10s'", ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT r.raw_message_id, r.source_id, s.code, r.source_message_id, r.published_at, r.received_at, r.processing_status,
                   (SELECT count(*)::int FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id),
                   (SELECT count(*)::int FROM processing.observations o WHERE o.raw_message_id = r.raw_message_id),
                   (SELECT d.outcome FROM messaging.events e JOIN processing.deliveries d ON d.event_id = e.event_id WHERE e.raw_message_id = r.raw_message_id ORDER BY d.completed_at DESC NULLS FIRST LIMIT 1),
                   left(coalesce(r.raw_text, ''), 160)
            FROM raw_messages r JOIN sources s ON s.source_id = r.source_id
            WHERE r.received_at >= now() - make_interval(hours => @hours)
              {(sourceId is null ? "" : "AND r.source_id = @source")}
              {(string.IsNullOrEmpty(q) ? "" : byId ? "AND (r.raw_message_id = @id OR r.source_message_id = @q)" : "AND (r.source_message_id = @q OR r.source_message_key = @q OR r.raw_text ILIKE @like)")}
            ORDER BY r.received_at DESC
            LIMIT @take
            """, conn, tx);
        cmd.Parameters.AddWithValue("hours", span);
        cmd.Parameters.AddWithValue("take", take);
        if (sourceId is not null)
        {
            cmd.Parameters.AddWithValue("source", sourceId.Value);
        }
        if (!string.IsNullOrEmpty(q))
        {
            cmd.Parameters.AddWithValue("q", q);
            if (byId)
            {
                cmd.Parameters.AddWithValue("id", rawId);
            }
            else
            {
                cmd.Parameters.AddWithValue("like", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            }
        }
        var rows = new List<MessageSearchRowDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MessageSearchRowDto(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                StatusName(reader.GetInt32(6)), reader.GetInt32(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10)));
        }
        return rows;
    }

    public async Task<MessageLifecycleDto?> LifecycleAsync(long rawMessageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);
        await Exec(conn, tx, "SET LOCAL statement_timeout = '10s'", ct);

        int sourceId; string sourceCode, sourceMessageId, status; DateTimeOffset publishedAt, receivedAt; string? text, url;
        await using (var cmd = new NpgsqlCommand("SELECT r.source_id, s.code, r.source_message_id, r.published_at, r.received_at, r.processing_status, r.raw_text, r.url FROM raw_messages r JOIN sources s ON s.source_id = r.source_id WHERE r.raw_message_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            sourceId = reader.GetInt32(0);
            sourceCode = reader.GetString(1);
            sourceMessageId = reader.GetString(2);
            publishedAt = reader.GetFieldValue<DateTimeOffset>(3);
            receivedAt = reader.GetFieldValue<DateTimeOffset>(4);
            status = StatusName(reader.GetInt32(5));
            text = reader.IsDBNull(6) ? null : reader.GetString(6);
            url = reader.IsDBNull(7) ? null : reader.GetString(7);
        }
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
        {
            url = null; // the card renders only http(s) links (P12 A01 rule)
        }

        // Events of this raw message (archived); the outbox row, when still present, adds published/confirmed times.
        var events = new List<(Guid Id, string Type, string Lane, DateTimeOffset OccurredAt, DateTimeOffset? PublishedAt, DateTimeOffset? ConfirmedAt, Guid? CausationId, string Producer)>();
        var truncated = false;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT e.event_id, e.event_type, e.lane, e.occurred_at, e.published_at, o.confirmed_at, e.causation_id, coalesce(e.envelope->>'producer', '')
            FROM messaging.events e LEFT JOIN messaging.outbox o ON o.event_id = e.event_id AND o.target_queue IS NULL
            WHERE e.raw_message_id = @id ORDER BY e.occurred_at, e.event_id LIMIT @max
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            cmd.Parameters.AddWithValue("max", MaxEvents + 1);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (events.Count == MaxEvents)
                {
                    truncated = true;
                    break;
                }
                events.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetString(7)));
            }
        }
        var eventIds = events.Select(e => e.Id).ToArray();

        var deliveries = new Dictionary<Guid, List<(string Subscription, string? Lane, DateTimeOffset ExpectedAt, string? Outcome, DateTimeOffset? CompletedAt, string? Reason, string? Actor)>>();
        await using (var cmd = new NpgsqlCommand("SELECT event_id, subscription_id, lane, expected_at, outcome, completed_at, reason, actor FROM processing.deliveries WHERE event_id = ANY(@ids) ORDER BY expected_at, subscription_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", eventIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!deliveries.TryGetValue(id, out var list))
                {
                    deliveries[id] = list = [];
                }
                list.Add((reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        var attempts = new Dictionary<(Guid, string), List<LifecycleAttemptDto>>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT event_id, split_part(subscription_id, ':', 1), attempt_id, worker, state, started_at, finished_at, error, retry_of_attempt_id, retry_reason FROM processing.attempts WHERE event_id = ANY(@ids) ORDER BY attempt_id DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", eventIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetGuid(0), reader.GetString(1));
                if (!attempts.TryGetValue(key, out var list))
                {
                    attempts[key] = list = [];
                }
                list.Add(new LifecycleAttemptDto(reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }
        var eventDtos = events.Select(e => new LifecycleEventDto(e.Id, e.Type, e.Lane, e.OccurredAt, e.PublishedAt, e.ConfirmedAt, e.CausationId, e.Producer,
            (deliveries.GetValueOrDefault(e.Id) ?? []).Select(d =>
            {
                var list = attempts.GetValueOrDefault((e.Id, d.Subscription)) ?? [];
                return new LifecycleDeliveryDto(d.Subscription, d.Lane ?? e.Lane, d.ExpectedAt, d.Outcome, d.CompletedAt, d.Reason, d.Actor, list.Take(MaxAttempts).OrderBy(a => a.AttemptId).ToList(), list.Count > MaxAttempts);
            }).ToList())).ToList();

        var extractions = new List<LifecycleExtractionDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT x.extraction_id, x.run_id, x.extraction_version, x.method, x.outcome, x.versions::text, x.finalized_by, x.created_at,
                   (SELECT count(*)::int FROM processing.observations o WHERE o.extraction_id = x.extraction_id), x.error::text
            FROM processing.extractions x WHERE x.raw_message_id = @id ORDER BY x.created_at
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                extractions.Add(new LifecycleExtractionDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : Truncate(reader.GetString(9), 1000)));
            }
        }
        var observations = new List<LifecycleObservationDto>();
        await using (var cmd = new NpgsqlCommand("SELECT observation_id, extraction_id, event_kind_code, category, effective_at, left(payload::text, 300), legacy_target_id FROM processing.observations WHERE raw_message_id = @id ORDER BY effective_at, observation_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                observations.Add(new LifecycleObservationDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6)));
            }
        }

        var derived = new List<LifecycleRefDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT 'track', tt.target_track_id, 'track #' || tt.target_track_id || ' (target #' || t.target_id || ')' FROM targets t JOIN track_targets tt ON tt.target_id = t.target_id WHERE t.raw_message_id = @id
            UNION ALL SELECT 'alert', a.air_alert_id, 'air alert #' || a.air_alert_id || CASE WHEN a.ended_at IS NULL THEN ' (active)' ELSE ' (ended)' END FROM air_alerts a WHERE a.start_raw_message_id = @id OR a.end_raw_message_id = @id
            UNION ALL SELECT 'incident', io.incident_id, 'incident #' || io.incident_id FROM incident_observations io JOIN processing.observations o ON o.observation_id = io.observation_id WHERE o.raw_message_id = @id
            LIMIT 200
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                derived.Add(new LifecycleRefDto(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }
        derived = derived.DistinctBy(d => (d.Kind, d.Id)).ToList();

        var quarantine = new List<LifecycleQuarantineDto>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT quarantine_id, subscription_id, lane, reason, error, quarantined_at, resolved_at, resolution, left(envelope::text, @preview + 1) FROM processing.quarantine WHERE event_id = ANY(@ids) OR (jsonb_typeof(envelope->'raw_message_id') = 'number' AND (envelope->>'raw_message_id')::bigint = @id) ORDER BY quarantined_at", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", eventIds);
            cmd.Parameters.AddWithValue("id", rawMessageId);
            cmd.Parameters.AddWithValue("preview", EnvelopePreview);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var envelope = reader.GetString(8);
                quarantine.Add(new LifecycleQuarantineDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    envelope.Length > EnvelopePreview ? envelope[..EnvelopePreview] : envelope, envelope.Length > EnvelopePreview));
            }
        }
        await tx.RollbackAsync(ct);

        return new MessageLifecycleDto(rawMessageId, sourceId, sourceCode, sourceMessageId, publishedAt, receivedAt, status, text, url,
            eventDtos, truncated, extractions, observations, derived, quarantine, Summarize(eventDtos));
    }

    /// <summary>
    /// Who still owes a receipt, who completed, who failed — over every expected delivery of every event of the root
    /// (`subscription/lane`). Completion (ADR-0005 `domain_completed`): every registered expected branch is terminal
    /// (`completed`/`noop`/`waived`); a `quarantined` branch makes it `needs_attention`; no events yet → `pending`.
    /// </summary>
    public static LifecycleSummaryDto Summarize(IReadOnlyList<LifecycleEventDto> events)
    {
        var waiting = new SortedSet<string>(StringComparer.Ordinal);
        var completed = new SortedSet<string>(StringComparer.Ordinal);
        var failed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            foreach (var d in e.Deliveries)
            {
                var key = $"{d.Subscription}/{d.Lane ?? e.Lane}";
                switch (d.Outcome)
                {
                    case null: waiting.Add(key); break;
                    case "quarantined": failed.Add(key); break;
                    default: completed.Add(key); break;
                }
            }
        }
        // The registry decided what "expected" meant at write time (OutboxWriter); the receipts carry that decision, so no manifest reader is needed here.
        var completion = events.Count == 0 ? "pending" : failed.Count > 0 ? "needs_attention" : waiting.Count > 0 ? "in_progress" : "completed";
        return new LifecycleSummaryDto(completion, waiting.ToList(), completed.ToList(), failed.ToList());
    }

    private static string StatusName(int status) => status switch { 0 => "pending", 1 => "processed", 2 => "failed", 3 => "skipped", 4 => "in_progress", _ => status.ToString() };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
