using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>
/// Public, read-only catalogue adapter over the direct processor's target, track and alert read models.
/// models instead of introducing a second aggregate writer.  Every database query is bounded; the final merge holds at
/// most one page from each kind, rather than materialising a historical catalogue in memory.
/// </summary>
public sealed class PublicCatalogQueries(IDbContextFactory<PulujDbContext> factory, ReferenceCache refs, TimeProvider clock)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int DetailPreviewSize = 10;
    public const int MaxWindowDays = 366;
    private const string LiveDataset = "live";
    private const string BestEffortConsistency = "best_effort_live";
    private const int MaxCollectionOffset = 10_000;
    // This key is deliberately stable across processes: a continuation may be served by another replica. The HMAC
    // binds the otherwise opaque offset to its entity, collection, and dataset descriptor; it is not an auth token.
    private static readonly byte[] CollectionCursorKey = SHA256.HashData(Encoding.UTF8.GetBytes("puluj-public-catalogue-cursor-v1"));

    public sealed record Query(
        string? EntityKinds, string? Q, string? EventKinds, string? EventCategories, string? CategoryIds, string? ClassIds, string? FamilyIds, string? ModelIds,
        string? SourceIds, int? RegionId, string? Status, string? Confidence, string? Location, bool? HasResults, string? Sort,
        DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? PageSize, string? Dataset, string? HistoryBasis, DateTimeOffset? At);

    public sealed class QueryException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private sealed record Filter(
        HashSet<string> Kinds, HashSet<int> EventKinds, HashSet<int> Categories, HashSet<int> Classes, HashSet<int> Families, HashSet<int> Models,
        HashSet<int> Sources, HashSet<int> RegionPlaces, HashSet<string> States, HashSet<ConfidenceLevel> Confidences, bool? HasLocation,
        DateTimeOffset From, DateTimeOffset To, int Limit, Cursor? After, string Fingerprint, string Dataset, string? Q);

    private sealed record Cursor(long Ticks, int Rank, long Id, string Fingerprint, string Dataset);

    private sealed record Candidate(int Rank, long NumericId, DateTimeOffset At, PublicEntitySummaryDto Summary);

    private static readonly string[] EntityKinds = ["track", "alert", "observation"];

    public async Task<PublicEntityPageDto> ListAsync(Query query, CancellationToken ct)
    {
        var filter = Validate(query);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actualDataset = LiveDataset;
        EnsureDataset(filter.Dataset, actualDataset);
        filter = filter with { Dataset = actualDataset, After = DecodeCursor(query.Cursor, filter.Fingerprint, actualDataset) };

        var candidates = new List<Candidate>(filter.Limit * EntityKinds.Length);
        if (filter.Kinds.Contains("track"))
        {
            candidates.AddRange(await TracksAsync(db, filter, ct));
        }
        if (filter.Kinds.Contains("alert"))
        {
            candidates.AddRange(await AlertsAsync(db, filter, ct));
        }
        if (filter.Kinds.Contains("observation"))
        {
            candidates.AddRange(await ObservationsAsync(db, filter, ct));
        }

        var ordered = candidates
            .Where(c => IsAfterCursor(c, filter.After))
            .OrderByDescending(c => c.At).ThenBy(c => c.Rank).ThenByDescending(c => c.NumericId)
            .Take(filter.Limit + 1).ToList();
        var more = ordered.Count > filter.Limit;
        if (more)
        {
            ordered.RemoveAt(ordered.Count - 1);
        }
        var next = more ? EncodeCursor(ordered[^1], filter.Fingerprint, filter.Dataset) : null;
        return new PublicEntityPageDto(filter.From, filter.To, filter.Dataset, BestEffortConsistency, ordered.Select(x => x.Summary).ToList(), next,
            RefreshRecommended: query.Cursor is not null, Capabilities());
    }

    public async Task<PublicEntityDetailsDto?> DetailsAsync(string kind, long id, string? dataset, string? historyBasis, DateTimeOffset? at, CancellationToken ct)
    {
        ValidateDetail(kind, dataset, historyBasis, at);
        await using var db = await factory.CreateDbContextAsync(ct);
        EnsureDataset(dataset, LiveDataset);
        var actualDataset = LiveDataset;
        var entity = await OneAsync(db, kind, id, at, ct);
        if (entity is null)
        {
            return null;
        }
        var evidence = await EvidenceAsync(db, kind, id, null, DetailPreviewSize, Scope(kind, id, actualDataset, "evidence"), ct);
        var messages = await MessagesAsync(db, kind, id, null, DetailPreviewSize, Scope(kind, id, actualDataset, "messages"), ct);
        var relations = await RelationsAsync(db, kind, id, null, DetailPreviewSize, Scope(kind, id, actualDataset, "relations"), ct);
        var root = $"/api/public/entities/{kind}/{id}";
        var query = "?dataset=" + Uri.EscapeDataString(actualDataset);
        return new PublicEntityDetailsDto(entity,
            evidence,
            messages,
            relations,
            new Dictionary<string, string>
            {
                ["evidence"] = root + "/evidence" + query,
                ["messages"] = root + "/messages" + query,
                ["relations"] = root + "/relations" + query,
            }, Capabilities());
    }

    public async Task<PublicCollectionPageDto<PublicEvidenceDto>?> EvidencePageAsync(string kind, long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        ValidateCollection(kind, dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actualDataset = LiveDataset;
        EnsureDataset(dataset, actualDataset);
        return await ExistsAsync(db, kind, id, ct) ? await EvidenceAsync(db, kind, id, cursor, PageSize(limit), Scope(kind, id, actualDataset, "evidence"), ct) : null;
    }

    public async Task<PublicCollectionPageDto<PublicMessageRefDto>?> MessagePageAsync(string kind, long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        ValidateCollection(kind, dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actualDataset = LiveDataset;
        EnsureDataset(dataset, actualDataset);
        return await ExistsAsync(db, kind, id, ct) ? await MessagesAsync(db, kind, id, cursor, PageSize(limit), Scope(kind, id, actualDataset, "messages"), ct) : null;
    }

    public async Task<PublicCollectionPageDto<PublicEntityRefDto>?> RelationPageAsync(string kind, long id, string? cursor, int? limit, string? dataset, CancellationToken ct)
    {
        ValidateCollection(kind, dataset);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actualDataset = LiveDataset;
        EnsureDataset(dataset, actualDataset);
        return await ExistsAsync(db, kind, id, ct) ? await RelationsAsync(db, kind, id, cursor, PageSize(limit), Scope(kind, id, actualDataset, "relations"), ct) : null;
    }

    private Filter Validate(Query q)
    {
        if (!string.IsNullOrWhiteSpace(q.HistoryBasis) || q.At is not null)
        {
            // P14 owns projection pinning/history readiness.  Rejecting it is safer than returning today's live data as if it were historical.
            throw new QueryException("historical dataset is not ready; reload without historyBasis/at", StatusCodes.Status409Conflict);
        }
        var kinds = Set(q.EntityKinds, EntityKinds, "entityKinds");
        if (kinds.Count == 0) kinds = EntityKinds.ToHashSet(StringComparer.Ordinal);
        var to = q.To ?? clock.GetUtcNow();
        var from = q.From ?? to.AddHours(-24);
        if (from >= to) throw new QueryException("from must be before to");
        if (to - from > TimeSpan.FromDays(MaxWindowDays)) throw new QueryException($"the window may span at most {MaxWindowDays} days");
        if (!string.IsNullOrWhiteSpace(q.Dataset) && !string.Equals(q.Dataset, LiveDataset, StringComparison.OrdinalIgnoreCase)
            && !q.Dataset.StartsWith(LiveDataset + ":", StringComparison.OrdinalIgnoreCase))
            throw new QueryException("dataset is no longer active; reload", StatusCodes.Status409Conflict);
        if (!string.IsNullOrWhiteSpace(q.EventCategories))
        {
            var requestedCategories = Set(q.EventCategories, refs.EventKinds.Values.Select(k => k.Category.ToString().ToLowerInvariant()).Distinct(), "eventCategories");
            var allowedByCategory = refs.EventKinds.Values.Where(k => requestedCategories.Contains(k.Category.ToString().ToLowerInvariant())).Select(k => k.EventKindId).ToHashSet();
            var explicitKinds = Codes(q.EventKinds, refs.EventKinds.Values.ToDictionary(k => k.Code, k => k.EventKindId, StringComparer.OrdinalIgnoreCase), "eventKinds");
            if (explicitKinds.Count > 0) explicitKinds.IntersectWith(allowedByCategory); else explicitKinds = allowedByCategory;
            return Finish(explicitKinds);
        }
        return Finish(Codes(q.EventKinds, refs.EventKinds.Values.ToDictionary(k => k.Code, k => k.EventKindId, StringComparer.OrdinalIgnoreCase), "eventKinds"));

        Filter Finish(HashSet<int> eventKinds)
        {
        // An explicit intersection that yields no kinds must return an empty catalogue, not accidentally remove the
        // event-kind predicate and broaden the request.
        if ((Csv(q.EventKinds).Any() || !string.IsNullOrWhiteSpace(q.EventCategories)) && eventKinds.Count == 0) eventKinds.Add(-1);
        var categories = Integers(q.CategoryIds, "categoryIds");
        var classes = Integers(q.ClassIds, "classIds");
        var families = Integers(q.FamilyIds, "familyIds");
        var models = Integers(q.ModelIds, "modelIds");
        var sources = Integers(q.SourceIds, "sourceIds");
        var confidences = ConfidenceSet(q.Confidence);
        var states = Csv(q.Status).Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (states.Count > 0 && kinds.Contains("observation")) throw new QueryException("status is unsupported for observation; remove it or select a compatible entity kind");
        if (q.HasResults is not null) throw new QueryException("hasResults is unsupported by the public catalogue");
        if (!string.IsNullOrWhiteSpace(q.Sort) && !string.Equals(q.Sort, "time_desc", StringComparison.OrdinalIgnoreCase)) throw new QueryException("sort must be time_desc");
        var hasLocation = q.Location?.ToLowerInvariant() switch
        {
            null or "" or "all" => (bool?)null,
            "located" => true,
            "unlocated" => false,
            _ => throw new QueryException("location must be located|unlocated"),
        };
        var regionPlaces = q.RegionId is int region
            ? refs.Descendants(region).Append(region).ToHashSet()
            : [];
        var limit = Math.Clamp(q.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var dataset = string.IsNullOrWhiteSpace(q.Dataset) ? LiveDataset : q.Dataset!;
        var fingerprint = Fingerprint(q, kinds, from, to, LiveDataset);
        var after = (Cursor?)null;
        return new Filter(kinds, eventKinds, categories, classes, families, models, sources, regionPlaces, states, confidences, hasLocation,
            from, to, limit, after, fingerprint, dataset, string.IsNullOrWhiteSpace(q.Q) ? null : q.Q.Trim());
        }
    }

    private static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

    private void ValidateDetail(string kind, string? dataset, string? historyBasis, DateTimeOffset? at)
    {
        if (!EntityKinds.Contains(kind, StringComparer.Ordinal)) throw new QueryException("kind must be track|alert|observation");
        if (!string.IsNullOrWhiteSpace(historyBasis) || at is not null)
        {
            if (!string.Equals(historyBasis, "reconstructed", StringComparison.OrdinalIgnoreCase) || at is null)
                throw new QueryException("historical detail requires historyBasis=reconstructed and at", StatusCodes.Status400BadRequest);
            if (at > clock.GetUtcNow()) throw new QueryException("historical at cannot be in the future");
        }
        if (!string.IsNullOrWhiteSpace(dataset) && !string.Equals(dataset, LiveDataset, StringComparison.OrdinalIgnoreCase)
            && !dataset.StartsWith(LiveDataset + ":", StringComparison.OrdinalIgnoreCase)) throw new QueryException("dataset is no longer active; reload", StatusCodes.Status409Conflict);
    }

    private void ValidateCollection(string kind, string? dataset) => ValidateDetail(kind, dataset, null, null);

    private static void EnsureDataset(string? dataset, string actualDataset)
    {
        if (!string.IsNullOrWhiteSpace(dataset) && !string.Equals(dataset, LiveDataset, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dataset, actualDataset, StringComparison.OrdinalIgnoreCase))
            throw new QueryException("dataset is no longer active; reload", StatusCodes.Status409Conflict);
    }

    private async Task<List<Candidate>> TracksAsync(PulujDbContext db, Filter f, CancellationToken ct)
    {
        // The catalogue period is an evidence interval.  A track whose newest report is later may still have evidence
        // inside the requested historic window, so filtering LastSeenAt here would lose it.
        var targetIds = TargetMatchIds(db, f);
        var q = db.TargetTracks.AsNoTracking().Where(t => db.TrackTargets.Any(tt => tt.TargetTrackId == t.TargetTrackId && targetIds.Contains(tt.TargetId)));
        if (f.States.Count > 0) q = q.Where(t => f.States.Contains(t.Status.ToString().ToLower()));
        q = ApplyTrackCursor(q, f.After).OrderByDescending(t => t.LastSeenAt).ThenByDescending(t => t.TargetTrackId).Take(f.Limit + 1);
        var rows = await q.ToListAsync(ct);
        var ids = rows.Select(x => x.TargetTrackId).ToList();
        var matched = await TrackEvidenceAsync(db, ids, f, ct);
        return rows.Select(t => new Candidate(0, t.TargetTrackId, t.LastSeenAt, WithoutGeometry(TrackSummary(t, matched.GetValueOrDefault(t.TargetTrackId))))).ToList();
    }

    private async Task<List<Candidate>> AlertsAsync(PulujDbContext db, Filter f, CancellationToken ct)
    {
        // Alerts are interval facts: their catalogue window is overlap, not a start time test.  Taxonomy/confidence filters
        // have no alert semantics and therefore exclude alerts instead of pretending they were applied.
        if (f.EventKinds.Count > 0 || f.Categories.Count > 0 || f.Classes.Count > 0 || f.Families.Count > 0 || f.Models.Count > 0 || f.Confidences.Count > 0 || !string.IsNullOrWhiteSpace(fingerprintOnlyQ(f))) return [];
        var q = db.AirAlerts.AsNoTracking().Where(a => a.StartedAt < f.To && (a.EndedAt == null || a.EndedAt >= f.From));
        if (f.Sources.Count > 0) q = q.Where(a => f.Sources.Contains(a.SourceId));
        if (f.RegionPlaces.Count > 0) q = q.Where(a => f.RegionPlaces.Contains(a.PlaceId));
        if (f.States.Count > 0)
        {
            var active = f.States.Contains("active") || f.States.Contains("open");
            var closed = f.States.Contains("closed") || f.States.Contains("resolved");
            q = q.Where(a => (active && a.EndedAt == null) || (closed && a.EndedAt != null));
        }
        if (f.HasLocation is bool has) q = has ? q.Where(_ => true) : q.Where(_ => false);
        q = ApplyAlertCursor(q, f.After).OrderByDescending(a => a.StartedAt).ThenByDescending(a => a.AirAlertId).Take(f.Limit + 1);
        var rows = await q.ToListAsync(ct);
        return rows.Select(a => new Candidate(2, a.AirAlertId, a.StartedAt, WithoutGeometry(AlertSummary(a)))).ToList();
    }

    private async Task<List<Candidate>> ObservationsAsync(PulujDbContext db, Filter f, CancellationToken ct)
    {
        var q = TargetMatches(db.Targets.AsNoTracking(), f);
        // A fact represented by an available canonical aggregate is not duplicated in the list.  Its direct observation URL
        // remains available through DetailsAsync, including when the projection was switched on after the fact was written.
        q = q.Where(t => !db.TrackTargets.Any(tt => tt.TargetId == t.TargetId));
        q = ApplyTargetCursor(q, f.After).OrderByDescending(t => t.ObservedAt).ThenByDescending(t => t.TargetId).Take(f.Limit + 1);
        var rows = await q.ToListAsync(ct);
        return rows.Select(t => new Candidate(3, t.TargetId, t.ObservedAt, WithoutGeometry(ObservationSummary(t)))).ToList();
    }

    private IQueryable<Target> TargetMatches(IQueryable<Target> q, Filter f)
    {
        q = q.Where(t => t.ObservedAt >= f.From && t.ObservedAt < f.To);
        if (f.EventKinds.Count > 0) q = q.Where(t => t.EventKindId.HasValue && f.EventKinds.Contains(t.EventKindId.Value));
        if (f.Categories.Count > 0) q = q.Where(t => t.TargetCategoryId.HasValue && f.Categories.Contains(t.TargetCategoryId.Value));
        if (f.Classes.Count > 0) q = q.Where(t => t.TargetClassId.HasValue && f.Classes.Contains(t.TargetClassId.Value));
        if (f.Families.Count > 0) q = q.Where(t => t.TargetFamilyId.HasValue && f.Families.Contains(t.TargetFamilyId.Value));
        if (f.Models.Count > 0) q = q.Where(t => t.TargetModelId.HasValue && f.Models.Contains(t.TargetModelId.Value));
        if (f.Sources.Count > 0) q = q.Where(t => f.Sources.Contains(t.SourceId));
        if (f.RegionPlaces.Count > 0) q = q.Where(t => t.LocationPlaceId.HasValue && f.RegionPlaces.Contains(t.LocationPlaceId.Value));
        if (f.Confidences.Count > 0) q = q.Where(t => f.Confidences.Contains(t.Confidence));
        if (f.HasLocation is bool located) q = located ? q.Where(t => t.LocationKind != LocationKind.Unknown) : q.Where(t => t.LocationKind == LocationKind.Unknown);
        var qNeedle = fingerprintOnlyQ(f);
        if (qNeedle is not null)
        {
            var sourceIds = refs.Sources.Values.Where(s => Contains(s.Code, qNeedle) || Contains(s.Name, qNeedle)).Select(s => s.SourceId).ToList();
            var placeIds = refs.Places.Values.Where(p => Contains(p.Name, qNeedle)).Select(p => p.Id).ToList();
            var kindIds = refs.EventKinds.Values.Where(k => Contains(k.Code, qNeedle) || Contains(k.NameUk, qNeedle)).Select(k => k.EventKindId).ToList();
            // q is deliberately metadata-only; searching SegmentText/RawText would turn an innocent catalogue search into an unbounded raw scan.
            q = q.Where(t => sourceIds.Contains(t.SourceId) || (t.LocationPlaceId.HasValue && placeIds.Contains(t.LocationPlaceId.Value)) || (t.EventKindId.HasValue && kindIds.Contains(t.EventKindId.Value)));
        }
        return q;
    }

    private IQueryable<Target> TargetMatchesStatic(IQueryable<Target> q, Filter f)
    {
        // Observation's own effective time/source already passed above; retaining all target filters makes a source+taxonomy
        // conjunction apply to the same evidence row instead of producing a join-multiplication false match.
        if (f.EventKinds.Count > 0) q = q.Where(t => t.EventKindId.HasValue && f.EventKinds.Contains(t.EventKindId.Value));
        if (f.Categories.Count > 0) q = q.Where(t => t.TargetCategoryId.HasValue && f.Categories.Contains(t.TargetCategoryId.Value));
        if (f.Classes.Count > 0) q = q.Where(t => t.TargetClassId.HasValue && f.Classes.Contains(t.TargetClassId.Value));
        if (f.Families.Count > 0) q = q.Where(t => t.TargetFamilyId.HasValue && f.Families.Contains(t.TargetFamilyId.Value));
        if (f.Models.Count > 0) q = q.Where(t => t.TargetModelId.HasValue && f.Models.Contains(t.TargetModelId.Value));
        if (f.RegionPlaces.Count > 0) q = q.Where(t => t.LocationPlaceId.HasValue && f.RegionPlaces.Contains(t.LocationPlaceId.Value));
        if (f.Confidences.Count > 0) q = q.Where(t => f.Confidences.Contains(t.Confidence));
        if (f.HasLocation is bool located) q = located ? q.Where(t => t.LocationKind != LocationKind.Unknown) : q.Where(t => t.LocationKind == LocationKind.Unknown);
        var qNeedle = fingerprintOnlyQ(f);
        if (qNeedle is not null)
        {
            var sourceIds = refs.Sources.Values.Where(s => Contains(s.Code, qNeedle) || Contains(s.Name, qNeedle)).Select(s => s.SourceId).ToList();
            var placeIds = refs.Places.Values.Where(p => Contains(p.Name, qNeedle)).Select(p => p.Id).ToList();
            var kindIds = refs.EventKinds.Values.Where(k => Contains(k.Code, qNeedle) || Contains(k.NameUk, qNeedle)).Select(k => k.EventKindId).ToList();
            q = q.Where(t => sourceIds.Contains(t.SourceId) || (t.LocationPlaceId.HasValue && placeIds.Contains(t.LocationPlaceId.Value)) || (t.EventKindId.HasValue && kindIds.Contains(t.EventKindId.Value)));
        }
        return q;
    }

    private IQueryable<long> TargetMatchIds(PulujDbContext db, Filter f) => TargetMatches(db.Targets.AsNoTracking(), f).Select(t => t.TargetId);

    private bool MatchesTarget(Target target, Filter f)
    {
        if (f.EventKinds.Count > 0 && (!target.EventKindId.HasValue || !f.EventKinds.Contains(target.EventKindId.Value))) return false;
        if (f.Categories.Count > 0 && (!target.TargetCategoryId.HasValue || !f.Categories.Contains(target.TargetCategoryId.Value))) return false;
        if (f.Classes.Count > 0 && (!target.TargetClassId.HasValue || !f.Classes.Contains(target.TargetClassId.Value))) return false;
        if (f.Families.Count > 0 && (!target.TargetFamilyId.HasValue || !f.Families.Contains(target.TargetFamilyId.Value))) return false;
        if (f.Models.Count > 0 && (!target.TargetModelId.HasValue || !f.Models.Contains(target.TargetModelId.Value))) return false;
        if (f.RegionPlaces.Count > 0 && (!target.LocationPlaceId.HasValue || !f.RegionPlaces.Contains(target.LocationPlaceId.Value))) return false;
        if (f.Confidences.Count > 0 && !f.Confidences.Contains(target.Confidence)) return false;
        if (f.HasLocation is bool located && (target.LocationKind != LocationKind.Unknown) != located) return false;
        var q = fingerprintOnlyQ(f);
        if (q is not null)
        {
            var source = refs.Sources.GetValueOrDefault(target.SourceId);
            var place = refs.Place(target.LocationPlaceId);
            var eventKind = target.EventKindId is int id ? refs.EventKinds.GetValueOrDefault(id) : null;
            if (!Contains(source?.Code, q) && !Contains(source?.Name, q) && !Contains(place?.Name, q) && !Contains(eventKind?.Code, q) && !Contains(eventKind?.NameUk, q)) return false;
        }
        return true;
    }

    private static bool NeedsTargetEvidence(Filter f) => f.EventKinds.Count > 0 || f.Categories.Count > 0 || f.Classes.Count > 0 || f.Families.Count > 0 || f.Models.Count > 0 ||
        f.Sources.Count > 0 || f.RegionPlaces.Count > 0 || f.Confidences.Count > 0 || f.HasLocation is not null || fingerprintOnlyQ(f) is not null;
    private static bool NeedsTargetOnlyFilter(Filter f) => f.EventKinds.Count > 0 || f.Categories.Count > 0 || f.Classes.Count > 0 || f.Families.Count > 0 || f.Models.Count > 0 ||
        f.RegionPlaces.Count > 0 || f.Confidences.Count > 0 || f.HasLocation is not null || fingerprintOnlyQ(f) is not null;

    private async Task<Dictionary<long, EvidenceStats>> TrackEvidenceAsync(PulujDbContext db, List<long> ids, Filter f, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var all = db.TrackTargets.AsNoTracking().Where(x => ids.Contains(x.TargetTrackId));
        var total = await all.GroupBy(x => x.TargetTrackId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var matched = await all.Join(TargetMatches(db.Targets.AsNoTracking(), f), x => x.TargetId, t => t.TargetId, (x, t) => new { x.TargetTrackId, t.SourceId })
            .GroupBy(x => x.TargetTrackId).Select(g => new { Id = g.Key, Count = g.Count(), Sources = g.Select(x => x.SourceId).Distinct().ToList() }).ToListAsync(ct);
        return ids.ToDictionary(id => id, id => new EvidenceStats(total.GetValueOrDefault(id), matched.FirstOrDefault(x => x.Id == id)?.Count ?? 0,
            matched.FirstOrDefault(x => x.Id == id)?.Sources.Order().ToList() ?? []));
    }

    private sealed record EvidenceStats(int Total, int Matched, IReadOnlyList<int> Sources);

    private PublicEntitySummaryDto TrackSummary(TargetTrack t, EvidenceStats? evidence) => new("track", t.TargetTrackId.ToString(), $"Трек #{t.TargetTrackId}", null, null,
        Classification(t.TargetCategoryId, t.TargetClassId, t.TargetFamilyId, t.TargetModelId), t.LastSeenAt, t.Status.ToString().ToLowerInvariant(), t.TrackConfidence.ToString().ToLowerInvariant(),
        t.LastLocationKind.ToString().ToLowerInvariant(), t.LastLocationPlaceId, refs.Place(t.LastLocationPlaceId)?.Name, refs.RegionOf(t.LastLocationPlaceId)?.Id,
        evidence?.Sources ?? [], evidence?.Total ?? t.TargetCount, evidence?.Matched ?? t.TargetCount, t.LastLocation is not null || t.LastLocationPlaceId is not null,
        Locator(t.LastLocationKind, t.LastLocationPlaceId, t.LastLocation, t.LastLocationAccuracyKm, t.LastSeenAt));

    private PublicEntitySummaryDto AlertSummary(AirAlert a) => new("alert", a.AirAlertId.ToString(), $"Тривога: {refs.Place(a.PlaceId)?.Name ?? $"#{a.PlaceId}"}", "air_alert", "Повітряна тривога", null,
        a.StartedAt, a.EndedAt is null ? "active" : "closed", null, "place", a.PlaceId, refs.Place(a.PlaceId)?.Name, refs.RegionOf(a.PlaceId)?.Id,
        [a.SourceId], a.StartRawMessageId is null ? 0 : 1, a.StartRawMessageId is null ? 0 : 1, true,
        Locator(LocationKind.Region, a.PlaceId, null, refs.Place(a.PlaceId)?.RadiusKm, a.StartedAt));

    private PublicEntitySummaryDto ObservationSummary(Target t)
    {
        var kind = t.EventKindId is int id ? refs.EventKinds.GetValueOrDefault(id) : null;
        return new("observation", t.TargetId.ToString(), kind?.NameUk ?? t.EventType.ToString(), kind?.Code, kind?.NameUk,
            t.TargetCategoryId is int cat ? Classification(cat, t.TargetClassId, t.TargetFamilyId, t.TargetModelId) : null, t.ObservedAt, null, t.Confidence.ToString().ToLowerInvariant(),
            t.LocationKind.ToString().ToLowerInvariant(), t.LocationPlaceId, refs.Place(t.LocationPlaceId)?.Name, refs.RegionOf(t.LocationPlaceId)?.Id,
            [t.SourceId], 1, 1, t.Location is not null || t.LocationPlaceId is not null,
            Locator(t.LocationKind, t.LocationPlaceId, t.Location, t.LocationAccuracyKm, t.ObservedAt));
    }

    private PublicMapLocatorDto Locator(LocationKind kind, int? placeId, Geometry? geometry, double? accuracy, DateTimeOffset at) =>
        new(kind == LocationKind.Unknown ? null : kind.ToString().ToLowerInvariant(), placeId, refs.Place(placeId)?.Name, refs.RegionOf(placeId)?.Id,
            Precision(kind), geometry, at,
            geometry is null && placeId is null ? "no_reported_location" : null);

    private static PublicEntitySummaryDto WithoutGeometry(PublicEntitySummaryDto summary) => summary with { Map = summary.Map with { Geometry = null } };

    private static string Precision(LocationKind kind) => kind switch
    {
        LocationKind.Point => "point",
        LocationKind.City => "city",
        LocationKind.District => "district",
        LocationKind.Region or LocationKind.Area => "region",
        _ => "unknown",
    };

    private string? Classification(int category, int? @class, int? family, int? model) =>
        model is int m && refs.Models.TryGetValue(m, out var md) ? md.CanonicalName : family is int f && refs.Families.TryGetValue(f, out var fa) ? fa.Name :
        @class is int c && refs.Classes.TryGetValue(c, out var cl) ? cl.Name : refs.Categories.GetValueOrDefault(category)?.Name;

    private async Task<PublicEntitySummaryDto?> OneAsync(PulujDbContext db, string kind, long id, DateTimeOffset? at, CancellationToken ct) => kind switch
    {
        "track" => await TrackAtAsync(db, id, at, ct),
        "alert" => await AlertAtAsync(db, id, at, ct),
        "observation" => await db.Targets.AsNoTracking().FirstOrDefaultAsync(x => x.TargetId == id, ct) is { } o ? ObservationSummary(o) : null,
        _ => null,
    };

    private async Task<bool> ExistsAsync(PulujDbContext db, string kind, long id, CancellationToken ct) =>
        await OneAsync(db, kind, id, null, ct) is not null;

    /// <summary>Uses the append-only revision, rather than the current track row, for an exact reconstructed frame.</summary>
    private async Task<PublicEntitySummaryDto?> TrackAtAsync(PulujDbContext db, long id, DateTimeOffset? at, CancellationToken ct)
    {
        if (at is null)
            return await db.TargetTracks.AsNoTracking().FirstOrDefaultAsync(x => x.TargetTrackId == id, ct) is { } current ? TrackSummary(current, null) : null;
        var revision = await db.TargetTrackRevisions.AsNoTracking().Where(x => x.TargetTrackId == id && x.RevisionAt <= at)
            .OrderByDescending(x => x.RevisionAt).FirstOrDefaultAsync(ct);
        if (revision is not null)
            return new PublicEntitySummaryDto("track", id.ToString(), $"Трек #{id}", null, null,
                Classification(revision.TargetCategoryId, revision.TargetClassId, revision.TargetFamilyId, revision.TargetModelId), revision.LastSeenAt,
                revision.Status.ToString().ToLowerInvariant(), revision.TrackConfidence.ToString().ToLowerInvariant(), revision.LastLocationKind.ToString().ToLowerInvariant(),
                revision.LastLocationPlaceId, refs.Place(revision.LastLocationPlaceId)?.Name, refs.RegionOf(revision.LastLocationPlaceId)?.Id, [], revision.TargetCount, revision.TargetCount,
                revision.LastLocation is not null || revision.LastLocationPlaceId is not null,
                Locator(revision.LastLocationKind, revision.LastLocationPlaceId, revision.LastLocation, revision.LastLocationAccuracyKm, revision.LastSeenAt));
        // The detail exists, but a state before its first evidence does not.  Do not fall back to the current row.
        return await db.TargetTracks.AsNoTracking().AnyAsync(x => x.TargetTrackId == id, ct)
            ? new PublicEntitySummaryDto("track", id.ToString(), $"Трек #{id}", null, null, null, at.Value, "not_yet_available", null, null, null, null, null, [], 0, 0, false,
                new PublicMapLocatorDto(null, null, null, null, null, null, at, "before_first_evidence"))
            : null;
    }

    private async Task<PublicEntitySummaryDto?> AlertAtAsync(PulujDbContext db, long id, DateTimeOffset? at, CancellationToken ct)
    {
        var alert = await db.AirAlerts.AsNoTracking().FirstOrDefaultAsync(x => x.AirAlertId == id, ct);
        if (alert is null) return null;
        if (at is not null && at < alert.StartedAt)
            return AlertSummary(alert) with { At = at.Value, State = "not_yet_available", MapAvailable = false,
                Map = new PublicMapLocatorDto(null, null, null, null, null, null, at, "before_alert_started") };
        return AlertSummary(alert);
    }

    private async Task<PublicCollectionPageDto<PublicEvidenceDto>> EvidenceAsync(PulujDbContext db, string kind, long id, string? cursor, int limit, string scope, CancellationToken ct)
    {
        var offset = Offset(cursor, scope);
        return kind switch
        {
            "observation" => Page(await ObservationEvidenceAsync(db, id, ct), offset, limit, scope),
            "track" => await TrackEvidencePagedAsync(db, id, offset, limit, scope, ct),
            "alert" => Page(await AlertEvidenceAsync(db, id, ct), offset, limit, scope),
            _ => new PublicCollectionPageDto<PublicEvidenceDto>([], null, 0),
        };
    }

    private async Task<List<PublicEvidenceDto>> ObservationEvidenceAsync(PulujDbContext db, long id, CancellationToken ct)
    {
        var t = await db.Targets.AsNoTracking().FirstOrDefaultAsync(x => x.TargetId == id, ct);
        return t is null ? [] : [Evidence("observation", id, t, "self", null)];
    }

    private async Task<PublicCollectionPageDto<PublicEvidenceDto>> TrackEvidencePagedAsync(PulujDbContext db, long id, int offset, int limit, string scope, CancellationToken ct)
    {
        var query = db.TrackTargets.AsNoTracking().Where(x => x.TargetTrackId == id).Join(db.Targets.AsNoTracking(), x => x.TargetId, t => t.TargetId, (x, t) => new { x, t });
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.x.Sequence).Skip(offset).Take(limit).Select(x => new { x.x.AssociationConfidence, x.t }).ToListAsync(ct);
        var items = rows.Select(x => Evidence("track", id, x.t, "aggregate_evidence", x.AssociationConfidence)).ToList();
        return new PublicCollectionPageDto<PublicEvidenceDto>(items, offset + items.Count < total ? EncodeOffset(offset + items.Count, scope) : null, total);
    }

    private async Task<List<PublicEvidenceDto>> AlertEvidenceAsync(PulujDbContext db, long id, CancellationToken ct)
    {
        var a = await db.AirAlerts.AsNoTracking().FirstOrDefaultAsync(x => x.AirAlertId == id, ct);
        if (a is null) return [];
        return new[] { a.StartRawMessageId, a.EndRawMessageId }.Where(x => x is not null).Select((raw, index) =>
            new PublicEvidenceDto("alert", id.ToString(), null, null, a.SourceId, index == 0 ? a.StartedAt : a.EndedAt ?? a.StartedAt, "air_alert", null, "place", a.PlaceId, index == 0 ? "start_message" : "end_message", null)).ToList();
    }

    private PublicEvidenceDto Evidence(string entityKind, long entityId, Target t, string relation, double? score) => new(entityKind, entityId.ToString(), null, t.TargetId.ToString(), t.SourceId,
        t.ObservedAt, t.EventKindId is int k ? refs.EventKinds.GetValueOrDefault(k)?.Code : null,
        t.TargetCategoryId is int cat ? Classification(cat, t.TargetClassId, t.TargetFamilyId, t.TargetModelId) : null,
        t.LocationKind.ToString().ToLowerInvariant(), t.LocationPlaceId, relation, score is double s ? Math.Round(s, 3) : null);

    private async Task<PublicCollectionPageDto<PublicMessageRefDto>> MessagesAsync(PulujDbContext db, string kind, long id, string? cursor, int limit, string scope, CancellationToken ct)
    {
        // Correlated EXISTS preserves raw-message de-duplication in SQL.  Do not first materialise every evidence ID:
        // a very large aggregate must still cost one bounded page query.
        IQueryable<RawMessage> rows = kind switch
        {
            "observation" => db.RawMessages.AsNoTracking().Where(r => db.Targets.Any(t => t.TargetId == id && t.RawMessageId == r.RawMessageId)),
            "track" => db.RawMessages.AsNoTracking().Where(r => db.TrackTargets.Any(tt => tt.TargetTrackId == id && db.Targets.Any(t => t.TargetId == tt.TargetId && t.RawMessageId == r.RawMessageId))),
            "alert" => db.RawMessages.AsNoTracking().Where(r => db.AirAlerts.Any(a => a.AirAlertId == id && (a.StartRawMessageId == r.RawMessageId || a.EndRawMessageId == r.RawMessageId))),
            _ => db.RawMessages.AsNoTracking().Where(_ => false),
        };
        var offset = Offset(cursor, scope);
        var total = await rows.CountAsync(ct);
        var page = await rows.OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.RawMessageId).Skip(offset).Take(limit)
            .Select(r => new PublicMessageRefDto(r.RawMessageId.ToString(), r.SourceId, r.PublishedAt, r.Url)).ToListAsync(ct);
        return new PublicCollectionPageDto<PublicMessageRefDto>(page, offset + page.Count < total ? EncodeOffset(offset + page.Count, scope) : null, total);
    }

    private async Task<PublicCollectionPageDto<PublicEntityRefDto>> RelationsAsync(PulujDbContext db, string kind, long id, string? cursor, int limit, string scope, CancellationToken ct)
    {
        var offset = Offset(cursor, scope);
        return kind switch
        {
            "observation" => await ObservationRelationsPagedAsync(db, id, offset, limit, scope, ct),
            "track" => await TrackRelationsPagedAsync(db, id, offset, limit, scope, ct),
            _ => new PublicCollectionPageDto<PublicEntityRefDto>([], null, 0),
        };
    }

    private async Task<PublicCollectionPageDto<PublicEntityRefDto>> ObservationRelationsPagedAsync(PulujDbContext db, long id, int offset, int limit, string scope, CancellationToken ct)
    {
        // A deterministic two-family ordering lets both SQL queries remain limit-sized: target links first (ordered by
        // their composite key), aggregate memberships second.  We never load offset+limit rows merely to merge them.
        var linkQuery = db.TargetLinks.AsNoTracking().Where(l => l.FromTargetId == id || l.ToTargetId == id);
        var linkTotal = await linkQuery.CountAsync(ct);
        var trackQuery = db.TrackTargets.AsNoTracking().Where(x => x.TargetId == id);
        var trackTotal = await trackQuery.CountAsync(ct);
        var result = new List<PublicEntityRefDto>(limit);
        if (offset < linkTotal)
        {
            var links = await linkQuery.OrderBy(l => l.FromTargetId).ThenBy(l => l.ToTargetId).ThenBy(l => l.Kind).Skip(offset).Take(limit).ToListAsync(ct);
            result.AddRange(links.Select(l => l.FromTargetId == id
            ? new PublicEntityRefDto("observation", l.ToTargetId.ToString(), null, l.Kind.ToString().ToLowerInvariant(), l.Probability)
            : new PublicEntityRefDto("observation", l.FromTargetId.ToString(), null, l.Kind.ToString().ToLowerInvariant(), l.Probability)));
            if (result.Count < limit)
            {
                var tracks = await trackQuery.OrderBy(x => x.TargetTrackId).Take(limit - result.Count).Select(x => x.TargetTrackId).ToListAsync(ct);
                result.AddRange(tracks.Select(track => new PublicEntityRefDto("track", track.ToString(), null, "aggregate_membership")));
            }
        }
        else
        {
            var tracks = await trackQuery.OrderBy(x => x.TargetTrackId).Skip(offset - linkTotal).Take(limit).Select(x => x.TargetTrackId).ToListAsync(ct);
            result.AddRange(tracks.Select(track => new PublicEntityRefDto("track", track.ToString(), null, "aggregate_membership")));
        }
        var total = linkTotal + trackTotal;
        var page = result;
        return new PublicCollectionPageDto<PublicEntityRefDto>(page, offset + page.Count < total ? EncodeOffset(offset + page.Count, scope) : null, total);
    }

    private async Task<PublicCollectionPageDto<PublicEntityRefDto>> TrackRelationsPagedAsync(PulujDbContext db, long id, int offset, int limit, string scope, CancellationToken ct)
    {
        var query = db.TrackTargets.AsNoTracking().Where(x => x.TargetTrackId == id);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Sequence).Skip(offset).Take(limit).Select(x => new { x.TargetId, x.AssociationConfidence }).ToListAsync(ct);
        var items = rows.Select(x => new PublicEntityRefDto("observation", x.TargetId.ToString(), null, "aggregate_evidence", x.AssociationConfidence)).ToList();
        return new PublicCollectionPageDto<PublicEntityRefDto>(items, offset + items.Count < total ? EncodeOffset(offset + items.Count, scope) : null, total);
    }

    private static PublicCollectionPageDto<T> Page<T>(IReadOnlyList<T> rows, int offset, int limit, string scope)
    {
        var page = rows.Skip(offset).Take(limit).ToList();
        return new PublicCollectionPageDto<T>(page, offset + page.Count < rows.Count ? EncodeOffset(offset + page.Count, scope) : null, rows.Count);
    }

    private static string Scope(string kind, long id, string dataset, string collection) => $"{kind}:{id}:{dataset}:{collection}";

    private static string EncodeOffset(int offset, string scope)
    {
        var payload = $"p:{offset}:{scope}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(CollectionCursorKey, Encoding.ASCII.GetBytes(payload)))[..16];
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{payload}:{signature}"));
    }
    private static int Offset(string? cursor, string scope)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 3 || parts[0] != "p" || !int.TryParse(parts[1], out var offset) || offset < 0 || offset > MaxCollectionOffset)
                throw new QueryException("invalid cursor");
            var payload = $"p:{offset}:{scope}";
            var expected = Convert.ToHexString(HMACSHA256.HashData(CollectionCursorKey, Encoding.ASCII.GetBytes(payload)))[..16];
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]), Encoding.ASCII.GetBytes(expected)) ? offset : throw new QueryException("invalid cursor");
        }
        catch (FormatException) { throw new QueryException("invalid cursor"); }
    }

    private static bool IsAfterCursor(Candidate c, Cursor? after) => after is null || c.At < new DateTimeOffset(after.Ticks, TimeSpan.Zero) ||
        (c.At == new DateTimeOffset(after.Ticks, TimeSpan.Zero) && (c.Rank > after.Rank || c.Rank == after.Rank && c.NumericId < after.Id));
    private static string EncodeCursor(Candidate c, string fingerprint, string dataset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.At.UtcTicks}|{c.Rank}|{c.NumericId}|{fingerprint}|{dataset}"));
    private static Cursor? DecodeCursor(string? cursor, string fingerprint, string dataset)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var p = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (p.Length != 5 || !long.TryParse(p[0], out var ticks) || !int.TryParse(p[1], out var rank) || !long.TryParse(p[2], out var id) || p[3] != fingerprint || p[4] != dataset)
                throw new QueryException("cursor does not match filters or dataset; reload", StatusCodes.Status409Conflict);
            return new Cursor(ticks, rank, id, p[3], p[4]);
        }
        catch (FormatException) { throw new QueryException("invalid cursor"); }
    }

    private static string Fingerprint(Query q, HashSet<string> kinds, DateTimeOffset from, DateTimeOffset to, string dataset)
    {
        var value = string.Join('|', [string.Join(',', kinds.Order()), q.Q?.Trim().ToLowerInvariant() ?? "", q.EventKinds ?? "", q.EventCategories ?? "", q.CategoryIds ?? "", q.ClassIds ?? "", q.FamilyIds ?? "", q.ModelIds ?? "", q.SourceIds ?? "",
            q.RegionId?.ToString() ?? "", q.Status ?? "", q.Confidence ?? "", q.Location ?? "", q.HasResults?.ToString() ?? "", q.Sort ?? "", from.UtcTicks.ToString(), to.UtcTicks.ToString(), dataset]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    private static HashSet<string> Set(string? raw, IEnumerable<string> allowed, string name)
    {
        var values = Csv(raw).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (values.Any(x => !allowed.Contains(x, StringComparer.OrdinalIgnoreCase))) throw new QueryException($"{name} contains an unsupported value");
        return values;
    }
    private static IEnumerable<string> Csv(string? raw) => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static HashSet<int> Integers(string? raw, string name)
    {
        var values = new HashSet<int>();
        foreach (var part in Csv(raw)) if (int.TryParse(part, out var value) && value > 0) values.Add(value); else throw new QueryException($"{name} must be a comma list of positive integers");
        return values;
    }
    private static HashSet<int> Codes(string? raw, IReadOnlyDictionary<string, int> values, string name)
    {
        var ids = new HashSet<int>();
        foreach (var code in Csv(raw)) if (values.TryGetValue(code, out var id)) ids.Add(id); else throw new QueryException($"unknown {name}: {code}");
        return ids;
    }
    private static HashSet<ConfidenceLevel> ConfidenceSet(string? raw)
    {
        var values = new HashSet<ConfidenceLevel>();
        foreach (var item in Csv(raw)) if (Enum.TryParse<ConfidenceLevel>(item, true, out var value)) values.Add(value); else throw new QueryException($"unknown confidence: {item}");
        return values;
    }
    private static bool Contains(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    private static string? fingerprintOnlyQ(Filter f) => f.Q;

    private static IReadOnlyDictionary<string, bool> Capabilities() => new Dictionary<string, bool>
    {
        ["tracks"] = true, ["observations"] = true, ["alerts"] = true, ["historicalDataset"] = false,
        ["alertTaxonomy"] = false, ["alertConfidence"] = false,
    };

    private static IQueryable<TargetTrack> ApplyTrackCursor(IQueryable<TargetTrack> q, Cursor? cursor) => cursor is null ? q : q.Where(x => x.LastSeenAt < new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) || (x.LastSeenAt == new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) && (0 > cursor.Rank || 0 == cursor.Rank && x.TargetTrackId < cursor.Id)));
    private static IQueryable<AirAlert> ApplyAlertCursor(IQueryable<AirAlert> q, Cursor? cursor) => cursor is null ? q : q.Where(x => x.StartedAt < new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) || (x.StartedAt == new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) && (2 > cursor.Rank || 2 == cursor.Rank && x.AirAlertId < cursor.Id)));
    private static IQueryable<Target> ApplyTargetCursor(IQueryable<Target> q, Cursor? cursor) => cursor is null ? q : q.Where(x => x.ObservedAt < new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) || (x.ObservedAt == new DateTimeOffset(cursor.Ticks, TimeSpan.Zero) && (3 > cursor.Rank || 3 == cursor.Rank && x.TargetId < cursor.Id)));
}
