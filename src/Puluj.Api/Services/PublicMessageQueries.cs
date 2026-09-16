using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Entities.Processing;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>
/// U05's read-only source-message adapter. A row is one persisted raw revision, never a fuzzy de-duplicated post.
/// It stays separate from the admin explorer and only projects public fields from raw/stage/domain read models.
/// </summary>
public sealed class PublicMessageQueries(IDbContextFactory<PulujDbContext> factory, ReferenceCache refs, TimeProvider clock)
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxWindowDays = 366;
    private const int InlineTextLimit = 32 * 1024;
    private const int RelationPreviewLimit = 100;
    private const string LiveDataset = "live";
    private const string Consistency = "best_effort_live";
    private static readonly byte[] CursorKey = SHA256.HashData(Encoding.UTF8.GetBytes("puluj-public-messages-v1"));

    public sealed record Query(string? Q, string? SourceIds, bool? HasResults, string? Outcome, string? EventKinds,
        string? CategoryIds, string? ClassIds, string? FamilyIds, string? ModelIds, int? RegionId, string? Location,
        string? Confidence, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? PageSize, string? Dataset);

    public sealed class QueryException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private sealed record Filter(HashSet<int> Sources, bool? HasResults, HashSet<string> Outcomes, HashSet<int> EventKinds,
        HashSet<int> Categories, HashSet<int> Classes, HashSet<int> Families, HashSet<int> Models, HashSet<int> RegionPlaces,
        bool? HasLocation, HashSet<ConfidenceLevel> Confidences, string? Q, DateTimeOffset From, DateTimeOffset To,
        int Limit, string Fingerprint, string Dataset, Cursor? After);
    private sealed record Cursor(long PublishedTicks, long Id, string Fingerprint, string Dataset);
    private sealed record Counts(int All, int Matched, int Located, int Unlocated);
    private sealed record OutcomeInfo(string Value, bool FromStage);

    public async Task<PublicMessagePageDto> ListAsync(Query query, CancellationToken ct)
    {
        var filter = Validate(query);
        await using var db = await factory.CreateDbContextAsync(ct);
        EnsureDataset(filter.Dataset);
        filter = filter with { After = DecodeCursor(query.Cursor, filter.Fingerprint, filter.Dataset) };

        IQueryable<RawMessage> rows = db.RawMessages.AsNoTracking().Where(x => x.PublishedAt >= filter.From && x.PublishedAt < filter.To);
        if (filter.Sources.Count > 0) rows = rows.Where(x => filter.Sources.Contains(x.SourceId));
        if (!string.IsNullOrWhiteSpace(filter.Q)) rows = rows.Where(x => x.RawText != null && EF.Functions.ILike(x.RawText, $"%{filter.Q}%"));
        if (filter.Outcomes.Count > 0)
        {
            var activeGeneration = await db.ProcessingGenerations.AsNoTracking().Where(x => x.IsActive).Select(x => (Guid?)x.GenerationId).SingleOrDefaultAsync(ct);
            // Only a finalizer outcome from the active generation is authoritative. Before that read model exists,
            // expose the legacy processing status explicitly rather than guessing that zero targets means no_facts.
            var requested = filter.Outcomes;
            rows = rows.Where(raw =>
                (requested.Contains("pending") && (raw.ProcessingStatus == ProcessingStatus.Pending || raw.ProcessingStatus == ProcessingStatus.InProgress)) ||
                (requested.Contains("failed") && raw.ProcessingStatus == ProcessingStatus.Failed) ||
                (requested.Contains("skipped") && raw.ProcessingStatus == ProcessingStatus.Skipped) ||
                (requested.Contains("legacy_processed") && raw.ProcessingStatus == ProcessingStatus.Processed) ||
                (activeGeneration != null && db.StageResults.Any(stage => stage.RawMessageId == raw.RawMessageId && stage.Stage == "finalize" &&
                    db.ProcessingRuns.Any(run => run.RunId == stage.RunId && run.GenerationId == activeGeneration) &&
                    (requested.Contains(stage.Outcome) || (requested.Contains("awaiting_llm") && stage.Outcome == "awaiting_llm") ||
                        (requested.Contains("parsed_projection_pending") && !new[] { "completed", "no_facts", "failed", "unsupported", "awaiting_llm" }.Contains(stage.Outcome))))));
        }
        if (filter.After is Cursor after)
            rows = rows.Where(x => x.PublishedAt < new DateTimeOffset(after.PublishedTicks, TimeSpan.Zero) || (x.PublishedAt == new DateTimeOffset(after.PublishedTicks, TimeSpan.Zero) && x.RawMessageId < after.Id));

        var derived = HasDerivedFilter(filter);
        if (derived)
        {
            var targetIds = TargetMatches(db.Targets.AsNoTracking(), filter).Select(x => x.RawMessageId);
            rows = rows.Where(x => targetIds.Contains(x.RawMessageId));
        }
        else if (filter.HasResults is bool hasResults)
        {
            rows = hasResults ? rows.Where(x => db.Targets.Any(t => t.RawMessageId == x.RawMessageId))
                : rows.Where(x => !db.Targets.Any(t => t.RawMessageId == x.RawMessageId));
        }

        var page = await rows.OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.RawMessageId).Take(filter.Limit + 1).ToListAsync(ct);
        var more = page.Count > filter.Limit;
        if (more) page.RemoveAt(page.Count - 1);
        var ids = page.Select(x => x.RawMessageId).ToList();
        var counts = await CountsAsync(db, ids, filter, ct);
        var outcomes = await OutcomesAsync(db, page, ct);
        var items = page.Select(x => Summary(x, counts.GetValueOrDefault(x.RawMessageId), outcomes.GetValueOrDefault(x.RawMessageId))).ToList();
        var next = more && page.Count > 0 ? EncodeCursor(page[^1], filter.Fingerprint, filter.Dataset) : null;
        return new PublicMessagePageDto(filter.From, filter.To, filter.Dataset, Consistency, items, next, query.Cursor is not null);
    }

    public async Task<PublicMessageDetailsDto?> DetailsAsync(long id, string? dataset, CancellationToken ct)
    {
        EnsureDataset(dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = await db.RawMessages.AsNoTracking().SingleOrDefaultAsync(x => x.RawMessageId == id, ct);
        if (raw is null) return null;
        var counts = (await CountsAsync(db, [id], null, ct)).GetValueOrDefault(id);
        var outcome = (await OutcomesAsync(db, [raw], ct)).GetValueOrDefault(id);
        var summary = Summary(raw, counts, outcome);
        var results = await ResultsAsync(db, raw, null, DefaultPageSize, ct);
        var revisions = await RevisionsAsync(db, raw, null, DefaultPageSize, ct);
        var direct = await AlertRelationsAsync(db, id, ct);
        var textState = raw.RawText is null ? "structured_no_text" : raw.RawText.Length > InlineTextLimit ? "chunked" : "available";
        var root = $"/api/public/messages/{id}";
        return new PublicMessageDetailsDto(summary, textState, textState == "available" ? raw.RawText : null, results, revisions, direct,
            new Dictionary<string, string> { ["results"] = root + "/results?dataset=live", ["revisions"] = root + "/revisions?dataset=live", ["text"] = root + "/text?dataset=live" });
    }

    public async Task<PublicCollectionPageDto<PublicMessageResultDto>?> ResultsPageAsync(long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        EnsureDataset(dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = await db.RawMessages.AsNoTracking().SingleOrDefaultAsync(x => x.RawMessageId == id, ct);
        return raw is null ? null : await ResultsAsync(db, raw, cursor, PageSize(limit), ct);
    }

    public async Task<PublicCollectionPageDto<PublicMessageRevisionDto>?> RevisionsPageAsync(long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        EnsureDataset(dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = await db.RawMessages.AsNoTracking().SingleOrDefaultAsync(x => x.RawMessageId == id, ct);
        return raw is null ? null : await RevisionsAsync(db, raw, cursor, PageSize(limit), ct);
    }

    public async Task<PublicMessageTextChunkDto?> TextAsync(long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        EnsureDataset(dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = await db.RawMessages.AsNoTracking().SingleOrDefaultAsync(x => x.RawMessageId == id, ct);
        if (raw is null) return null;
        if (raw.RawText is null) return new PublicMessageTextChunkDto("structured_no_text", null, 0, 0, null);
        var offset = DecodeOffset(cursor, $"text:{id}:{dataset ?? LiveDataset}");
        var size = Math.Clamp(limit ?? InlineTextLimit, 1, InlineTextLimit);
        var text = offset < raw.RawText.Length ? raw.RawText.Substring(offset, Math.Min(size, raw.RawText.Length - offset)) : "";
        var next = offset + text.Length < raw.RawText.Length ? EncodeOffset(offset + text.Length, $"text:{id}:{dataset ?? LiveDataset}") : null;
        return new PublicMessageTextChunkDto("available", text, offset, raw.RawText.Length, next);
    }

    private Filter Validate(Query q)
    {
        var to = q.To ?? clock.GetUtcNow();
        var from = q.From ?? to.AddHours(-24);
        if (from >= to) throw new QueryException("from must be before to");
        if (to - from > TimeSpan.FromDays(MaxWindowDays)) throw new QueryException($"the window may span at most {MaxWindowDays} days");
        EnsureDataset(q.Dataset);
        var sources = Integers(q.SourceIds, "sourceIds");
        var outcomes = Csv(q.Outcome).Select(x => x.ToLowerInvariant()).ToHashSet();
        var allowedOutcomes = new HashSet<string>(["pending", "awaiting_llm", "parsed_projection_pending", "completed", "no_facts", "failed", "skipped", "legacy_processed"]);
        if (!outcomes.IsSubsetOf(allowedOutcomes)) throw new QueryException("unsupported public outcome");
        var eventKinds = Codes(q.EventKinds, refs.EventKinds.Values.ToDictionary(x => x.Code, x => x.EventKindId, StringComparer.OrdinalIgnoreCase), "eventKinds");
        var location = q.Location?.ToLowerInvariant() switch { null or "" or "all" => (bool?)null, "located" => true, "unlocated" => false, _ => throw new QueryException("location must be located|unlocated") };
        var confidence = ConfidenceSet(q.Confidence);
        var region = q.RegionId is int place ? refs.Descendants(place).Append(place).ToHashSet() : [];
        var fingerprint = Fingerprint(q, from, to);
        var categories = Integers(q.CategoryIds, "categoryIds");
        var classes = Integers(q.ClassIds, "classIds");
        var families = Integers(q.FamilyIds, "familyIds");
        var models = Integers(q.ModelIds, "modelIds");
        if (q.HasResults == false && (eventKinds.Count > 0 || categories.Count > 0 || classes.Count > 0 || families.Count > 0 || models.Count > 0 || region.Count > 0 || location is not null || confidence.Count > 0))
            throw new QueryException("hasResults=false cannot be combined with result-derived filters");
        return new Filter(sources, q.HasResults, outcomes, eventKinds, categories, classes, families, models, region,
            location, confidence, string.IsNullOrWhiteSpace(q.Q) ? null : q.Q.Trim(), from, to, PageSize(q.PageSize), fingerprint, string.IsNullOrWhiteSpace(q.Dataset) ? LiveDataset : q.Dataset!, null);
    }

    private IQueryable<Target> TargetMatches(IQueryable<Target> q, Filter f)
    {
        if (f.EventKinds.Count > 0) q = q.Where(x => x.EventKindId.HasValue && f.EventKinds.Contains(x.EventKindId.Value));
        if (f.Categories.Count > 0) q = q.Where(x => x.TargetCategoryId.HasValue && f.Categories.Contains(x.TargetCategoryId.Value));
        if (f.Classes.Count > 0) q = q.Where(x => x.TargetClassId.HasValue && f.Classes.Contains(x.TargetClassId.Value));
        if (f.Families.Count > 0) q = q.Where(x => x.TargetFamilyId.HasValue && f.Families.Contains(x.TargetFamilyId.Value));
        if (f.Models.Count > 0) q = q.Where(x => x.TargetModelId.HasValue && f.Models.Contains(x.TargetModelId.Value));
        if (f.RegionPlaces.Count > 0) q = q.Where(x => x.LocationPlaceId.HasValue && f.RegionPlaces.Contains(x.LocationPlaceId.Value));
        if (f.HasLocation is bool located) q = located ? q.Where(x => x.LocationKind != LocationKind.Unknown) : q.Where(x => x.LocationKind == LocationKind.Unknown);
        if (f.Confidences.Count > 0) q = q.Where(x => f.Confidences.Contains(x.Confidence));
        return q;
    }

    private static bool HasDerivedFilter(Filter f) => f.EventKinds.Count > 0 || f.Categories.Count > 0 || f.Classes.Count > 0 || f.Families.Count > 0 || f.Models.Count > 0 || f.RegionPlaces.Count > 0 || f.HasLocation is not null || f.Confidences.Count > 0;

    private async Task<Dictionary<long, Counts>> CountsAsync(PulujDbContext db, List<long> rawIds, Filter? filter, CancellationToken ct)
    {
        if (rawIds.Count == 0) return [];
        var all = await db.Targets.AsNoTracking().Where(x => rawIds.Contains(x.RawMessageId)).GroupBy(x => x.RawMessageId)
            .Select(g => new { Id = g.Key, All = g.Count(), Located = g.Count(x => x.LocationKind != LocationKind.Unknown) }).ToListAsync(ct);
        var matched = filter is null ? all.ToDictionary(x => x.Id, x => x.All) : await TargetMatches(db.Targets.AsNoTracking().Where(x => rawIds.Contains(x.RawMessageId)), filter)
            .GroupBy(x => x.RawMessageId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return all.ToDictionary(x => x.Id, x => new Counts(x.All, matched.GetValueOrDefault(x.Id), x.Located, x.All - x.Located));
    }

    private async Task<Dictionary<long, OutcomeInfo>> OutcomesAsync(PulujDbContext db, IReadOnlyList<RawMessage> raws, CancellationToken ct)
    {
        if (raws.Count == 0) return [];
        var ids = raws.Select(x => x.RawMessageId).ToList();
        var activeGeneration = await db.ProcessingGenerations.AsNoTracking().Where(x => x.IsActive).Select(x => (Guid?)x.GenerationId).SingleOrDefaultAsync(ct);
        // A stage is authoritative only when its run is pinned to the selected generation. If that read-side state is
        // absent, show the explicit legacy outcome rather than publishing a late/failed/superseded run.
        var stages = await (from stage in db.StageResults.AsNoTracking()
                            join run in db.ProcessingRuns.AsNoTracking() on stage.RunId equals run.RunId
                            where activeGeneration != null && ids.Contains(stage.RawMessageId) && stage.Stage == "finalize" && run.GenerationId == activeGeneration
                            select new { stage.RawMessageId, stage.Outcome, At = stage.FinishedAt ?? stage.StartedAt })
            .ToListAsync(ct);
        return raws.ToDictionary(raw => raw.RawMessageId, raw =>
        {
            var stage = stages.Where(x => x.RawMessageId == raw.RawMessageId).OrderByDescending(x => x.At).FirstOrDefault();
            return stage is null ? new OutcomeInfo(LegacyOutcome(raw.ProcessingStatus), false) : new OutcomeInfo(PublicOutcome(stage.Outcome), true);
        });
    }

    private PublicMessageSummaryDto Summary(RawMessage raw, Counts? counts, OutcomeInfo? outcome) => new(raw.RawMessageId.ToString(), raw.SourceId, refs.Sources.GetValueOrDefault(raw.SourceId)?.Code,
        raw.PublishedAt, raw.ReceivedAt, raw.SourceMessageKey, raw.SourceRevision, $"{raw.SourceId}:{raw.SourceMessageKey}", outcome?.Value ?? LegacyOutcome(raw.ProcessingStatus),
        outcome?.FromStage == true ? "stage_result" : "legacy", SafeUrl(raw.Url), counts?.All ?? 0, counts?.Matched ?? 0, counts?.Located ?? 0, counts?.Unlocated ?? 0, raw.RawText is not null);

    private async Task<PublicCollectionPageDto<PublicMessageResultDto>> ResultsAsync(PulujDbContext db, RawMessage raw, string? cursor, int limit, CancellationToken ct)
    {
        var scope = $"results:{raw.RawMessageId}";
        var offset = DecodeOffset(cursor, scope);
        var query = db.Targets.AsNoTracking().Where(x => x.RawMessageId == raw.RawMessageId);
        var total = await query.CountAsync(ct);
        var targets = await query.OrderBy(x => x.SegmentIndex).ThenBy(x => x.ObservationId).ThenBy(x => x.TargetId).Skip(offset).Take(limit).ToListAsync(ct);
        var targetIds = targets.Select(x => x.TargetId).ToList();
        // A result-page carries at most 100 aggregate references per visible fact. The full aggregate graph remains
        // separately paged by U04; this avoids materialising arbitrary memberships inside a message response.
        var tracks = await db.TrackTargets.AsNoTracking().Where(x => targetIds.Contains(x.TargetId)).OrderBy(x => x.TargetId).ThenBy(x => x.Sequence)
            .Take(targetIds.Count * RelationPreviewLimit).Select(x => new { x.TargetId, x.TargetTrackId }).ToListAsync(ct);
        var activeGeneration = await db.ProcessingGenerations.AsNoTracking().Where(x => x.IsActive).Select(x => (Guid?)x.GenerationId).SingleOrDefaultAsync(ct);
        var incidents = activeGeneration is Guid generation
            ? await db.IncidentObservations.AsNoTracking().Where(x => x.GenerationId == generation && x.LegacyTargetId != null && targetIds.Contains(x.LegacyTargetId.Value))
                .OrderBy(x => x.LegacyTargetId).ThenBy(x => x.IncidentId).Take(targetIds.Count * RelationPreviewLimit).Select(x => new { TargetId = x.LegacyTargetId!.Value, x.IncidentId }).ToListAsync(ct)
            : [];
        var trackLinks = tracks.GroupBy(x => x.TargetId).ToDictionary(x => x.Key, x => (IReadOnlyList<long>)x.Select(y => y.TargetTrackId).ToList());
        var incidentLinks = incidents.GroupBy(x => x.TargetId).ToDictionary(x => x.Key, x => (IReadOnlyList<long>)x.Select(y => y.IncidentId).ToList());
        var items = targets.Select(t => Result(t, trackLinks.GetValueOrDefault(t.TargetId) ?? [], incidentLinks.GetValueOrDefault(t.TargetId) ?? [])).ToList();
        return new PublicCollectionPageDto<PublicMessageResultDto>(items, offset + items.Count < total ? EncodeOffset(offset + items.Count, scope) : null, total);
    }

    private PublicMessageResultDto Result(Target t, IReadOnlyList<long> tracks, IReadOnlyList<long> incidents)
    {
        var kind = t.EventKindId is int id ? refs.EventKinds.GetValueOrDefault(id) : null;
        var map = Locator(t.LocationKind, t.LocationPlaceId, t.Location, t.LocationAccuracyKm, t.ObservedAt);
        return new PublicMessageResultDto(t.TargetId.ToString(), t.ObservationId?.ToString(), t.SegmentIndex, t.SegmentText, kind?.Code,
            Classification(t.TargetCategoryId, t.TargetClassId, t.TargetFamilyId, t.TargetModelId), t.ObservedAt, t.Confidence.ToString().ToLowerInvariant(), t.SourceId,
            t.Location is not null || t.LocationPlaceId is not null, map, tracks.Select(x => new PublicEntityRefDto("track", x.ToString(), null, "aggregate_evidence"))
                .Concat(incidents.Select(x => new PublicEntityRefDto("incident", x.ToString(), null, "aggregate_evidence"))).ToList());
    }

    private async Task<PublicCollectionPageDto<PublicMessageRevisionDto>> RevisionsAsync(PulujDbContext db, RawMessage raw, string? cursor, int limit, CancellationToken ct)
    {
        var scope = $"revisions:{raw.RawMessageId}";
        var offset = DecodeOffset(cursor, scope);
        var query = db.RawMessages.AsNoTracking().Where(x => x.SourceId == raw.SourceId && x.SourceMessageKey == raw.SourceMessageKey);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.RawMessageId).Skip(offset).Take(limit).ToListAsync(ct);
        var items = rows.Select(x => new PublicMessageRevisionDto(x.RawMessageId.ToString(), x.PublishedAt, x.SourceRevision, x.RawMessageId == raw.RawMessageId)).ToList();
        return new PublicCollectionPageDto<PublicMessageRevisionDto>(items, offset + items.Count < total ? EncodeOffset(offset + items.Count, scope) : null, total);
    }

    private async Task<List<PublicEntityRefDto>> AlertRelationsAsync(PulujDbContext db, long rawId, CancellationToken ct) => await db.AirAlerts.AsNoTracking()
        .Where(x => x.StartRawMessageId == rawId || x.EndRawMessageId == rawId).OrderBy(x => x.AirAlertId).Select(x => new PublicEntityRefDto("alert", x.AirAlertId.ToString(), null, x.StartRawMessageId == rawId ? "start_message" : "end_message")).ToListAsync(ct);

    private PublicMapLocatorDto Locator(LocationKind kind, int? placeId, Geometry? geometry, double? accuracy, DateTimeOffset at) => new(kind == LocationKind.Unknown ? null : kind.ToString().ToLowerInvariant(), placeId,
        refs.Place(placeId)?.Name, refs.RegionOf(placeId)?.Id, IncidentQueries.Precision(kind, accuracy, refs.Place(placeId)), geometry, at, geometry is null && placeId is null ? "no_reported_location" : null);

    private string? Classification(int? category, int? @class, int? family, int? model) => model is int m && refs.Models.TryGetValue(m, out var md) ? md.CanonicalName : family is int f && refs.Families.TryGetValue(f, out var fa) ? fa.Name : @class is int c && refs.Classes.TryGetValue(c, out var cl) ? cl.Name : category is int ca ? refs.Categories.GetValueOrDefault(ca)?.Name : null;
    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? value : null;
    private static string PublicOutcome(string value) => value.ToLowerInvariant() switch { "completed" or "no_facts" or "failed" or "unsupported" => value.ToLowerInvariant(), "awaiting_llm" => "awaiting_llm", _ => "parsed_projection_pending" };
    private static string LegacyOutcome(ProcessingStatus status) => status switch { ProcessingStatus.Pending or ProcessingStatus.InProgress => "pending", ProcessingStatus.Failed => "failed", ProcessingStatus.Skipped => "skipped", ProcessingStatus.Processed => "legacy_processed", _ => "unavailable" };
    private static int PageSize(int? value) => Math.Clamp(value ?? DefaultPageSize, 1, MaxPageSize);
    private static IEnumerable<string> Csv(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static HashSet<int> Integers(string? value, string name) { var values = new HashSet<int>(); foreach (var part in Csv(value)) { if (!int.TryParse(part, out var id) || id <= 0) throw new QueryException($"{name} must contain positive IDs"); values.Add(id); } return values; }
    private static HashSet<int> Codes(string? value, IReadOnlyDictionary<string, int> known, string name) { var values = new HashSet<int>(); foreach (var part in Csv(value)) { if (!known.TryGetValue(part, out var id)) throw new QueryException($"unknown {name} value: {part}"); values.Add(id); } return values; }
    private static HashSet<ConfidenceLevel> ConfidenceSet(string? value) { var result = new HashSet<ConfidenceLevel>(); foreach (var item in Csv(value)) { if (!Enum.TryParse<ConfidenceLevel>(item, true, out var level)) throw new QueryException("unknown confidence"); result.Add(level); } return result; }
    private static void EnsureDataset(string? value) { if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, LiveDataset, StringComparison.OrdinalIgnoreCase)) throw new QueryException("dataset is no longer active; reload", StatusCodes.Status409Conflict); }
    private static string Fingerprint(Query q, DateTimeOffset from, DateTimeOffset to)
    {
        var value = string.Join('|', [q.Q ?? "", q.SourceIds ?? "", q.HasResults?.ToString() ?? "", q.Outcome ?? "", q.EventKinds ?? "", q.CategoryIds ?? "", q.ClassIds ?? "", q.FamilyIds ?? "", q.ModelIds ?? "", q.RegionId?.ToString() ?? "", q.Location ?? "", q.Confidence ?? "", from.UtcTicks.ToString(), to.UtcTicks.ToString(), LiveDataset]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }
    private static string EncodeCursor(RawMessage raw, string fingerprint, string dataset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{raw.PublishedAt.UtcTicks}|{raw.RawMessageId}|{fingerprint}|{dataset}"));
    private static Cursor? DecodeCursor(string? cursor, string fingerprint, string dataset) { if (string.IsNullOrWhiteSpace(cursor)) return null; try { var p = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|'); if (p.Length != 4 || !long.TryParse(p[0], out var ticks) || !long.TryParse(p[1], out var id) || p[2] != fingerprint || p[3] != dataset) throw new QueryException("cursor does not match filters or dataset; reload", StatusCodes.Status409Conflict); return new Cursor(ticks, id, p[2], p[3]); } catch (FormatException) { throw new QueryException("invalid cursor"); } }
    private static string EncodeOffset(int offset, string scope) { var payload = $"p:{offset}:{scope}"; var signature = Convert.ToHexString(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes(payload)))[..16]; return Convert.ToBase64String(Encoding.UTF8.GetBytes($"p:{offset}:{signature}")); }
    private static int DecodeOffset(string? cursor, string scope) { if (string.IsNullOrWhiteSpace(cursor)) return 0; try { var p = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':'); if (p.Length != 3 || p[0] != "p" || !int.TryParse(p[1], out var offset) || offset < 0 || offset > 100_000) throw new QueryException("invalid cursor"); var expected = Convert.ToHexString(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes($"p:{offset}:{scope}")))[..16]; if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p[2]), Encoding.UTF8.GetBytes(expected))) throw new QueryException("invalid cursor"); return offset; } catch (FormatException) { throw new QueryException("invalid cursor"); } }
}
