using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>
/// Read side of incidents (plan §8.5–8.6, ADR-0011): a bounded window with a keyset cursor, two history modes —
/// <c>effective</c> (the current state of every incident whose reports fall in the window: the reconstruction over all
/// known data) and <c>recorded</c> (what the system knew at <c>asOf</c>: the last revision recorded by then, from its
/// snapshot) — the details with evidence links and revisions, and the one-row reads the push adapter needs. Never the
/// whole history in one response: the window is capped, the page is capped, the snapshot list is capped.
/// </summary>
public sealed class IncidentQueries(IDbContextFactory<PulujDbContext> factory, ReferenceCache refs, TimeProvider clock, IOptions<MapOptions> options)
{
    public const string EffectiveMode = "effective";
    public const string RecordedMode = "recorded";
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 200;

    private MapOptions Map => options.Value;

    public sealed record Query(DateTimeOffset? From, DateTimeOffset? To, string? States, string? Kind, string? Category, string? Cursor, int? Limit, string? Mode, DateTimeOffset? AsOf, bool IncludeSuppressed);

    public sealed class QueryException(string message) : Exception(message);

    /// <summary>The window and page the caller asked for, validated and clamped: a span above the maximum is refused, not silently cut.</summary>
    public sealed record Bounds(DateTimeOffset From, DateTimeOffset To, int Limit, string Mode, DateTimeOffset? AsOf, IReadOnlySet<string> States, (DateTimeOffset At, long Id)? After);

    public Bounds Validate(Query q)
    {
        var now = clock.GetUtcNow();
        var mode = (q.Mode ?? EffectiveMode).ToLowerInvariant();
        if (mode is not (EffectiveMode or RecordedMode))
        {
            throw new QueryException($"mode must be {EffectiveMode} or {RecordedMode}");
        }
        DateTimeOffset? asOf = mode == RecordedMode ? q.AsOf ?? throw new QueryException("mode=recorded needs asOf") : null;
        var to = q.To ?? asOf ?? now;
        var from = q.From ?? to - Map.IncidentWindow;
        if (from >= to)
        {
            throw new QueryException("from must be before to");
        }
        if (to - from > Map.IncidentMaxWindow)
        {
            throw new QueryException($"the window may span at most {Map.IncidentMaxWindowDays} days");
        }
        var limit = Math.Clamp(q.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var states = (q.States ?? $"{Incident.Reported},{Incident.Confirmed},{Incident.Resolved}")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var known = new[] { Incident.Reported, Incident.Confirmed, Incident.Resolved, Incident.Retracted };
        if (states.Count == 0 || states.Any(s => !known.Contains(s)))
        {
            throw new QueryException("state must be a comma list of reported|confirmed|resolved|retracted");
        }
        return new Bounds(from, to, limit, mode, asOf, states, DecodeCursor(q.Cursor));
    }

    /// <summary>Keyset cursor: the last row's (last_reported_at, id), opaque to the client.</summary>
    public static string EncodeCursor(DateTimeOffset at, long id) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{at.UtcTicks}:{id}"));

    public static (DateTimeOffset At, long Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }
        try
        {
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(cursor)).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && long.TryParse(parts[1], out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : throw new QueryException("invalid cursor");
        }
        catch (FormatException)
        {
            throw new QueryException("invalid cursor");
        }
    }

