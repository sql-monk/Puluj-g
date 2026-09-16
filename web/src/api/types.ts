// Mirrors src/Puluj.Contracts/Dtos.cs (camelCase, nulls omitted by the API).
import type { Geometry, LineString, Point } from 'geojson'

export type Confidence = 'Unknown' | 'Low' | 'Medium' | 'High' | 'Confirmed'
export type LocationKind = 'Unknown' | 'DirectionOnly' | 'Region' | 'District' | 'City' | 'Area' | 'Point'
export type DirectionKind = 'Unknown' | 'Compass' | 'TowardsPlace'
export type TrackStatus = 'Active' | 'Closed' | 'Cancelled'
export type DisplayMode = 'uav' | 'cruise' | 'ballistic' | 'aircraft'
/** Map API/SignalR bigint fields are opaque decimal strings; numeric values exist only at legacy/test boundaries. */
export type MapId = string | number

export interface SpeedProfile {
  minKmh?: number
  maxKmh?: number
  etaEnabled: boolean
}

export interface TargetTypeDto {
  categoryCode: string
  categoryName: string
  classCode?: string
  className?: string
  familyCode?: string
  familyName?: string
  modelCode?: string
  modelName?: string
  displayMode: DisplayMode
  fadeMinutes: number
  speedProfile: SpeedProfile
  label: string
}

export interface LocationDto {
  kind: LocationKind
  placeId?: number
  placeName?: string
  regionId?: number
  regionName?: string
  point?: Point
  accuracyKm?: number
}

export interface DirectionDto {
  degrees: number
  kind: DirectionKind
  confidence: Confidence
}

/** One earlier reported position of a track: the crumbs drawn behind the marker. */
export interface FixDto {
  at: string
  placeName?: string
  kind: LocationKind
  point: Point
  accuracyKm?: number
  /** The report only named a destination: the object was on its way to this place. */
  approach: boolean
  /** That this earlier report is the same object as the next one in the chain (1 for the current position). */
  probability: number
}

export interface TrackDto {
  id: MapId
  status: TrackStatus
  closedReason?: string
  type: TargetTypeDto
  modelConfidence: Confidence
  trackConfidence: Confidence
  firstSeenAt: string
  lastSeenAt: string
  updatedAt: string
  lastLocation?: LocationDto
  trackGeometry?: LineString
  direction?: DirectionDto
  objectCount?: number
  targetCount: number
  distinctSourceCount: number
  /** Ids of the sources whose targets make up the track (feeds the per-source filter and the badge). */
  sourceIds: number[]
  /** The last few distinct reported positions, oldest first, the current one last. */
  fixes: FixDto[]
  /** Raw messages behind the newest targets: tracks sharing one are neighbours by message. */
  messageIds: MapId[]
}

export type AlertLevel = 'Unknown' | 'Yellow' | 'Red'

export interface AlertDto {
  id: MapId
  placeId: number
  placeName: string
  alertType: string
  /** Yellow / Red where the administration publishes levels; Unknown for plain on/off alerts (drawn as red). */
  level: AlertLevel
  startedAt: string
  endedAt?: string
  /** Point + radius for places without a polygon (raion towns). */
  location?: LocationDto
  /** The place's parents, nearest first, up to the root: [raion, oblast] for a hromada, [oblast] for a raion, [] for an
   * oblast or Kyiv. An alert covers a place when its placeId is the place or one of the place's ancestors; it lies inside
   * the place when the place is among these. */
  ancestorIds: number[]
}

/** The live map's time windows (GET /api/map/config): the lifetime choices and the feed depth the server works with. */
export interface MapConfigDto {
  lifetimeOptionsMinutes: number[]
  maxLifetimeMinutes: number
  feedHours: number
}

/** The live map's time windows (GET /api/map/config): the lifetime choices and the feed depth the server works with. */
export interface MapConfigDto {
  lifetimeOptionsMinutes: number[]
  maxLifetimeMinutes: number
  feedHours: number
}

export interface SnapshotDto {
  at: string
  historical: boolean
  tracks: TrackDto[]
  alerts: AlertDto[]
  /** Localized non-track facts: explosions, air-defence activity, and threat cancellations. */
  events: TargetDto[]
}

