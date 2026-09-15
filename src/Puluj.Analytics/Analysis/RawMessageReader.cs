using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Analysis;

/// <summary>A raw message as the analytics needs it: six columns of `raw_messages` plus two payload fields.</summary>
public sealed record RawRow(long RawMessageId, int SourceId, string SourceMessageId, DateTime PublishedAt, DateTime ReceivedAt, string? RawText, string? ForwardedFrom, long? ChannelId)
{
    /// <summary>`(post key, is edit)`: Telegram edits are stored as `{id}:e{timestamp}` and belong to the post `{id}`.</summary>
    public (string Key, bool IsEdit) Post()
    {
        var m = RawMessageReader.EditSuffix().Match(SourceMessageId);
        return m.Success ? (SourceMessageId[..m.Index], true) : (SourceMessageId, false);
    }
}

public sealed record RawText(long RawMessageId, string? Text);

/// <summary>Reads the public schema with plain SQL (column aliases in snake_case, matched to the record properties by the naming convention).</summary>
public static partial class RawMessageReader
{
    [GeneratedRegex(@":e\d+$")]
    public static partial Regex EditSuffix();

    [GeneratedRegex(@"^channel (\d+)$")]
    private static partial Regex ForwardChannel();

    /// <summary>Numeric channel id of a `forwardedFrom` value in the form `channel 123456`, else null (a display name of an unknown chat).</summary>
    public static long? ForwardedChannelId(string? forwardedFrom)
    {
        if (forwardedFrom is null)
        {
            return null;
        }
        var m = ForwardChannel().Match(forwardedFrom);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>The next batch above the watermark, oldest id first; rows younger than the safety lag are left for later.</summary>
    public static Task<List<RawRow>> BatchAsync(AnalyticsDbContext db, long watermark, DateTime receivedBefore, int limit, CancellationToken ct) =>
        db.Database.SqlQuery<RawRow>($"""
            SELECT raw_message_id, source_id, source_message_id, published_at, received_at, raw_text,
                   raw_payload ->> 'forwardedFrom' AS forwarded_from,
                   CASE WHEN raw_payload ->> 'channelId' ~ '^\d+$' THEN (raw_payload ->> 'channelId')::bigint END AS channel_id
            FROM raw_messages
            WHERE raw_message_id > {watermark} AND received_at < {receivedBefore}
            ORDER BY raw_message_id
            LIMIT {limit}
            """).ToListAsync(ct);

    public static Task<List<RawText>> TextsAsync(AnalyticsDbContext db, long[] ids, CancellationToken ct) =>
        db.Database.SqlQuery<RawText>($"SELECT raw_message_id, raw_text AS text FROM raw_messages WHERE raw_message_id = ANY({ids})").ToListAsync(ct);

    /// <summary>Facts already extracted by the processing pipeline. They are the primary key for cross-channel pairing.</summary>
    public static Task<List<EventFact>> FactsAsync(AnalyticsDbContext db, long[] ids, CancellationToken ct) =>
        db.Database.SqlQuery<EventFact>($"""
            SELECT raw_message_id, observed_at, event_type, target_category_id, target_class_id, target_family_id, target_model_id,
                   object_count, object_count_is_approximate, location_place_id, origin_place_id, destination_place_id, direction_deg
            FROM targets WHERE raw_message_id = ANY({ids})
            """).ToListAsync(ct);

    /// <summary>Other indexed posts that already contain the same event type and target category. The detailed fact
    /// comparison stays in C# so location roles, direction and count have one explicit, testable definition.</summary>
    public static Task<List<Candidate>> SemanticCandidatesAsync(AnalyticsDbContext db, MessageFingerprint message, EventFact fact, TimeSpan window, int limit, CancellationToken ct)
    {
        var from = message.PublishedAt.UtcDateTime - window;
        var to = message.PublishedAt.UtcDateTime + window;
        return fact.TargetCategoryId is int category
            ? db.Database.SqlQuery<Candidate>($"""
                SELECT DISTINCT m.raw_message_id, m.source_id, m.post_key, m.published_at, m.forwarded_source_id
                FROM analytics.messages m JOIN targets t ON t.raw_message_id = m.raw_message_id
                WHERE m.source_id <> {message.SourceId} AND m.published_at BETWEEN {from} AND {to}
                  AND t.event_type = {fact.EventType} AND t.target_category_id = {category}
                ORDER BY m.published_at, m.raw_message_id LIMIT {limit}
                """).ToListAsync(ct)
            : db.Database.SqlQuery<Candidate>($"""
                SELECT DISTINCT m.raw_message_id, m.source_id, m.post_key, m.published_at, m.forwarded_source_id
                FROM analytics.messages m JOIN targets t ON t.raw_message_id = m.raw_message_id
                WHERE m.source_id <> {message.SourceId} AND m.published_at BETWEEN {from} AND {to}
                  AND t.event_type = {fact.EventType} AND t.target_category_id IS NULL
                ORDER BY m.published_at, m.raw_message_id LIMIT {limit}
                """).ToListAsync(ct);
    }

    public static async Task<long> MaxIdAsync(AnalyticsDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<long>("SELECT coalesce(max(raw_message_id), 0) AS \"Value\" FROM raw_messages").ToListAsync(ct)).FirstOrDefault();

    private sealed record ChannelRow(int SourceId, long ChannelId);

    /// <summary>`channelId → source_id` for every collected Telegram channel, so a forward from one of them resolves to a source. One scan of the payloads; kept in memory afterwards.</summary>
    public static async Task<Dictionary<long, int>> ChannelMapAsync(AnalyticsDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ChannelRow>("""
            SELECT DISTINCT source_id, (raw_payload ->> 'channelId')::bigint AS channel_id
            FROM raw_messages
            WHERE raw_payload ->> 'channelId' ~ '^\d+$'
            """).ToListAsync(ct);
        var map = new Dictionary<long, int>();
        foreach (var r in rows)
        {
            map[r.ChannelId] = r.SourceId;
        }
        return map;
    }
}
