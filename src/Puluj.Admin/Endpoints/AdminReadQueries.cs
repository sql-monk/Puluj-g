using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Puluj.Contracts;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Admin.Endpoints;

/// <summary>
/// Keyset-paged reads for the admin panel. LLM history also counts all matching rows on every request;
/// its cost depends on the selected period and filters, not just the requested page size.
/// </summary>
public static class AdminReadQueries
{
    public const int MaxPage = 200;
    private const int MessageTextLimit = 2_000;

    /// <summary>LLM outcomes that are a normal answer; everything else (HTTP errors, timeouts, invalid responses) is a failure.</summary>
    public static readonly string[] LlmOkOutcomes = ["facts", "empty", "refusal", "success"];

    /// <summary>
    /// Messages of one source, newest received first (index `source_id, received_at DESC, raw_message_id DESC`), or the single
    /// message `rawMessageId`. `cursor` is the <see cref="AdminMessagePageDto.NextCursor"/> of the previous page.
    /// </summary>
    public static async Task<AdminMessagePageDto> MessagesAsync(PulujDbContext db, int? sourceId, long? rawMessageId, string? cursor, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, MaxPage);
        var query = db.RawMessages.AsNoTracking();
        if (rawMessageId is { } id)
        {
            query = query.Where(x => x.RawMessageId == id);
        }
        if (sourceId is { } source)
        {
            query = query.Where(x => x.SourceId == source);
        }
        if (TryParseCursor(cursor, out var at, out var beforeId))
        {
            query = query.Where(x => x.ReceivedAt < at || (x.ReceivedAt == at && x.RawMessageId < beforeId));
        }
        var rows = await query
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RawMessageId)
            .Take(take + 1)
            .Select(x => new
            {
                x.RawMessageId, x.SourceId, SourceCode = x.Source!.Code, SourceName = x.Source.Name, x.SourceMessageId,
                x.PublishedAt, x.ReceivedAt, x.ProcessingStatus, x.ProcessedAt, x.Attempts,
                Text = x.RawText == null ? null : x.RawText.Substring(0, MessageTextLimit + 1),
                x.Url, Targets = x.Targets.Count(),
                x.SourceMessageKey, x.SourceRevision,
                Revisions = db.RawMessages.Count(r => r.SourceId == x.SourceId && r.SourceMessageKey == x.SourceMessageKey),
            })
            .ToListAsync(ct);
        var page = rows.Take(take).Select(x => new AdminMessageDto(
            x.RawMessageId, x.SourceId, x.SourceCode, x.SourceName, x.SourceMessageId, x.PublishedAt, x.ReceivedAt,
            x.ProcessingStatus.ToString(), x.ProcessedAt, x.Attempts,
            x.Text is { Length: > MessageTextLimit } ? x.Text[..MessageTextLimit] : x.Text, x.Text is { Length: > MessageTextLimit },
            x.Url, x.Targets, x.SourceMessageKey, x.SourceRevision, x.Revisions)).ToList();
        var next = rows.Count > take ? Cursor(page[^1].ReceivedAt, page[^1].Id) : null;
        return new AdminMessagePageDto(page, next);
    }

    /// <summary>
    /// LLM requests of the period, newest first, filtered by result (`failures`, `facts`, `empty`, `refusal`, `success`) and by a
    /// search term: a number matches the request or message id, anything else the source code.
    /// </summary>
    public static async Task<LlmRequestPageDto> LlmRequestsAsync(
        PulujDbContext db, DateTimeOffset from, DateTimeOffset to, string? outcome, string? search, long? beforeId, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, MaxPage);
        var query = db.LlmRequests.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt <= to);
        query = outcome switch
        {
            null or "" or "all" => query,
            "failures" => query.Where(x => !LlmOkOutcomes.Contains(x.Outcome)),
            _ => query.Where(x => x.Outcome == outcome),
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (long.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                query = query.Where(x => x.LlmRequestId == number || x.RawMessageId == number);
            }
            else
            {
                var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                query = query.Where(x => EF.Functions.ILike(x.Source!.Code, pattern));
            }
        }
        var total = await query.LongCountAsync(ct);
        if (beforeId is { } before)
        {
            query = query.Where(x => x.LlmRequestId < before);
        }
        var rows = await query.OrderByDescending(x => x.LlmRequestId).Take(take + 1).Select(x => new LlmRequestDto(
            x.LlmRequestId, x.OccurredAt, x.RawMessageId, x.SourceId, x.Source!.Code, x.Worker, x.Model, x.PromptVersion,
            x.Outcome, x.StatusCode, x.DurationMs, x.InputTokens, x.CacheCreationInputTokens, x.CacheReadInputTokens, x.OutputTokens,
            x.EstimatedCostUsd, x.FactsCount, x.Error)).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        return new LlmRequestPageDto(page, total, rows.Count > take ? page[^1].Id : null);
    }

    public static string Cursor(DateTimeOffset at, long id) => $"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{id.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParseCursor(string? cursor, out DateTimeOffset at, out long id)
    {
        at = default;
        id = 0;
        var parts = cursor?.Split('_');
        if (parts is not { Length: 2 }
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }
        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}