/** One reported position of a track inside a replay window. */
export interface ReplaySampleDto {
  at: string
  point: Point
  directionDeg?: number
  approach: boolean
}
export interface ReplayTrackDto {
  id: MapId
  type: TargetTypeDto
  /** Oldest first. */
  samples: ReplaySampleDto[]
}
/** A whole replay window in one payload: the client animates each track between its reports. */
export interface ReplayDto {
  from: string
  to: string
  tracks: ReplayTrackDto[]
}

export interface SourceDto {
  id: number
  code: string
  name: string
  type: string
  trustLevel: number
  url?: string
}

/** Read-only dictionary tree returned by /api/taxonomy. IDs stay numeric taxonomy IDs, never event kinds. */
export interface TaxonomyModelDto {
  id: number
  code: string
  name: string
  manufacturer?: string
  country?: string
}
export interface TaxonomyFamilyDto {
  id: number
  code: string
  name: string
  models: TaxonomyModelDto[]
}
export interface TaxonomyClassDto {
  id: number
  code: string
  name: string
  families: TaxonomyFamilyDto[]
}
export interface TaxonomyCategoryDto {
  id: number
  code: string
  name: string
  classes: TaxonomyClassDto[]
}
export interface TaxonomyDto {
  categories: TaxonomyCategoryDto[]
}

/** The enabled API catalogue. A code absent here can still be a historical URL value. */
export interface EventKindDto {
  id: number
  code: string
  nameUk: string
  category: string
  mapVisible: boolean
}

export interface RawMessageDto {
  id: MapId
  sourceMessageId: string
  publishedAt: string
  receivedAt: string
  text?: string
  url?: string
}

/**
 * A node of the selected target's family. generation = generations above the head (0 = the head's own level: siblings,
 * cousins). ancestral = on the head's own ancestry (parent, grandparent); otherwise a relative: where an ancestor
 * could have flown instead. The node is drawn as the target it is: its class glyph, turned by its course.
 */
export interface PredecessorDto {
  targetId: MapId
  generation: number
  ancestral: boolean
  at: string
  placeName?: string
  kind: LocationKind
  point?: Point
  accuracyKm?: number
  approach: boolean
  label?: string
  displayMode: DisplayMode
  typeLabel: string
  directionDeg?: number
}
/** generation = the generation of `from` above the head; ancestral = a link on the head's own ancestry. */
export interface PredecessorLinkDto {
  fromTargetId: MapId
  toTargetId: MapId
  generation: number
  ancestral: boolean
  kind: 'Continuation' | 'Split' | 'Merge' | 'Possible' | 'Duplicate'
  /** The link's own probability. */
  probability: number
  /** Product of the probabilities from the newest report down to this link. */
  pathProbability: number
}
export interface PredecessorsDto {
  trackId: MapId
  headTargetId: MapId
  targets: PredecessorDto[]
  links: PredecessorLinkDto[]
}

export interface TargetLinkDto {
  targetId: MapId
  kind: 'Continuation' | 'Split' | 'Merge' | 'Possible' | 'Duplicate'
  /** 0..1: that the two reports are the same object (links into one target sum to at most 1). */
  probability: number
  direction: 'from' | 'to'
  distanceKm?: number
  minutesApart?: number
  headingDiffDeg?: number
  requiredMinutes?: number
}

/** Source rating report: per-day counters and the earned rating, who copies whom, and the groups that follow. */
export interface SourceRatingDayDto {
  day: string
  targets: number
  copies: number
  copiedBy: number
  avgLeadSeconds?: number
  rating?: number
}
export interface SourceRatingDto {
  id: number
  name: string
  trustLevel: number
  rating?: number
  group?: number
  days: SourceRatingDayDto[]
}
export interface SourceCopyDto {
  copierId: number
  originalId: number
  count: number
  avgDelaySeconds: number
}
export interface SourceRatingReportDto {
  days: string[]
  sources: SourceRatingDto[]
  copies: SourceCopyDto[]
  groups: number[][]
}

