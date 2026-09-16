using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Api.Services;

/// <summary>Read side: live snapshot, historical replay from revisions (spec §20), track details with provenance (spec §18).</summary>
public sealed class SnapshotService(IDbContextFactory<PulujDbContext> factory, DtoMapper mapper, TimeProvider clock, ReferenceCache refs, IOptions<MapOptions> options, IncidentQueries incidents, ILogger<SnapshotService> logger)
{
    /// <summary>In history mode, tracks last reported earlier than this before `at` are not part of a snapshot.</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(3);
    /// <summary>Facts shown as event markers, rather than as moving tracks or regional alert state.</summary>
    private static readonly EventType[] MapEventTypes = [EventType.ExplosionReport, EventType.AirDefenseActivity, EventType.TargetCancelled];

    private MapOptions Map => options.Value;

    // The live snapshot is the same for everyone, so it is built once per MapOptions.SnapshotCache and shared: a
    // reconnect storm (every client re-fetches after a hub reconnect) costs one set of queries. One entry per
    // `activeOnly` value. The build runs without a request token: one client going away must not fail the others.
    private readonly object _cacheLock = new();
    private readonly Dictionary<bool, (DateTimeOffset BuiltAt, Task<SnapshotDto> Task)> _liveCache = [];

    /// <summary>
    /// What is on the map right now: tracks last reported inside the longest marker lifetime a viewer can pick
    /// (whatever their status; the client applies its own lifetime and "active only" on top) and every open alert
    /// (an open alert is a state, not an event: some regions have been under one continuously since 2022).
    /// </summary>
    public async Task<SnapshotDto> LiveAsync(bool activeOnly, CancellationToken ct)
    {
        if (Map.SnapshotCache <= TimeSpan.Zero)
        {
            return await BuildLiveAsync(activeOnly, ct);
        }
        var now = clock.GetUtcNow();
        Task<SnapshotDto> task;
        lock (_cacheLock)
        {
            if (!_liveCache.TryGetValue(activeOnly, out var entry) || entry.Task.IsFaulted || entry.Task.IsCanceled || now - entry.BuiltAt >= Map.SnapshotCache)
            {
                entry = (now, BuildLiveAsync(activeOnly, CancellationToken.None));
                _liveCache[activeOnly] = entry;
            }
            task = entry.Task;
        }
        return await task.WaitAsync(ct);
    }

    private async Task<SnapshotDto> BuildLiveAsync(bool activeOnly, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = now - Map.MaxLifetime;
        var tracks = await db.TargetTracks.AsNoTracking()
            .Where(t => t.LastSeenAt >= since && (!activeOnly || t.Status == TrackStatus.Active))
            .OrderByDescending(t => t.LastSeenAt)
            .ToListAsync(ct);
        var alerts = await db.AirAlerts.AsNoTracking()
            .Where(a => a.EndedAt == null)
            .OrderBy(a => a.StartedAt)
            .ToListAsync(ct);
        var ids = tracks.Select(t => t.TargetTrackId).ToList();
        var sources = await SourceIdsAsync(db, ids, null, ct);
        var fixes = await FixesAsync(db, ids, null, ct);
        var messages = await MessageIdsAsync(db, ids, null, ct);
        var events = await MapEventsAsync(db, since, null, ct);
        var (incidentRows, truncated) = await incidents.LiveAsync(now, ct);
        if (truncated)
        {
            logger.LogWarning("Live snapshot carries only the newest {Limit} incidents of the last {Hours} h; the client pages the rest through /api/incidents", Map.IncidentSnapshotLimit, Map.IncidentHours);
        }
        return new SnapshotDto(now, false, tracks.Select(t => mapper.Track(t, sources.GetValueOrDefault(t.TargetTrackId, []), fixes.GetValueOrDefault(t.TargetTrackId), messages.GetValueOrDefault(t.TargetTrackId))).ToList(), alerts.Select(mapper.Alert).ToList(), events, incidentRows, truncated);
    }

