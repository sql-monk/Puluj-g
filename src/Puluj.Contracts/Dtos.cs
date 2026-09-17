using NetTopologySuite.Geometries;

namespace Puluj.Contracts;

/// <summary>Class/model behaviour the client needs for fading and ETA (spec §12, §17). Speeds are ranges, never a single number.</summary>
public sealed record SpeedProfileDto(double? MinKmh, double? MaxKmh, bool EtaEnabled);

public sealed record TargetTypeDto(
    string CategoryCode, string CategoryName,
    string? ClassCode, string? ClassName,
    string? FamilyCode, string? FamilyName,
    string? ModelCode, string? ModelName,
    string DisplayMode, int FadeMinutes, SpeedProfileDto SpeedProfile)
{
    /// <summary>Deepest identified level, for labels ("Shahed-136", "Shahed family", "Крилата ракета").</summary>
    public string Label => ModelName ?? FamilyName ?? ClassName ?? CategoryName;
}

public sealed record LocationDto(string Kind, int? PlaceId, string? PlaceName, int? RegionId, string? RegionName, Point? Point, double? AccuracyKm);

public sealed record DirectionDto(double Degrees, string Kind, string Confidence);

/// <summary>One earlier reported position of a track ("was near Romny at 21:40"): the crumbs drawn behind the marker.</summary>
/// <param name="Approach">The report only named a destination: the point is that place, the object was on the way to it.</param>
/// <param name="Probability">That this earlier report is the same object as the next one in the chain (1 for the current position).</param>
public sealed record FixDto(DateTimeOffset At, string? PlaceName, string Kind, Point Point, double? AccuracyKm, bool Approach, double Probability);

public sealed record TrackDto(
    long Id,
    string Status,
    string? ClosedReason,
    TargetTypeDto Type,
    string ModelConfidence,
    string TrackConfidence,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset UpdatedAt,
    LocationDto? LastLocation,
    LineString? TrackGeometry,
    DirectionDto? Direction,
    int? ObjectCount,
    int TargetCount,
    int DistinctSourceCount,
    IReadOnlyList<int> SourceIds,
    /// <summary>The last few distinct reported positions, oldest first, the current one last.</summary>
    IReadOnlyList<FixDto> Fixes,
    /// <summary>Raw messages behind the newest targets: tracks sharing one are "neighbours by message".</summary>
    IReadOnlyList<long> MessageIds);

/// <param name="Level">Unknown | Yellow | Red (regional administrations publish levels; alerts.in.ua does not).</param>
/// <param name="Location">Where to draw it when the place has no polygon (raion towns): point + radius.</param>
/// <param name="AncestorIds">The place's parents, nearest first, up to the root (`[raion, oblast]` for a hromada, `[oblast]`
/// for a raion, empty for an oblast or Kyiv): the client tells from these which alerts cover a place and which lie inside it.</param>
public sealed record AlertDto(long Id, int PlaceId, string PlaceName, string AlertType, string Level, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, LocationDto? Location, IReadOnlyList<int> AncestorIds);

/// <param name="Events">Short-lived, non-track facts that have a reported location (explosions, air-defence activity, and threat cancellations).</param>
/// <param name="Incidents">P11 (additive, ADR-0011): incidents last reported inside the incident window; the `events` list stays for the compatibility window.</param>
/// <param name="IncidentsTruncated">True when the window holds more incidents than the snapshot cap: the client pages the rest through GET /api/incidents.</param>
public sealed record SnapshotDto(DateTimeOffset At, bool Historical, IReadOnlyList<TrackDto> Tracks, IReadOnlyList<AlertDto> Alerts, IReadOnlyList<TargetDto> Events,
    IReadOnlyList<IncidentDto>? Incidents = null, bool? IncidentsTruncated = null);

/// <summary>
/// The live map's time windows (GET /api/map/config), so the client and the server agree on what is still on the map:
/// the lifetime choices of the panel (the largest one is the window of the live snapshot and of track pushes) and the
/// window of the feed.
/// </summary>
public sealed record MapConfigDto(IReadOnlyList<int> LifetimeOptionsMinutes, int MaxLifetimeMinutes, double FeedHours, double? IncidentHours = null);

/// <summary>One reported position of a track in a replay window: where it was said to be, when, and on what course.</summary>
public sealed record ReplaySampleDto(DateTimeOffset At, Point Point, double? DirectionDeg, bool Approach);
/// <summary>A track over a replay window: its class and every reported position in order, oldest first.</summary>
public sealed record ReplayTrackDto(long Id, TargetTypeDto Type, IReadOnlyList<ReplaySampleDto> Samples);
/// <summary>Everything a timelapse of the window needs in one payload; the client interpolates between the samples.</summary>
public sealed record ReplayDto(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ReplayTrackDto> Tracks);