export interface TargetDto {
  id: MapId
  observedAt: string
  eventType: string
  type?: TargetTypeDto
  modelConfidence: Confidence
  classificationConfidence: Confidence
  confidence: Confidence
  location?: LocationDto
  origin?: LocationDto
  destination?: LocationDto
  direction?: DirectionDto
  objectCount?: number
  objectCountIsApproximate: boolean
  identificationMethod: string
  identificationSource?: string
  segmentText?: string
  duplicateOfTargetId?: MapId
  associationConfidence?: number
  source: SourceDto
  rawMessage: RawMessageDto
  /** Track the target was attached to (feed highlighting), if any. */
  trackId?: MapId
  /** Links to related targets (only filled in track details). */
  links?: TargetLinkDto[]
}

export interface TrackDetailsDto {
  track: TrackDto
  targets: TargetDto[]
}

export interface PlaceDto {
  id: number
  name: string
  level: string
  parentId?: number
  parentName?: string
  lon: number
  lat: number
  radiusKm: number
  population: number
}

export interface RegionDto {
  id: number
  name: string
  level: string
  countryCode: string
  /** Set for city districts (Kyiv): the city-region they belong to. */
  parentId?: number
  geometry: Geometry
}

export interface TimelineBucketDto {
  from: string
  targets: number
  tracksOpened: number
  alerts: number
}

// U04 public catalogue. Bigint database identities are intentionally strings here: JavaScript numbers cannot safely
// represent every server ID, and the catalogue must round-trip direct links without precision loss.
export type PublicEntityKind = 'track' | 'incident' | 'alert' | 'observation'
export interface PublicMapLocatorDto {
  locationKind?: string
  placeId?: number
  placeName?: string
  regionId?: number
  precision?: string
  geometry?: Geometry
  at?: string
  unavailableReason?: string
}
export interface PublicEntitySummaryDto {
  kind: PublicEntityKind
  id: string
  title: string
  catalogKind?: string
  catalogKindName?: string
  classification?: string
  at: string
  state?: string
  confidence?: string
  locationKind?: string
  placeId?: number
  placeName?: string
  regionId?: number
  sourceIds: number[]
  totalEvidenceCount: number
  matchedEvidenceCount: number
  mapAvailable: boolean
  map: PublicMapLocatorDto
}
export interface PublicEntityPageDto {
  from: string
  to: string
  dataset: string
  consistency: 'best_effort_live'
  items: PublicEntitySummaryDto[]
  nextCursor?: string
  refreshRecommended: boolean
  capabilities: Record<string, boolean>
}
export interface PublicEvidenceDto {
  entityKind: PublicEntityKind
  entityId: string
  observationId?: string
  targetId?: string
  sourceId: number
  at: string
  catalogKind?: string
  classification?: string
  locationKind?: string
  placeId?: number
  relation: string
  score?: number
}
export interface PublicMessageRefDto { id: string; sourceId: number; publishedAt: string; url?: string }
export interface PublicEntityRefDto { kind: PublicEntityKind; id: string; title?: string; relation: string; probability?: number }
export interface PublicCollectionPageDto<T> { items: T[]; nextCursor?: string; totalCount: number }
export interface PublicEntityDetailsDto {
  entity: PublicEntitySummaryDto
  evidence: PublicCollectionPageDto<PublicEvidenceDto>
  messages: PublicCollectionPageDto<PublicMessageRefDto>
  relations: PublicCollectionPageDto<PublicEntityRefDto>
  links: Record<'evidence' | 'messages' | 'relations', string>
  capabilities: Record<string, boolean>
}