    public async Task<SnapshotDto> AtAsync(DateTimeOffset at, bool activeOnly, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = at - HistoryWindow;
        // Latest revision of every track as of `at`.
        var revisions = await db.TargetTrackRevisions
            .FromSqlInterpolated($"""
                SELECT DISTINCT ON (target_track_id) *
                FROM target_track_revisions
                WHERE revision_at <= {at} AND last_seen_at >= {since}
                ORDER BY target_track_id, revision_at DESC
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        var visible = revisions.Where(r => !activeOnly || r.Status == TrackStatus.Active).ToList();
        var trackIds = visible.Select(r => r.TargetTrackId).ToList();
        var sources = await SourceIdsAsync(db, trackIds, at, ct);
        var fixes = await FixesAsync(db, trackIds, at, ct);
        var messages = await MessageIdsAsync(db, trackIds, at, ct);
        var alerts = await db.AirAlerts.AsNoTracking()
            .Where(a => a.StartedAt <= at && (a.EndedAt == null || a.EndedAt > at))
            .OrderBy(a => a.StartedAt)
            .ToListAsync(ct);
        var events = await MapEventsAsync(db, since, at, ct);
        // Incidents in history mode are what the system knew at `at` (recorded mode, ADR-0011), never today's reconstruction.
        var (incidentRows, incidentsTruncated) = await incidents.AtAsync(at, ct);
        return new SnapshotDto(at, true,
            visible.OrderByDescending(r => r.LastSeenAt).Select(r => mapper.Track(r, sources.GetValueOrDefault(r.TargetTrackId, []), fixes.GetValueOrDefault(r.TargetTrackId), messages.GetValueOrDefault(r.TargetTrackId))).ToList(),
            alerts.Select(mapper.Alert).ToList(),
            events, incidentRows, incidentsTruncated);
    }

    /// <summary>Map events are individual facts, not tracks: only show a reported location, never an invented point.</summary>
    private async Task<IReadOnlyList<TargetDto>> MapEventsAsync(PulujDbContext db, DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
    {
        var rows = await db.Targets.AsNoTracking()
            .Include(o => o.RawMessage)
            .Where(o => MapEventTypes.Contains(o.EventType) && o.Location != null && o.ObservedAt >= since && (until == null || o.ObservedAt <= until))
            .OrderByDescending(o => o.ObservedAt).ThenByDescending(o => o.TargetId)
            .ToListAsync(ct);
        return rows.Select(o => mapper.Target(o, null)).ToList();
    }

    /// <summary>A replay window may span at most this much.</summary>
    private static readonly TimeSpan MaxReplayWindow = TimeSpan.FromHours(36);

    /// <summary>
    /// The tracks of a replay window with every position they were reported at (from their revisions): the client draws
    /// each one moving between consecutive reports. Tracks last reported up to HistoryWindow before the window start
    /// are included so that whatever was still on the map at the start is there.
    /// </summary>
    public async Task<ReplayDto> ReplayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }
        if (to - from > MaxReplayWindow)
        {
            from = to - MaxReplayWindow;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = from - HistoryWindow;
        var revisions = await db.TargetTrackRevisions.AsNoTracking()
            .Where(r => r.LastSeenAt >= since && r.LastSeenAt <= to && r.RevisionAt <= to)
            .OrderBy(r => r.TargetTrackId).ThenBy(r => r.LastSeenAt).ThenBy(r => r.RevisionAt)
            .ToListAsync(ct);
        var tracks = new List<ReplayTrackDto>();
        foreach (var group in revisions.GroupBy(r => r.TargetTrackId))
        {
            var samples = new List<ReplaySampleDto>();
            TargetTrackRevision? last = null;
            foreach (var r in group)
            {
                last = r;
                var loc = mapper.Location(r.LastLocationKind, r.LastLocationPlaceId, r.LastLocation, r.LastLocationAccuracyKm);
                if (loc?.Point is null)
                {
                    continue;
                }
                var direction = r.DirectionKind == DirectionKind.Unknown ? null : r.DirectionDeg;
                var sample = new ReplaySampleDto(r.LastSeenAt, loc.Point, direction, r.LastLocationKind == LocationKind.DirectionOnly);
                // A revision that changed nothing about the position (a new source, a count) adds no sample.
                if (samples.Count > 0 && samples[^1].At == sample.At && samples[^1].Point.EqualsExact(sample.Point))
                {
                    continue;
                }
                samples.Add(sample);
            }
            if (samples.Count == 0 || last is null)
            {
                continue;
            }
            tracks.Add(new ReplayTrackDto(group.Key, mapper.TargetType(last.TargetCategoryId, last.TargetClassId, last.TargetFamilyId, last.TargetModelId), samples));
        }
        return new ReplayDto(from, to, tracks);
    }

    /// <summary>One track as the map draws it; null when it does not exist or was last reported before <paramref name="notBefore"/> (the realtime bridge skips those).</summary>
    public async Task<TrackDto?> TrackAsync(long id, CancellationToken ct, DateTimeOffset? notBefore = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await db.TargetTracks.AsNoTracking().FirstOrDefaultAsync(x => x.TargetTrackId == id && (notBefore == null || x.LastSeenAt >= notBefore), ct);
        return t is null ? null : mapper.Track(t, (await SourceIdsAsync(db, [id], null, ct)).GetValueOrDefault(id, []), (await FixesAsync(db, [id], null, ct)).GetValueOrDefault(id), (await MessageIdsAsync(db, [id], null, ct)).GetValueOrDefault(id));
    }

    /// <summary>How many of the newest raw messages travel with a track (for "neighbours by message").</summary>
    private const int MaxMessages = 6;

    private static async Task<Dictionary<long, List<long>>> MessageIdsAsync(PulujDbContext db, List<long> trackIds, DateTimeOffset? at, CancellationToken ct)
    {
        if (trackIds.Count == 0)
        {
            return [];
        }
        var rows = await db.TrackTargets.AsNoTracking()
            .Where(l => trackIds.Contains(l.TargetTrackId) && (at == null || l.Target!.ObservedAt <= at))
            .Select(l => new { l.TargetTrackId, l.Target!.RawMessageId, l.Target.ObservedAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TargetTrackId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ObservedAt).Select(r => r.RawMessageId).Distinct().Take(MaxMessages).ToList());
    }

    /// <summary>How many earlier positions travel with a track (the current one included).</summary>
    private const int MaxFixes = 4;

    /// <summary>
    /// The chain behind each track's newest target, from the kinematic links the database keeps (puluj_target_chain):
    /// at every step the most probable predecessor, with its probability. Oldest first, the current target last.
    /// </summary>
    private async Task<Dictionary<long, List<FixDto>>> FixesAsync(PulujDbContext db, List<long> trackIds, DateTimeOffset? at, CancellationToken ct)
    {
        if (trackIds.Count == 0)
        {
            return [];
        }
        // The newest target of each track (as of `at` in history mode).
        var heads = await db.TrackTargets.AsNoTracking()
            // Duplicates carry no kinematic links of their own (the trigger moves them onto the original), so the head is
            // the newest original report.
            .Where(l => trackIds.Contains(l.TargetTrackId) && (at == null || l.Target!.ObservedAt <= at) && l.Target!.DuplicateOfTargetId == null)
            .GroupBy(l => l.TargetTrackId)
            .Select(g => new { TrackId = g.Key, TargetId = g.OrderByDescending(l => l.Target!.ObservedAt).ThenByDescending(l => l.TargetId).Select(l => l.TargetId).First() })
            .ToListAsync(ct);
        if (heads.Count == 1)
        {
            return new Dictionary<long, List<FixDto>> { [heads[0].TrackId] = await ChainAsync(db, heads[0].TargetId, ct) };
        }
        // Every chain in one round trip (a snapshot has dozens of tracks): the function is applied laterally to each head.
        var trackIds1 = heads.Select(h => h.TrackId).ToArray();
        var headIds = heads.Select(h => h.TargetId).ToArray();
        var rows = await db.Database.SqlQuery<TrackChainRow>($"""
            SELECT h.track_id, c.step, c.target_id, c.probability
            FROM unnest({trackIds1}::bigint[], {headIds}::bigint[]) AS h(track_id, target_id)
            CROSS JOIN LATERAL puluj_target_chain(h.target_id, {MaxFixes - 1}) AS c
            """).ToListAsync(ct);
        var ids = rows.Select(r => r.TargetId).Distinct().ToList();
        var targets = await db.Targets.AsNoTracking().Where(t => ids.Contains(t.TargetId)).ToDictionaryAsync(t => t.TargetId, ct);
        var result = new Dictionary<long, List<FixDto>>();
        foreach (var group in rows.GroupBy(r => r.TrackId))
        {
            var fixes = new List<FixDto>();
            foreach (var r in group.OrderByDescending(r => r.Step))
            {
                if (targets.TryGetValue(r.TargetId, out var t) && mapper.Fix(t, r.Probability) is { } fix)
                {
                    fixes.Add(fix);
                }
            }
            result[group.Key] = fixes;
        }
        return result;
    }

    // Unmapped query types follow the snake_case naming convention: the SQL columns are used as they are.
    private sealed record ChainRow(int Step, long TargetId, double Probability);
    private sealed record TrackChainRow(long TrackId, int Step, long TargetId, double Probability);
    private sealed record FamilyRow(int Generation, long FromTargetId, long ToTargetId, int Kind, double Probability, double PathProbability, bool Ancestral);

    /// <summary>
    /// The family of the track's newest target (puluj_target_family): every probable predecessor `depth` generations
    /// back — the whole fork, not only the best chain — and where else each of those predecessors could have flown,
    /// with the probability of each link and along each path.
    /// </summary>
    public async Task<PredecessorsDto?> PredecessorsAsync(long trackId, int depth, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var head = await db.TrackTargets.AsNoTracking()
            .Where(l => l.TargetTrackId == trackId && l.Target!.DuplicateOfTargetId == null)
            .OrderByDescending(l => l.Target!.ObservedAt).ThenByDescending(l => l.TargetId)
            .Select(l => l.TargetId)
            .FirstOrDefaultAsync(ct);
        if (head == 0)
        {
            return null;
        }
        var rows = await db.Database.SqlQuery<FamilyRow>($"SELECT generation, from_target_id, to_target_id, kind, probability, path_probability, ancestral FROM puluj_target_family({head}, {depth})").ToListAsync(ct);
        var ids = rows.SelectMany(r => new[] { r.FromTargetId, r.ToTargetId }).Append(head).Distinct().ToList();
        var targets = await db.Targets.AsNoTracking().Where(t => ids.Contains(t.TargetId)).ToDictionaryAsync(t => t.TargetId, ct);
        // An ancestor's generation is its shortest way up from the head; a relative sits one below the ancestor it hangs from.
        var generation = new Dictionary<long, int>();
        var ancestral = new HashSet<long> { head };
        foreach (var r in rows.Where(r => r.Ancestral))
        {
            ancestral.Add(r.FromTargetId);
            generation[r.FromTargetId] = Math.Min(generation.GetValueOrDefault(r.FromTargetId, int.MaxValue), r.Generation);
        }
        foreach (var r in rows.Where(r => !r.Ancestral))
        {
            generation.TryAdd(r.ToTargetId, r.Generation - 1);
        }
        var nodes = new List<PredecessorDto>();
        foreach (var (id, t) in targets)
        {
            var fix = mapper.Fix(t, 1);
            if (fix is null)
            {
                continue;
            }
            var type = mapper.TargetType(t.TargetCategoryId ?? 0, t.TargetClassId, t.TargetFamilyId, t.TargetModelId);
            var direction = t.DirectionKind == DirectionKind.Unknown ? null : t.DirectionDeg;
            nodes.Add(new PredecessorDto(id, generation.GetValueOrDefault(id, 0), ancestral.Contains(id), fix.At, fix.PlaceName, fix.Kind, fix.Point, fix.AccuracyKm, fix.Approach,
                t.SegmentText is { Length: > 90 } st ? st[..90] + "…" : t.SegmentText, type.DisplayMode, type.Label, direction));
        }
        var links = rows
            .Where(r => targets.ContainsKey(r.FromTargetId) && targets.ContainsKey(r.ToTargetId))
            .Select(r => new PredecessorLinkDto(r.FromTargetId, r.ToTargetId, r.Generation, r.Ancestral, ((TargetLinkKind)r.Kind).ToString(), r.Probability, r.PathProbability))
            .ToList();
        return new PredecessorsDto(trackId, head, nodes.OrderBy(n => n.Generation).ThenByDescending(n => n.At).ToList(), links);
    }

    private async Task<List<FixDto>> ChainAsync(PulujDbContext db, long headTargetId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ChainRow>($"SELECT step, target_id, probability FROM puluj_target_chain({headTargetId}, {MaxFixes - 1})").ToListAsync(ct);
        var ids = rows.Select(r => r.TargetId).ToList();
        var targets = await db.Targets.AsNoTracking().Where(t => ids.Contains(t.TargetId)).ToDictionaryAsync(t => t.TargetId, ct);
        var fixes = new List<FixDto>();
        foreach (var r in rows.OrderByDescending(r => r.Step))
        {
            if (targets.TryGetValue(r.TargetId, out var t) && mapper.Fix(t, r.Probability) is { } fix)
            {
                fixes.Add(fix);
            }
        }
        return fixes;
    }

    private static async Task<Dictionary<long, int[]>> SourceIdsAsync(PulujDbContext db, List<long> trackIds, DateTimeOffset? at, CancellationToken ct)
    {
        if (trackIds.Count == 0)
        {
            return [];
        }
        var rows = await db.TrackTargets.AsNoTracking()
            .Where(l => trackIds.Contains(l.TargetTrackId) && (at == null || l.Target!.ObservedAt <= at))
            .Select(l => new { l.TargetTrackId, l.Target!.SourceId })
            .Distinct()
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TargetTrackId).ToDictionary(g => g.Key, g => g.Select(r => r.SourceId).OrderBy(x => x).ToArray());
    }

    public async Task<TrackDetailsDto?> TrackDetailsAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await db.TargetTracks.AsNoTracking().FirstOrDefaultAsync(x => x.TargetTrackId == id, ct);
        if (t is null)
        {
            return null;
        }
        var links = await db.TrackTargets.AsNoTracking()
            .Where(l => l.TargetTrackId == id)
            .Include(l => l.Target!).ThenInclude(o => o.RawMessage)
            .OrderBy(l => l.Target!.ObservedAt).ThenBy(l => l.TargetId)
            .ToListAsync(ct);
        var sourceIds = links.Select(l => l.Target!.SourceId).Distinct().OrderBy(x => x).ToArray();
        var messageIds = links.OrderByDescending(l => l.Target!.ObservedAt).Select(l => l.Target!.RawMessageId).Distinct().Take(MaxMessages).ToList();
        var targetIds = links.Select(l => l.TargetId).ToList();
        var targetLinks = await db.TargetLinks.AsNoTracking()
            .Where(l => targetIds.Contains(l.FromTargetId) || targetIds.Contains(l.ToTargetId))
            .ToListAsync(ct);
        IReadOnlyList<TargetLinkDto> LinksOf(long targetId) => targetLinks
            .Where(l => l.FromTargetId == targetId || l.ToTargetId == targetId)
            .OrderByDescending(l => l.Probability)
            .Select(l => new TargetLinkDto(l.FromTargetId == targetId ? l.ToTargetId : l.FromTargetId, l.Kind.ToString(), l.Probability,
                l.FromTargetId == targetId ? "to" : "from", l.DistanceKm, l.MinutesApart, l.HeadingDiffDeg, l.RequiredMinutes))
            .ToList();
        var head = links.Where(l => l.Target!.DuplicateOfTargetId == null).OrderByDescending(l => l.Target!.ObservedAt).ThenByDescending(l => l.TargetId).Select(l => l.TargetId).FirstOrDefault();
        var fixes = head == 0 ? [] : await ChainAsync(db, head, ct);
        return new TrackDetailsDto(mapper.Track(t, sourceIds, fixes, messageIds), links.Select(l => mapper.Target(l.Target!, l.AssociationConfidence, id, LinksOf(l.TargetId))).ToList());
    }

    /// <summary>
    /// Newest targets first (feed panel). Duplicates are kept — they are provenance too. Live (no `until`) never reaches
    /// further back than the feed window; a replay window never further than MaxReplayWindow before its end.
    /// </summary>
    public async Task<IReadOnlyList<TargetDto>> RecentTargetsAsync(DateTimeOffset since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        var floor = until is { } u ? u - MaxReplayWindow : clock.GetUtcNow() - Map.FeedWindow;
        if (since < floor)
        {
            since = floor;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Targets.AsNoTracking()
            .Include(o => o.RawMessage)
            .Where(o => o.ObservedAt >= since && (until == null || o.ObservedAt <= until))
            .OrderByDescending(o => o.ObservedAt).ThenByDescending(o => o.TargetId)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(ct);
        var trackOf = await TrackOfAsync(db, rows.Select(o => o.TargetId).ToList(), ct);
        return rows.Select(o => mapper.Target(o, null, trackOf.TryGetValue(o.TargetId, out var t) ? t : null)).ToList();
    }

    /// <summary>Which track each target belongs to (the feed highlights the lines behind the selected target).</summary>
    private static async Task<Dictionary<long, long>> TrackOfAsync(PulujDbContext db, List<long> targetIds, CancellationToken ct)
    {
        if (targetIds.Count == 0)
        {
            return [];
        }
        return await db.TrackTargets.AsNoTracking()
            .Where(l => targetIds.Contains(l.TargetId))
            .GroupBy(l => l.TargetId)
            .Select(g => new { g.Key, TrackId = g.Min(l => l.TargetTrackId) })
            .ToDictionaryAsync(x => x.Key, x => x.TrackId, ct);
    }

    /// <summary>
    /// Alerts concerning a place over the last `hours`, ended ones included, newest first: the region window's history.
    /// "Concerning" is hierarchical — on the place, on a place that covers it (a Kyiv district under a city-wide alert,
    /// a raion under an oblast-wide one) or on a place inside it (a raion or hromada of the oblast).
    /// </summary>
    public async Task<IReadOnlyList<AlertDto>> AlertHistoryAsync(int placeId, double hours, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = clock.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 14));
        var ids = refs.Related(placeId).ToArray();
        var rows = await db.AirAlerts.AsNoTracking()
            .Where(a => ids.Contains(a.PlaceId) && (a.EndedAt == null || a.EndedAt >= since))
            .OrderByDescending(a => a.StartedAt)
            .Take(200)
            .ToListAsync(ct);
        return rows.Select(mapper.Alert).ToList();
    }

    /// <summary>One report with its links; null when it does not exist or was observed before <paramref name="notBefore"/>.</summary>
    public async Task<TargetDto?> TargetAsync(long id, CancellationToken ct, DateTimeOffset? notBefore = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var o = await db.Targets.AsNoTracking().Include(x => x.RawMessage).FirstOrDefaultAsync(x => x.TargetId == id && (notBefore == null || x.ObservedAt >= notBefore), ct);
        if (o is null)
        {
            return null;
        }
        var trackOf = await TrackOfAsync(db, [id], ct);
        // Its kinematic links both ways, most probable first: the link window shows the numbers behind a probability.
        var links = (await db.TargetLinks.AsNoTracking().Where(l => l.FromTargetId == id || l.ToTargetId == id).ToListAsync(ct))
            .OrderByDescending(l => l.Probability)
            .Select(l => new TargetLinkDto(l.FromTargetId == id ? l.ToTargetId : l.FromTargetId, l.Kind.ToString(), l.Probability,
                l.FromTargetId == id ? "to" : "from", l.DistanceKm, l.MinutesApart, l.HeadingDiffDeg, l.RequiredMinutes))
            .ToList();
        return mapper.Target(o, null, trackOf.TryGetValue(id, out var t) ? t : null, links);
    }

    /// <summary>One alert; null when it does not exist or ended before <paramref name="endedNotBefore"/> (open alerts always qualify, whatever their age).</summary>
    public async Task<AlertDto?> AlertAsync(long id, CancellationToken ct, DateTimeOffset? endedNotBefore = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var a = await db.AirAlerts.AsNoTracking().FirstOrDefaultAsync(x => x.AirAlertId == id && (endedNotBefore == null || x.EndedAt == null || x.EndedAt >= endedNotBefore), ct);
        return a is null ? null : mapper.Alert(a);
    }

    public async Task<IReadOnlyList<TimelineBucketDto>> TimelineAsync(DateTimeOffset from, DateTimeOffset to, int bucketMinutes, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bucket = TimeSpan.FromMinutes(Math.Clamp(bucketMinutes, 1, 24 * 60));
        var targets = await db.Targets.AsNoTracking()
            .Where(o => o.ObservedAt >= from && o.ObservedAt < to && o.DuplicateOfTargetId == null)
            .Select(o => o.ObservedAt)
            .ToListAsync(ct);
        var opened = await db.TargetTracks.AsNoTracking()
            .Where(t => t.FirstSeenAt >= from && t.FirstSeenAt < to)
            .Select(t => t.FirstSeenAt)
            .ToListAsync(ct);
        var alerts = await db.AirAlerts.AsNoTracking()
            .Where(a => a.StartedAt < to && (a.EndedAt == null || a.EndedAt >= from))
            .Select(a => new { a.StartedAt, a.EndedAt })
            .ToListAsync(ct);

        var buckets = new List<TimelineBucketDto>();
        for (var start = from; start < to; start += bucket)
        {
            var end = start + bucket;
            buckets.Add(new TimelineBucketDto(start,
                targets.Count(x => x >= start && x < end),
                opened.Count(x => x >= start && x < end),
                alerts.Count(a => a.StartedAt < end && (a.EndedAt == null || a.EndedAt >= start))));
        }
        return buckets;
    }

    private sealed record RatingRow(int SourceId, DateOnly Day, int Targets, int Copies, int CopiedBy, double? AvgLeadSeconds, double? Rating);

    /// <summary>Source rating over the last `days`: per-day rows from the source_rating_daily view, copy pairs, and copy groups.</summary>
    public async Task<SourceRatingReportDto> SourceRatingAsync(int days, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        days = Math.Clamp(days, 1, 90);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Kyiv).DateTime);
        var since = today.AddDays(-(days - 1));
        var rows = await db.Database.SqlQuery<RatingRow>($"""
            SELECT source_id, day, targets, copies, copied_by, avg_lead_seconds, rating::float AS rating
            FROM source_rating_daily WHERE day >= {since}
            """).ToListAsync(ct);
        var copies = await db.SourceCopies.AsNoTracking().Where(c => c.Day >= since).ToListAsync(ct);
        var pairs = copies.GroupBy(c => (c.CopierSourceId, c.OriginalSourceId))
            .Select(g => new SourceCopyDto(g.Key.CopierSourceId, g.Key.OriginalSourceId, g.Sum(c => c.Count), g.Sum(c => c.DelaySecondsSum) / Math.Max(1, g.Sum(c => c.Count))))
            .OrderByDescending(p => p.Count)
            .ToList();
        var totals = rows.GroupBy(r => r.SourceId).ToDictionary(g => g.Key, g => g.Sum(r => r.Targets));
        // A copy relation that carries at least 3 facts and at least 30 % of the copier's output joins the two sources
        // into one group (transitively).
        var parent = new Dictionary<int, int>();
        int Find(int x) => parent.TryGetValue(x, out var p) && p != x ? parent[x] = Find(p) : (parent.TryAdd(x, x) ? x : x);
        foreach (var p in pairs.Where(p => p.Count >= 3 && p.Count >= 0.3 * totals.GetValueOrDefault(p.CopierId)))
        {
            var a = Find(p.CopierId);
            var b = Find(p.OriginalId);
            if (a != b)
            {
                parent[a] = b;
            }
        }
        var groups = parent.Keys.GroupBy(Find).Where(g => g.Count() > 1).Select((g, i) => (Index: i + 1, Members: g.OrderBy(x => x).ToList())).ToList();
        var groupOf = groups.SelectMany(g => g.Members.Select(m => (m, g.Index))).ToDictionary(x => x.m, x => x.Index);
        var dayList = Enumerable.Range(0, days).Select(i => since.AddDays(i)).ToList();
        var sources = refs.Sources.Values.OrderByDescending(s => s.Priority).Select(src =>
        {
            var mine = rows.Where(r => r.SourceId == src.SourceId).ToDictionary(r => r.Day);
            var series = dayList.Select(d => mine.TryGetValue(d, out var r) ? new SourceRatingDayDto(d, r.Targets, r.Copies, r.CopiedBy, r.AvgLeadSeconds, r.Rating) : new SourceRatingDayDto(d, 0, 0, 0, null, null)).ToList();
            var weighted = series.Where(x => x.Rating is not null && x.Targets > 0).ToList();
            var rating = weighted.Count == 0 ? (double?)null : Math.Round(weighted.Sum(x => x.Rating!.Value * x.Targets) / weighted.Sum(x => x.Targets), 3);
            return new SourceRatingDto(src.SourceId, src.Name, src.TrustLevel, rating, groupOf.TryGetValue(src.SourceId, out var g) ? g : null, series);
        }).ToList();
        return new SourceRatingReportDto(dayList, sources, pairs, groups.Select(g => (IReadOnlyList<int>)g.Members).ToList());
    }

    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
}
