using Microsoft.EntityFrameworkCore;
using Npgsql;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;
using System.Text.Json;

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
    public static readonly string[] Views = ["all", "ignored", "llm", "targets", "events", "failed"];
    public static readonly string[] Sorts = ["receivedAt", "publishedAt", "rawMessageId", "source", "sourceMessageId", "status"];

    public static bool IsValidView(string? view) => string.IsNullOrWhiteSpace(view) || Views.Contains(view, StringComparer.Ordinal);
    public static bool IsValidSort(string? sort) => string.IsNullOrWhiteSpace(sort) || Sorts.Contains(sort, StringComparer.Ordinal);
    public static bool IsValidDirection(string? direction) => string.IsNullOrWhiteSpace(direction) || direction is "asc" or "desc";

    /// <summary>Compatibility overload for existing operator tools: an unqualified search means every outcome.</summary>
    public Task<IReadOnlyList<MessageSearchRowDto>> SearchAsync(string? q, int? sourceId, int? hours, int? limit, CancellationToken ct) =>
        SearchAsync(q, sourceId, hours, limit, "all", ct);

    public async Task<IReadOnlyList<MessageSearchRowDto>> SearchAsync(string? q, int? sourceId, int? hours, int? limit, string? view, CancellationToken ct)
        => (await SearchPageAsync(q, sourceId is { } id ? [id] : null, hours, 1, limit, view, "receivedAt", "desc", ct)).Items;

    /// <summary>Reads a page of immutable raw-message revisions.  Sort choices are an allow-list so no request value
    /// becomes SQL syntax; source filters are bound as a PostgreSQL array.</summary>
    public async Task<MessageSearchPageDto> SearchPageAsync(string? q, IReadOnlyCollection<int>? sourceIds, int? hours, int? page, int? limit, string? view, string? sort, string? direction, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? MaxLimit, 1, MaxLimit);
        var pageNumber = Math.Clamp(page ?? 1, 1, 1_000_000);
        var span = Math.Clamp(hours ?? 24, 1, MaxHours);
        var sources = sourceIds?.Where(x => x > 0).Distinct().ToArray() ?? [];
        q = q?.Trim();
        view = string.IsNullOrWhiteSpace(view) ? "all" : view;
        sort = string.IsNullOrWhiteSpace(sort) ? "receivedAt" : sort;
        direction = string.IsNullOrWhiteSpace(direction) ? "desc" : direction;
        if (!IsValidView(view))
        {
            throw new ArgumentException($"unknown message view '{view}'", nameof(view));
        }
        if (!IsValidSort(sort) || !IsValidDirection(direction))
        {
            throw new ArgumentException("unknown message sort", nameof(sort));
        }
        if (q is { Length: > 200 })
        {
            q = q[..200];
        }
        var byId = long.TryParse(q, out var rawId);
        // A free-text search is the only unindexed path (ILIKE over raw_text): bounded to a week unless a key/id narrows it.
        if (!string.IsNullOrEmpty(q) && !byId && sources.Length == 0)
        {
            span = Math.Min(span, MaxTextHours);
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);
        await Exec(conn, tx, "SET LOCAL statement_timeout = '10s'", ct);
        var where = $"""
            WHERE r.received_at >= now() - make_interval(hours => @hours)
              {(sources.Length == 0 ? "" : "AND r.source_id = ANY(@sources)")}
              {(string.IsNullOrEmpty(q) ? "" : byId ? "AND (r.raw_message_id = @id OR r.source_message_id = @q)" : "AND (r.source_message_id = @q OR r.source_message_key = @q OR r.raw_text ILIKE @like)")}
              {ViewSql(view)}
            """;

        void Bind(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("hours", span);
            if (sources.Length > 0)
            {
                command.Parameters.AddWithValue("sources", sources);
            }
            if (!string.IsNullOrEmpty(q))
            {
                command.Parameters.AddWithValue("q", q);
                if (byId)
                {
                    command.Parameters.AddWithValue("id", rawId);
                }
                else
                {
                    command.Parameters.AddWithValue("like", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
                }
            }
        }

        long total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM raw_messages r JOIN sources s ON s.source_id = r.source_id {where}", conn, tx))
        {
            Bind(count);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT r.raw_message_id, r.source_id, s.code, r.source_message_id, r.published_at, r.received_at, r.processing_status,
                   (SELECT count(*)::int FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id),
                   (SELECT count(*)::int FROM processing.observations o WHERE o.raw_message_id = r.raw_message_id),
                   (SELECT count(*)::int FROM targets t WHERE t.raw_message_id = r.raw_message_id),
                   (SELECT count(*)::int FROM llm_requests l WHERE l.raw_message_id = r.raw_message_id),
                   (SELECT x.outcome FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id ORDER BY x.created_at DESC LIMIT 1),
                   (SELECT x.method FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id ORDER BY x.created_at DESC LIMIT 1),
                   (SELECT d.outcome FROM messaging.events e JOIN processing.deliveries d ON d.event_id = e.event_id WHERE e.raw_message_id = r.raw_message_id ORDER BY d.completed_at DESC NULLS FIRST LIMIT 1),
                   left(coalesce(r.raw_text, ''), 160),
                   coalesce(r.raw_payload -> 'reactions', '[]'::jsonb)::text
            FROM raw_messages r JOIN sources s ON s.source_id = r.source_id
            {where}
            ORDER BY {OrderSql(sort, direction)}, r.raw_message_id DESC
            LIMIT @take OFFSET @offset
            """, conn, tx);
        Bind(cmd);
        cmd.Parameters.AddWithValue("take", take);
        cmd.Parameters.AddWithValue("offset", (long)(pageNumber - 1) * take);
        var rows = new List<MessageSearchRowDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MessageSearchRowDto(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                StatusName(reader.GetInt32(6)), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetString(14), Reactions(reader.GetString(15))));
        }
        return new MessageSearchPageDto(rows, total, pageNumber, take);
    }

    private static string OrderSql(string sort, string direction) => (sort, direction) switch
    {
        ("receivedAt", "asc") => "r.received_at ASC",
        ("receivedAt", _) => "r.received_at DESC",
        ("publishedAt", "asc") => "r.published_at ASC",
        ("publishedAt", _) => "r.published_at DESC",
        ("rawMessageId", "asc") => "r.raw_message_id ASC",
        ("rawMessageId", _) => "r.raw_message_id DESC",
        ("source", "asc") => "s.code ASC",
        ("source", _) => "s.code DESC",
        ("sourceMessageId", "asc") => "r.source_message_id ASC",
        ("sourceMessageId", _) => "r.source_message_id DESC",
        ("status", "asc") => "r.processing_status ASC",
        ("status", _) => "r.processing_status DESC",
        _ => "r.received_at DESC",
    };

    private static IReadOnlyList<MessageReactionDto> Reactions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return document.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object
                    && x.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && x.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    && x.TryGetProperty("count", out var count) && count.TryGetInt32(out _))
                .Select(x => new MessageReactionDto(x.GetProperty("kind").GetString()!, x.GetProperty("value").GetString()!, x.GetProperty("count").GetInt32()))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
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

        var targets = new List<LifecycleTargetDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT t.target_id, t.segment_index, t.event_type::text, k.code, t.observed_at, t.object_count,
                   coalesce(tm.code, tf.code, tc.code), p.name, t.location_accuracy_km, t.duplicate_of_target_id
            FROM targets t
            LEFT JOIN event_kinds k ON k.event_kind_id = t.event_kind_id
            LEFT JOIN target_models tm ON tm.target_model_id = t.target_model_id
            LEFT JOIN target_families tf ON tf.target_family_id = t.target_family_id
            LEFT JOIN target_classes tc ON tc.target_class_id = t.target_class_id
            LEFT JOIN places p ON p.place_id = t.location_place_id
            WHERE t.raw_message_id = @id ORDER BY t.segment_index, t.target_id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                targets.Add(new LifecycleTargetDto(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.IsDBNull(9) ? null : reader.GetInt64(9)));
            }
        }

        var llmRequests = new List<LifecycleLlmRequestDto>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT llm_request_id, occurred_at, model, prompt_version, outcome, status_code, duration_ms, input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens, estimated_cost_usd, facts_count, error FROM llm_requests WHERE raw_message_id = @id ORDER BY occurred_at, llm_request_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rawMessageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                llmRequests.Add(new LifecycleLlmRequestDto(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.GetInt32(12), reader.IsDBNull(13) ? null : Truncate(reader.GetString(13), 1000)));
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
            eventDtos, truncated, extractions, observations, targets, llmRequests, derived, quarantine, Summarize(eventDtos));
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

    private static string ViewSql(string view) => view switch
    {
        "all" => "",
        // These are explicit terminal outcomes, rather than a guess from an empty result set.
        "ignored" => "AND (r.processing_status = 3 OR EXISTS (SELECT 1 FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id AND x.outcome IN ('no_facts', 'unsupported')))",
        "llm" => "AND EXISTS (SELECT 1 FROM llm_requests l WHERE l.raw_message_id = r.raw_message_id)",
        "targets" => "AND EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = r.raw_message_id)",
        "events" => "AND EXISTS (SELECT 1 FROM processing.observations o WHERE o.raw_message_id = r.raw_message_id)",
        "failed" => "AND (r.processing_status = 2 OR EXISTS (SELECT 1 FROM processing.extractions x WHERE x.raw_message_id = r.raw_message_id AND x.outcome = 'failed'))",
        _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