    public async Task<IncidentPageDto> ListAsync(Query query, CancellationToken ct)
    {
        var b = Validate(query);
        await using var db = await factory.CreateDbContextAsync(ct);
        var kindId = query.Kind is { Length: > 0 } code ? refs.EventKinds.Values.FirstOrDefault(k => k.Code == code)?.EventKindId ?? -1 : (int?)null;
        var categoryKinds = query.Category is { Length: > 0 } cat
            ? refs.EventKinds.Values.Where(k => string.Equals(k.Category.ToString(), cat, StringComparison.OrdinalIgnoreCase)).Select(k => k.EventKindId).ToList()
            : null;
        if (b.Mode == RecordedMode)
        {
            return await ListRecordedAsync(db, b, kindId, categoryKinds, query.IncludeSuppressed, ct);
        }
        var rows = await ListQuery(db, b.From, b.To, b.States, query.IncludeSuppressed, kindId, categoryKinds, b.After, b.Limit).ToListAsync(ct);
        var more = rows.Count > b.Limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var links = await LinksAsync(db, rows.Select(r => r.IncidentId).ToList(), ct);
        var items = rows.Select(r => ToDto(r, links.GetValueOrDefault(r.IncidentId))).ToList();
        var next = more ? EncodeCursor(rows[^1].LastReportedAt, rows[^1].IncidentId) : null;
        return new IncidentPageDto(b.From, b.To, b.Mode, items, next, false);
    }

    /// <summary>The effective-mode page query: the window on last_reported_at, the state set, the keyset predicate, one row past the limit to learn whether there is a next page.</summary>
    private static IQueryable<Incident> ListQuery(PulujDbContext db, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> states, bool includeSuppressed, int? kindId, List<int>? categoryKinds, (DateTimeOffset At, long Id)? after, int limit)
    {
        var q = Active(db).Where(i => i.LastReportedAt >= from && i.LastReportedAt <= to && states.Contains(i.State));
        if (!includeSuppressed)
        {
            q = q.Where(i => !i.Suppressed);
        }
        if (kindId is int k)
        {
            q = q.Where(i => i.EventKindId == k);
        }
        if (categoryKinds is not null)
        {
            q = q.Where(i => categoryKinds.Contains(i.EventKindId));
        }
        if (after is { } a)
        {
            q = q.Where(i => i.LastReportedAt < a.At || (i.LastReportedAt == a.At && i.IncidentId < a.Id));
        }
        return q.OrderByDescending(i => i.LastReportedAt).ThenByDescending(i => i.IncidentId).Take(limit + 1);
    }

    /// <summary>The SQL EF generates for a page (query budget evidence: the plan is asserted on this text, not on a hand-written copy).</summary>
    public string ListSql(PulujDbContext db, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> states, int limit) =>
        ListQuery(db, from, to, states, false, null, null, null, limit).ToQueryString();