public sealed record SourceDto(int Id, string Code, string Name, string Type, double TrustLevel, string? Url);

public sealed record RawMessageDto(long Id, string SourceMessageId, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt, string? Text, string? Url);

/// <summary>One target with its full provenance chain (spec §18, §31).</summary>
public sealed record TargetDto(
    long Id,
    DateTimeOffset ObservedAt,
    string EventType,
    TargetTypeDto? Type,
    string ModelConfidence,
    string ClassificationConfidence,
    string Confidence,
    LocationDto? Location,
    LocationDto? Origin,
    LocationDto? Destination,
    DirectionDto? Direction,
    int? ObjectCount,
    bool ObjectCountIsApproximate,
    string IdentificationMethod,
    string? IdentificationSource,
    string? SegmentText,
    long? DuplicateOfTargetId,
    double? AssociationConfidence,
    SourceDto Source,
    RawMessageDto RawMessage,
    long? TrackId,
    IReadOnlyList<TargetLinkDto> Links,
    /// <summary>Plan §8.2 catalog code (event_kinds.code), next to the legacy <c>EventType</c> for the compatibility window; null until backfilled.</summary>
    string? EventKindCode = null);

/// <summary>Plan §8.2 event catalog entry as the read-side sees it (GET /api/event-kinds). Presentation only; legacy EventType is the enum name this kind maps to, or null.</summary>
public sealed record EventKindDto(
    int Id, string Code, string NameUk, string Category, string? DefaultSeverity, string? StateModel, bool RequiresLocationForMap,
    string? RenderMode, string? MapColor, string? MapIcon, TimeSpan? MapLifetime, bool CreatesIncident, bool MapVisible, int SortOrder,
    string? LegacyEventType, int PolicyVersion);

/// <summary>A link from this target to another: which one, how ("continuation" = kinematic predecessor, "duplicate"), how probable,
/// whether the other one is earlier ("from") or later ("to"), and the kinematics behind the number.</summary>
/// <summary>A probable earlier report of the same object: generation 1 = directly before the track's newest target, 2 = before that.</summary>
/// <summary>
/// A node of the selected target's family. Generation = how many generations above the head (0 = the head's own,
/// i.e. siblings and cousins). Ancestral = on the head's own ancestry (parent, grandparent); otherwise a relative:
/// where an ancestor could have flown instead. DisplayMode / TypeLabel / DirectionDeg let the map draw the node as
/// the target it is (class glyph, turned by its course).
/// </summary>
public sealed record PredecessorDto(long TargetId, int Generation, bool Ancestral, DateTimeOffset At, string? PlaceName, string Kind, Point? Point, double? AccuracyKm, bool Approach, string? Label,
    string DisplayMode, string TypeLabel, double? DirectionDeg);

/// <param name="Probability">The link's own probability.</param>
/// <param name="PathProbability">Product of the probabilities from the head down to this link.</param>
/// <summary>Generation = the generation of `From` above the head. Ancestral = a link on the head's own ancestry.</summary>
public sealed record PredecessorLinkDto(long FromTargetId, long ToTargetId, int Generation, bool Ancestral, string Kind, double Probability, double PathProbability);

public sealed record PredecessorsDto(long TrackId, long HeadTargetId, IReadOnlyList<PredecessorDto> Targets, IReadOnlyList<PredecessorLinkDto> Links);

public sealed record TargetLinkDto(long TargetId, string Kind, double Probability, string Direction, double? DistanceKm, double? MinutesApart, double? HeadingDiffDeg, double? RequiredMinutes);

public sealed record TrackDetailsDto(TrackDto Track, IReadOnlyList<TargetDto> Targets);

public sealed record PlaceDto(int Id, string Name, string Level, int? ParentId, string? ParentName, double Lon, double Lat, double RadiusKm, int Population);

/// <param name="ParentId">Set for city districts (Kyiv): the city-region they belong to.</param>
public sealed record RegionDto(int Id, string Name, string Level, string CountryCode, int? ParentId, Geometry Geometry);

public sealed record TaxonomyModelDto(int Id, string Code, string Name, string? Manufacturer, string? Country, SpeedProfileDto SpeedProfile);
public sealed record TaxonomyFamilyDto(int Id, string Code, string Name, IReadOnlyList<TaxonomyModelDto> Models);
public sealed record TaxonomyClassDto(int Id, string Code, string Name, string DisplayMode, int FadeMinutes, SpeedProfileDto SpeedProfile, IReadOnlyList<TaxonomyFamilyDto> Families);
public sealed record TaxonomyCategoryDto(int Id, string Code, string Name, IReadOnlyList<TaxonomyClassDto> Classes);
public sealed record TaxonomyDto(IReadOnlyList<TaxonomyCategoryDto> Categories);

