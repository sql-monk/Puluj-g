using Microsoft.EntityFrameworkCore;
using Puluj.Analytics.Persistence;

namespace Puluj.Analytics.Analysis;

/// <summary>The raw-message columns needed for the independent analytics index.</summary>
public sealed record RawRow(long RawMessageId, int SourceId, string SourceMessageId, DateTime PublishedAt, DateTime ReceivedAt)
{
    /// <summary>`(post key, is edit)`: Telegram edits are stored as `{id}:e{timestamp}` and belong to the post `{id}`.</summary>
    public (string Key, bool IsEdit) Post()
    {
        var suffix = SourceMessageId.LastIndexOf(":e", StringComparison.Ordinal);
        return suffix > 0 && long.TryParse(SourceMessageId[(suffix + 2)..], out _)
            ? (SourceMessageId[..suffix], true)
            : (SourceMessageId, false);
    }
}

/// <summary>Reads the public schema with plain SQL (column aliases in snake_case, matched to the record properties by the naming convention).</summary>
public static class RawMessageReader
{
    /// <summary>The next batch above the watermark, oldest id first; rows younger than the safety lag are left for later.</summary>
    public static Task<List<RawRow>> BatchAsync(AnalyticsDbContext db, long watermark, DateTime receivedBefore, int limit, CancellationToken ct) =>
        db.Database.SqlQuery<RawRow>($"""
            SELECT raw_message_id, source_id, source_message_id, published_at, received_at
            FROM raw_messages
            WHERE raw_message_id > {watermark} AND received_at < {receivedBefore}
            ORDER BY raw_message_id
            LIMIT {limit}
            """).ToListAsync(ct);

    public static async Task<long> MaxIdAsync(AnalyticsDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<long>("SELECT coalesce(max(raw_message_id), 0) AS \"Value\" FROM raw_messages").ToListAsync(ct)).FirstOrDefault();

}