    /// <summary>
    /// «What the system knew then»: for every incident that existed by <c>asOf</c>, its last revision recorded by then; the window
    /// applies to that snapshot's last_reported_at. Bounded by the window on the current rows (an incident cannot have had reports
    /// later than it has now) and by the page.
    /// </summary>
    private async Task<IncidentPageDto> ListRecordedAsync(PulujDbContext db, Bounds b, int? kindId, List<int>? categoryKinds, bool includeSuppressed, CancellationToken ct)
    {
        var asOf = b.AsOf!.Value;
        var candidates = Active(db).Where(i => i.CreatedAt <= asOf && i.FirstReportedAt <= b.To && i.LastReportedAt >= b.From);
        if (kindId is int k)
        {
            candidates = candidates.Where(i => i.EventKindId == k);
        }
        if (categoryKinds is not null)
        {
            candidates = candidates.Where(i => categoryKinds.Contains(i.EventKindId));
        }
        var ids = await candidates.Select(i => i.IncidentId).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return new IncidentPageDto(b.From, b.To, b.Mode, [], null, false);
        }
        var revisions = await db.IncidentRevisions.AsNoTracking()
            .Where(r => ids.Contains(r.IncidentId) && r.RecordedAt <= asOf)
            .GroupBy(r => r.IncidentId)
            .Select(g => g.OrderByDescending(r => r.Revision).First())
            .ToListAsync(ct);
        var kinds = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.IncidentId)).Select(i => new { i.IncidentId, i.EventKindId, i.GenerationId }).ToDictionaryAsync(i => i.IncidentId, ct);
        var items = revisions
            .Select(r => FromSnapshot(r, kinds[r.IncidentId].EventKindId, kinds[r.IncidentId].GenerationId))
            .Where(d => d.LastReportedAt >= b.From && d.LastReportedAt <= b.To && b.States.Contains(d.State) && (includeSuppressed || !d.Suppressed))
            .Where(d => b.After is not { } a || d.LastReportedAt < a.At || (d.LastReportedAt == a.At && d.Id < a.Id))
            .OrderByDescending(d => d.LastReportedAt).ThenByDescending(d => d.Id)
            .Take(b.Limit + 1)
            .ToList();
        var more = items.Count > b.Limit;
        if (more)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new IncidentPageDto(b.From, b.To, b.Mode, items, more ? EncodeCursor(items[^1].LastReportedAt, items[^1].Id) : null, false);
    }

    /// <summary>The incidents of the live snapshot: last reported inside the incident window, visible (not suppressed, not retracted), newest first, capped.</summary>
    public async Task<(IReadOnlyList<IncidentDto> Items, bool Truncated)> LiveAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = now - Map.IncidentWindow;
        var rows = await Active(db)
            .Where(i => i.LastReportedAt >= since && !i.Suppressed && i.State != Incident.Retracted)
            .OrderByDescending(i => i.LastReportedAt).ThenByDescending(i => i.IncidentId)
            .Take(Map.IncidentSnapshotLimit + 1)
            .ToListAsync(ct);
        var truncated = rows.Count > Map.IncidentSnapshotLimit;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var links = await LinksAsync(db, rows.Select(r => r.IncidentId).ToList(), ct);
        return (rows.Select(r => ToDto(r, links.GetValueOrDefault(r.IncidentId))).ToList(), truncated);
    }

    /// <summary>The incidents as the system knew them at <c>at</c> (history snapshot): recorded mode over the incident window before <c>at</c>.</summary>
    public async Task<(IReadOnlyList<IncidentDto> Items, bool Truncated)> AtAsync(DateTimeOffset at, CancellationToken ct)
    {
        var items = new List<IncidentDto>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(new Query(at - Map.IncidentWindow, at, null, null, null, cursor, MaxPageSize, RecordedMode, at, false), ct);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null && items.Count < Map.IncidentSnapshotLimit);
        var truncated = items.Count > Map.IncidentSnapshotLimit || (cursor is not null && items.Count >= Map.IncidentSnapshotLimit);
        return (items.Take(Map.IncidentSnapshotLimit).ToList(), truncated);
    }

    /// <summary>One incident for the push adapter: null when it is outside the incident window (nothing to push).</summary>
    public async Task<IncidentDto?> OneAsync(long id, DateTimeOffset notBefore, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await Active(db).FirstOrDefaultAsync(i => i.IncidentId == id && i.LastReportedAt >= notBefore, ct);
        if (row is null)
        {
            return null;
        }
        var links = await LinksAsync(db, [id], ct);
        return ToDto(row, links.GetValueOrDefault(id));
    }

    public async Task<IncidentDetailsDto?> DetailsAsync(long id, int? revision, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // A suppressed (moderated) or shadow-generation incident is not public by id either: the admin API has its own read.
        var row = await Active(db).Include(i => i.Observations).FirstOrDefaultAsync(i => i.IncidentId == id && !i.Suppressed, ct);
        if (row is null)
        {
            return null;
        }
        var revisions = await db.IncidentRevisions.AsNoTracking().Where(r => r.IncidentId == id).OrderBy(r => r.Revision).ToListAsync(ct);
        IncidentDto incident;
        if (revision is int rev)
        {
            var asOf = revisions.FirstOrDefault(r => r.Revision == rev);
            if (asOf is null)
            {
                return null;
            }
            incident = FromSnapshot(asOf, row.EventKindId, row.GenerationId);
        }
        else
        {
            incident = ToDto(row, (row.Observations.Select(o => o.SourceId).Distinct().ToList(), row.Observations.Count));
        }
        var targetIds = row.Observations.Where(o => o.LegacyTargetId != null).Select(o => o.LegacyTargetId!.Value).ToList();
        var targets = await db.Targets.AsNoTracking().Include(t => t.RawMessage).Where(t => targetIds.Contains(t.TargetId)).ToDictionaryAsync(t => t.TargetId, ct);
        var observations = row.Observations.OrderBy(o => o.EffectiveAt).ThenBy(o => o.ObservationId).Select(o =>
        {
            var target = o.LegacyTargetId is long tid ? targets.GetValueOrDefault(tid) : null;
            return new IncidentObservationDto(o.ObservationId, o.LegacyTargetId, o.SourceId, refs.Sources.GetValueOrDefault(o.SourceId)?.Code, o.Relation, Math.Round(o.Score, 3), o.EffectiveAt, o.LinkedAt,
                target?.SegmentText, target?.RawMessage is { } raw ? DtoMapper.RawMessage(raw) : null);
        }).ToList();
        return new IncidentDetailsDto(incident, observations, revisions.Select(r => new IncidentRevisionDto(r.Revision, r.Change, r.EffectiveAt, r.RecordedAt, RedactActor(r.Actor), r.Reason)).ToList());
    }

    /// <summary>Only the active generation(s) reach the read side (§11.4): a shadow replay writes its own incidents and never shows on the live map.</summary>
    private static IQueryable<Incident> Active(PulujDbContext db) =>
        db.Incidents.AsNoTracking().Where(i => db.ProcessingGenerations.Any(g => g.GenerationId == i.GenerationId && g.IsActive));

    /// <summary>Public API: who changed an incident is `system` (the incident-worker's producer name, `incident-worker@instance`) or `operator` (an admin command); the name stays in the admin API.</summary>
    public static string RedactActor(string actor) => actor.StartsWith(SystemActorPrefix, StringComparison.Ordinal) ? "system" : "operator";

    /// <summary>The worker's actor as written by `IncidentStateWriter` (its producer name); anything else came through an admin command.</summary>
    public const string SystemActorPrefix = "incident-worker";

    /// <summary>Source ids and link count per incident in one query (the list never loads the links themselves).</summary>
    private static async Task<Dictionary<long, (List<int> Sources, int Count)>> LinksAsync(PulujDbContext db, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var rows = await db.IncidentObservations.AsNoTracking()
            .Where(o => ids.Contains(o.IncidentId))
            .GroupBy(o => o.IncidentId)
            .Select(g => new { g.Key, Sources = g.Select(o => o.SourceId).Distinct().ToList(), Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (r.Sources.Order().ToList(), r.Count));
    }

    public IncidentDto ToDto(Incident i, (List<int> Sources, int Count)? links)
    {
        var kind = refs.EventKinds.GetValueOrDefault(i.EventKindId);
        var (sources, count) = links ?? ([], 0);
        return new IncidentDto(i.IncidentId, kind?.Code ?? i.EventKindId.ToString(), kind?.NameUk ?? "", kind?.Category.ToString().ToLowerInvariant() ?? "incident",
            i.State, i.Suppressed, i.EventAt, i.FirstReportedAt, i.LastReportedAt,
            Location(i.LocationKind, i.LocationPlaceId, i.Geometry?.Centroid, i.AccuracyKm),
            i.Confidence.ToString().ToLowerInvariant(), i.SourceCount, i.Revision, i.ClosureReason, i.MergedIntoIncidentId,
            new IncidentProvenanceDto(i.CanonicalObservationId, count, sources, null, i.LastEventId, i.GenerationId));
    }

    /// <summary>§8.5 precision: a point only for located evidence, a marker for a city, an area for anything coarser; nothing without a place.</summary>
    public IncidentLocationDto? Location(LocationKind kind, int? placeId, Point? point, double? accuracyKm)
    {
        var place = refs.Place(placeId);
        if (place is null && point is null)
        {
            return null;
        }
        var region = refs.RegionOf(placeId);
        if (point is not null)
        {
            point.SRID = Geo.Srid;
        }
        return new IncidentLocationDto(kind.ToString().ToLowerInvariant(), placeId, place?.Name, region?.Id, region?.Id == place?.Id ? null : region?.Name, point, accuracyKm, Precision(kind, accuracyKm, place));
    }

    /// <summary>
    /// Precision comes from the evidence kind alone (review B2): the geometry of an incident is the centroid of the named place, so a small radius
    /// never makes it a "point"; only a fact the parser located as a point is one. The accuracy is the label/circle radius, not the classifier.
    /// </summary>
    public static string Precision(LocationKind kind, double? accuracyKm, ReferenceCache.PlaceInfo? place) => kind switch
    {
        LocationKind.Point => "point",
        LocationKind.City => "city",
        LocationKind.District => "district",
        LocationKind.Region or LocationKind.Area => "region",
        // Rows without a kind but with a place (older data): the place level decides — coarse levels first, a named area is never a city.
        LocationKind.Unknown when place?.Level is PlaceLevel.Region or PlaceLevel.Country or PlaceLevel.NamedArea => "region",
        LocationKind.Unknown when place?.Level is PlaceLevel.District or PlaceLevel.Hromada => "district",
        LocationKind.Unknown when place?.Level is PlaceLevel.City or PlaceLevel.Town or PlaceLevel.Village => "city",
        _ => "unknown",
    };

    /// <summary>An incident as one revision recorded it (the as-of view): the snapshot is the state the client saw at that revision.</summary>
    public IncidentDto FromSnapshot(IncidentRevision revision, int eventKindId, Guid generationId)
    {
        var s = revision.Snapshot.RootElement;
        var kind = refs.EventKinds.GetValueOrDefault(eventKindId);
        var location = s.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object ? loc : (JsonElement?)null;
        var kindName = location?.TryGetProperty("kind", out var lk) == true ? lk.GetString() : null;
        var placeId = location?.TryGetProperty("place_id", out var lp) == true && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : (int?)null;
        var accuracy = location?.TryGetProperty("accuracy_km", out var la) == true && la.ValueKind == JsonValueKind.Number ? la.GetDouble() : (double?)null;
        Point? point = null;
        if (location?.TryGetProperty("geometry", out var g) == true && g.TryGetProperty("coordinates", out var c) && c.GetArrayLength() == 2)
        {
            point = Geo.Point(c[0].GetDouble(), c[1].GetDouble());
        }
        var observations = s.TryGetProperty("observations", out var obs) && obs.ValueKind == JsonValueKind.Array ? obs.EnumerateArray().ToList() : [];
        var sources = observations.Select(o => o.TryGetProperty("source_id", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt32() : -1).Where(x => x >= 0).Distinct().Order().ToList();
        return new IncidentDto(revision.IncidentId, kind?.Code ?? Str(s, "event_kind_code") ?? "", kind?.NameUk ?? "", kind?.Category.ToString().ToLowerInvariant() ?? "incident",
            Str(s, "state") ?? Incident.Reported, s.TryGetProperty("suppressed", out var sup) && sup.ValueKind == JsonValueKind.True,
            Time(s, "event_at") ?? revision.EffectiveAt, Time(s, "first_reported_at") ?? revision.EffectiveAt, Time(s, "last_reported_at") ?? revision.EffectiveAt,
            Location(ParseKind(kindName), placeId, point, accuracy),
            Str(s, "confidence") ?? "unknown", s.TryGetProperty("source_count", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : sources.Count,
            revision.Revision, Str(s, "closure_reason"), s.TryGetProperty("merged_into_incident_id", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : null,
            new IncidentProvenanceDto(Guid.TryParse(Str(s, "canonical_observation_id"), out var canon) ? canon : null, observations.Count, sources, null, null, generationId));
    }

    private static string? Str(JsonElement s, string name) => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement s, string name) => Str(s, name) is { } v && DateTimeOffset.TryParse(v, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static LocationKind ParseKind(string? kind) => kind switch
    {
        "direction_only" => LocationKind.DirectionOnly,
        "region" => LocationKind.Region,
        "district" => LocationKind.District,
        "city" => LocationKind.City,
        "area" => LocationKind.Area,
        "point" => LocationKind.Point,
        _ => LocationKind.Unknown,
    };
}