public sealed record TimelineBucketDto(DateTimeOffset From, int Targets, int TracksOpened, int Alerts);

/// <summary>Development-only: inject a message as a collector would. Payload (optional) is stored as RawPayload, e.g. an alerts.in.ua alert.</summary>
public sealed record IngestRequest(string SourceCode, string? Text, DateTimeOffset? PublishedAt, string? SourceMessageId, System.Text.Json.JsonElement? Payload);

// Statistics page (GET /api/stats/{targets|alerts|sources|recognition}?from&to): one payload per tab, aggregated for
// one period in Europe/Kyiv buckets. Same for every viewer; per-bucket arrays are aligned with Period.BucketStarts.
public sealed record StatsPeriodDto(DateTimeOffset From, DateTimeOffset To, string Bucket, IReadOnlyList<DateTimeOffset> BucketStarts);
/// <summary>One requested URL filter that this metric cannot apply without changing its population.</summary>
public sealed record StatsUnavailableFilterDto(string Key, string Reason);
/// <summary>Filter and population disclosure carried by every statistics tab.</summary>
public sealed record StatsFilterMetaDto(IReadOnlyList<string> Applied, IReadOnlyList<StatsUnavailableFilterDto> Unavailable, string TimeBasis, string Population, string? ExclusionReason);
public sealed record StatsCategoryDto(string Code, string Name);
public sealed record StatsClassDto(string Code, string Name, string CategoryCode, long Targets, long Tracks, long ObjectsDeclared);
/// <summary>Targets located in a region (oblast, Kyiv); Id is null for the folded "other" row.</summary>
public sealed record StatsRegionDto(int? Id, string Name, long Targets);
public sealed record StatsRouteDto(int FromId, string FromName, int ToId, string ToName, long Count);
public sealed record StatsSliceDto(string Key, string Label, long Count);
/// <summary>"What flew": facts (targets that are not a repeat) and tracks per bucket and category, classes, regions, routes, hour x weekday.</summary>
public sealed record StatsTargetsDto(
    StatsPeriodDto Period,
    StatsFilterMetaDto Filters,
    long Targets,
    long Tracks,
    long ObjectsDeclared,
    IReadOnlyList<StatsCategoryDto> Categories,
    // One row per bucket; inner arrays are counts per category in the order of Categories.
    IReadOnlyList<IReadOnlyList<long>> TargetsByBucket,
    IReadOnlyList<IReadOnlyList<long>> TracksByBucket,
    IReadOnlyList<StatsClassDto> ByClass,
    IReadOnlyList<StatsRegionDto> ByRegion,
    // Facts whose location does not resolve to a region (none, direction only, abroad).
    long Unlocated,
    IReadOnlyList<StatsRouteDto> Routes,
    // 7 rows (Monday first) x 24 hours, Europe/Kyiv.
    IReadOnlyList<IReadOnlyList<long>> HourWeekday);
public sealed record StatsAlertRegionDto(int Id, string Name, long Count, double Hours);
public sealed record StatsAlertDayDto(DateOnly Day, long Count, double Hours);
/// <summary>Region-level air-raid alerts: hours under alert and declarations per bucket, per region, durations, hour of day, top days.</summary>
public sealed record StatsAlertsDto(
    StatsPeriodDto Period,
    StatsFilterMetaDto Filters,
    long Alerts,
    double AlertHours,
    // Alerts still open at the end of the period.
    long OpenAtEnd,
    IReadOnlyList<long> DeclaredByBucket,
    IReadOnlyList<double> HoursByBucket,
    IReadOnlyList<StatsAlertRegionDto> ByRegion,
    IReadOnlyList<StatsSliceDto> Durations,
    // 24 entries: alerts declared per hour of the day, Europe/Kyiv.
    IReadOnlyList<long> DeclaredByHour,
    IReadOnlyList<StatsAlertDayDto> TopDays);