export interface PublicMessageSummaryDto {
  id: string
  sourceId: number
  sourceCode?: string
  publishedAt: string
  receivedAt: string
  sourceMessageKey: string
  sourceRevision: string
  revisionGroup: string
  excerpt?: string
  outcome: string
  outcomeSource: 'stage_result' | 'legacy'
  url?: string
  resultCount: number
  matchedResultCount: number
  locatedResultCount: number
  unlocatedResultCount: number
  hasText: boolean
}
export interface PublicMessagePageDto { from: string; to: string; dataset: string; consistency: string; items: PublicMessageSummaryDto[]; nextCursor?: string; refreshRecommended: boolean }
export interface PublicMessageResultDto { targetId: string; observationId?: string; segmentIndex: number; segmentText?: string; catalogKind?: string; classification?: string; at: string; confidence: string; sourceId: number; mapAvailable: boolean; map: PublicMapLocatorDto; relations: PublicEntityRefDto[] }
export interface PublicMessageRevisionDto { id: string; publishedAt: string; sourceRevision: string; isCurrent: boolean }
export interface PublicMessageDetailsDto { message: PublicMessageSummaryDto; textState: string; text?: string; results: PublicCollectionPageDto<PublicMessageResultDto>; revisions: PublicCollectionPageDto<PublicMessageRevisionDto>; directRelations: PublicEntityRefDto[]; links: Record<string, string> }
export interface PublicMessageTextChunkDto { state: string; text?: string; offset: number; totalLength: number; nextCursor?: string }

// Statistics page (GET /api/stats/{targets|alerts|sources|recognition}?from&to): one payload per tab for one period.
// Per-bucket arrays are aligned with `period.bucketStarts`.
export type StatsBucketUnit = 'hour' | 'day' | 'week'

export interface StatsPeriodDto {
  from: string
  to: string
  bucket: StatsBucketUnit
  bucketStarts: string[]
}

export interface StatsCategoryDto {
  code: string
  name: string
}

export interface StatsClassDto {
  code: string
  name: string
  categoryCode: string
  targets: number
  tracks: number
  objectsDeclared: number
}

export interface StatsRegionDto {
  /** Absent for the folded "other" row. */
  id?: number
  name: string
  targets: number
}

export interface StatsRouteDto {
  fromId: number
  fromName: string
  toId: number
  toName: string
  count: number
}

export interface StatsSliceDto {
  key: string
  label: string
  count: number
}

/** "What flew": facts and tracks per bucket and category, classes, regions, routes, hour × weekday. */
export interface StatsTargetsDto {
  period: StatsPeriodDto
  targets: number
  tracks: number
  objectsDeclared: number
  categories: StatsCategoryDto[]
  /** One row per bucket; inner arrays are counts per category in the order of `categories`. */
  targetsByBucket: number[][]
  tracksByBucket: number[][]
  byClass: StatsClassDto[]
  byRegion: StatsRegionDto[]
  /** Facts whose location does not resolve to a region. */
  unlocated: number
  routes: StatsRouteDto[]
  /** 7 rows (Monday first) × 24 hours, Europe/Kyiv. */
  hourWeekday: number[][]
}

export interface StatsAlertRegionDto {
  id: number
  name: string
  count: number
  hours: number
}

export interface StatsAlertDayDto {
  /** Kyiv calendar day, `YYYY-MM-DD`. */
  day: string
  count: number
  hours: number
}

/** Region-level air-raid alerts of the period. */
export interface StatsAlertsDto {
  period: StatsPeriodDto
  alerts: number
  alertHours: number
  openAtEnd: number
  declaredByBucket: number[]
  hoursByBucket: number[]
  byRegion: StatsAlertRegionDto[]
  durations: StatsSliceDto[]
  /** 24 entries, Europe/Kyiv. */
  declaredByHour: number[]
  topDays: StatsAlertDayDto[]
}

export interface StatsSourceDto {
  id: number
  code: string
  name: string
  messages: number
  processed: number
  withTargets: number
  targets: number
  medianLagSeconds?: number
  /** Messages per bucket. */
  series: number[]
}

export interface StatsSourcesDto {
  period: StatsPeriodDto
  messages: number
  processed: number
  withTargets: number
  targets: number
  sources: StatsSourceDto[]
}

/** How the pipeline read the period: distributions of the facts, processed messages with / without a fact per bucket. */
export interface StatsRecognitionDto {
  period: StatsPeriodDto
  targets: number
  processed: number
  withTargets: number
  eventTypes: StatsSliceDto[]
  methods: StatsSliceDto[]
  confidence: StatsSliceDto[]
  locationKinds: StatsSliceDto[]
  processedByBucket: number[]
  withTargetsByBucket: number[]
}