/// <summary>Series: messages per bucket, aligned with Period.BucketStarts.</summary>
public sealed record StatsSourceDto(int Id, string Code, string Name, long Messages, long Processed, long WithTargets, long Targets, double? MedianLagSeconds, IReadOnlyList<long> Series);
public sealed record StatsSourcesDto(StatsPeriodDto Period, StatsFilterMetaDto Filters, StatsFilterMetaDto FactFilters, long Messages, long Processed, long WithTargets, long Targets, IReadOnlyList<StatsSourceDto> Sources);
/// <summary>How the pipeline read the period: distributions of the facts and the share of processed messages without a fact per bucket.</summary>
public sealed record StatsRecognitionDto(
    StatsPeriodDto Period,
    StatsFilterMetaDto Filters,
    long Targets,
    long Processed,
    long WithTargets,
    IReadOnlyList<StatsSliceDto> EventTypes,
    IReadOnlyList<StatsSliceDto> Methods,
    IReadOnlyList<StatsSliceDto> Confidence,
    IReadOnlyList<StatsSliceDto> LocationKinds,
    IReadOnlyList<long> ProcessedByBucket,
    IReadOnlyList<long> WithTargetsByBucket);

// ---- P11 incidents (plan §8.5–8.6, ADR-0011): additive read-side contracts ----

/// <summary>
/// Where an incident is, and how precisely (§8.5, ADR-0011 п.2). <c>Precision</c> comes from the evidence's location kind alone — never from a small
/// radius, because the point is the centroid of the named place: <c>point</c> — a fact the parser located as a point; <c>city</c> — a city/town marker
/// (a marker with a precision label, never an address); <c>district</c>/<c>region</c> — an area: the client draws the place polygon or the error circle of
/// <c>AccuracyKm</c>, never a small pin at the centroid; <c>unknown</c> — no usable place (the incident stays off the map). <c>AccuracyKm</c> is the radius/label.
/// </summary>
public sealed record IncidentLocationDto(string Kind, int? PlaceId, string? PlaceName, int? RegionId, string? RegionName, Point? Point, double? AccuracyKm, string Precision);

/// <summary>What the state rests on: the canonical observation, how many observations/sources, the policy that linked them, the last event (causal chain).</summary>
public sealed record IncidentProvenanceDto(Guid? CanonicalObservationId, int ObservationCount, IReadOnlyList<int> SourceIds, string? PolicyVersion, Guid? LastEventId, Guid GenerationId);

/// <summary>One incident as the map/feed sees it (GET /api/incidents, snapshot.incidents, hub IncidentUpserted/IncidentRevised). Static events: no course, ETA or forecast.</summary>
public sealed record IncidentDto(
    long Id,
    string Kind,
    string KindName,
    string Category,
    string State,
    bool Suppressed,
    DateTimeOffset EventAt,
    DateTimeOffset FirstReportedAt,
    DateTimeOffset LastReportedAt,
    IncidentLocationDto? Location,
    string Confidence,
    int SourceCount,
    int Revision,
    string? ClosureReason,
    long? MergedIntoIncidentId,
    IncidentProvenanceDto Provenance);

/// <summary>An observation linked to an incident: the evidence link with its relation and the report it came from (the same raw-message fields as TargetDto).</summary>
public sealed record IncidentObservationDto(Guid ObservationId, long? TargetId, int SourceId, string? SourceCode, string Relation, double Score, DateTimeOffset EffectiveAt, DateTimeOffset LinkedAt,
    string? SegmentText, RawMessageDto? RawMessage);

/// <summary>One revision of an incident: what changed, when it took effect (evidence time) and when the system recorded it (clock). <c>Actor</c> is redacted on the public API to <c>system</c> | <c>operator</c>.</summary>
public sealed record IncidentRevisionDto(int Revision, string Change, DateTimeOffset EffectiveAt, DateTimeOffset RecordedAt, string Actor, string? Reason);

/// <summary>GET /api/incidents/{id}: the incident (as of a revision when asked), its evidence links and its revision history.</summary>
public sealed record IncidentDetailsDto(IncidentDto Incident, IReadOnlyList<IncidentObservationDto> Observations, IReadOnlyList<IncidentRevisionDto> Revisions);

/// <summary>A page of incidents inside a bounded time window; <c>NextCursor</c> continues the keyset, <c>Truncated</c> says the window holds more than the snapshot cap.</summary>
public sealed record IncidentPageDto(DateTimeOffset From, DateTimeOffset To, string Mode, IReadOnlyList<IncidentDto> Items, string? NextCursor, bool Truncated);

// ---- U04 public catalogue -------------------------------------------------
// These are deliberately separate from the map DTOs above.  Map DTOs predate the
// catalogue and include such things as raw text and rendered geometry; the public
// catalogue is an allow-list and keeps large evidence collections paged.

/// <summary>A small, stable reference to a public catalogue item.  IDs are strings because the database uses bigint.</summary>
public sealed record PublicEntityRefDto(string Kind, string Id, string? Title, string Relation, double? Probability = null);

/// <summary>Location usable by a map drill-down.  Geometry is present only when it was actually reported; a place reference is never promoted to an exact point.</summary>
public sealed record PublicMapLocatorDto(string? LocationKind, int? PlaceId, string? PlaceName, int? RegionId, string? Precision,
    Geometry? Geometry, DateTimeOffset? At, string? UnavailableReason);

/// <summary>One compact catalogue row.  The response is an allow-list: it contains neither raw message text/payload nor parser metadata.</summary>
public sealed record PublicEntitySummaryDto(
    string Kind, string Id, string Title, string? CatalogKind, string? CatalogKindName, string? Classification,
    DateTimeOffset At, string? State, string? Confidence, string? LocationKind, int? PlaceId, string? PlaceName, int? RegionId,
    IReadOnlyList<int> SourceIds, int TotalEvidenceCount, int MatchedEvidenceCount, bool MapAvailable, PublicMapLocatorDto Map);

/// <summary>Opaque keyset page.  Lists are best-effort against concurrent live updates; a dataset/cursor mismatch is a reload signal, not snapshot isolation.</summary>
public sealed record PublicEntityPageDto(DateTimeOffset From, DateTimeOffset To, string Dataset, string Consistency,
    IReadOnlyList<PublicEntitySummaryDto> Items, string? NextCursor, bool RefreshRecommended, IReadOnlyDictionary<string, bool> Capabilities);

/// <summary>An evidence fact without the raw message body.  <c>ObservationId</c> remains nullable for legacy facts.</summary>
public sealed record PublicEvidenceDto(string EntityKind, string EntityId, string? ObservationId, string? TargetId, int SourceId,
    DateTimeOffset At, string? CatalogKind, string? Classification, string? LocationKind, int? PlaceId, string Relation, double? Score);

/// <summary>A deduplicated raw-message reference.  Text and payload intentionally are not part of the public catalogue contract.</summary>
public sealed record PublicMessageRefDto(string Id, int SourceId, DateTimeOffset PublishedAt, string? Url);

public sealed record PublicCollectionPageDto<T>(IReadOnlyList<T> Items, string? NextCursor, int TotalCount);

/// <summary>Details keep the first bounded evidence/message/relation page for a useful initial render; callers continue with the corresponding paged resource.</summary>
public sealed record PublicEntityDetailsDto(PublicEntitySummaryDto Entity, PublicCollectionPageDto<PublicEvidenceDto> Evidence,
    PublicCollectionPageDto<PublicMessageRefDto> Messages, PublicCollectionPageDto<PublicEntityRefDto> Relations,
    IReadOnlyDictionary<string, string> Links, IReadOnlyDictionary<string, bool> Capabilities);

// ---- U05 public source-message catalogue ---------------------------------
// A raw-message id identifies one stored source revision. These contracts intentionally expose neither RawPayload nor
// any processing-stage JSON/worker detail; the public outcome is a small, documented projection of that state.

public sealed record PublicMessageSummaryDto(
    string Id, int SourceId, string? SourceCode, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt,
    string SourceMessageKey, string SourceRevision, string RevisionGroup,
    string Outcome, string OutcomeSource, string? Url, string? UrlText, int ResultCount, int MatchedResultCount,
    int LocatedResultCount, int UnlocatedResultCount, string? Excerpt, bool HasText);

public sealed record PublicMessagePageDto(DateTimeOffset From, DateTimeOffset To, string Dataset, string Consistency,
    IReadOnlyList<PublicMessageSummaryDto> Items, string? NextCursor, bool RefreshRecommended);

public sealed record PublicMessageResultDto(
    string TargetId, string? ObservationId, int SegmentIndex, string? SegmentText, string? CatalogKind, string? Classification,
    DateTimeOffset At, string Confidence, int SourceId, bool MapAvailable, PublicMapLocatorDto Map,
    IReadOnlyList<PublicEntityRefDto> Relations);

public sealed record PublicMessageRevisionDto(string Id, DateTimeOffset PublishedAt, string SourceRevision, bool IsCurrent);

public sealed record PublicMessageDetailsDto(PublicMessageSummaryDto Message, string TextState, string? Text,
    PublicCollectionPageDto<PublicMessageResultDto> Results, PublicCollectionPageDto<PublicMessageRevisionDto> Revisions,
    IReadOnlyList<PublicEntityRefDto> DirectRelations, IReadOnlyDictionary<string, string> Links);

public sealed record PublicMessageTextChunkDto(string State, string? Text, int Offset, int TotalLength, string? NextCursor);
