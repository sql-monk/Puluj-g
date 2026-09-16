import type { Geometry } from 'geojson'
import type { DataQuery } from '../public/query'
import type { AlertDto, EventKindDto, MapConfigDto, MapId, TargetDto, PlaceDto, PredecessorsDto, PublicCollectionPageDto, PublicEntityDetailsDto, PublicEntityKind, PublicEntityPageDto, PublicEntityRefDto, PublicEvidenceDto, PublicMessageDetailsDto, PublicMessagePageDto, PublicMessageResultDto, PublicMessageRevisionDto, PublicMessageTextChunkDto, PublicMessageRefDto, RegionDto, ReplayDto, SnapshotDto, SourceDto, StatsAlertsDto, StatsRecognitionDto, StatsSourcesDto, StatsTargetsDto, TaxonomyDto, TimelineBucketDto, TrackDetailsDto } from './types'

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { headers: { Accept: 'application/json' }, signal })
  if (!res.ok) {
    throw new Error(`${path}: HTTP ${res.status}`)
  }
  return (await res.json()) as T
}

function publicQuery(params: Record<string, string | number | boolean | undefined>): string {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) if (value !== undefined) query.set(key, String(value))
  return query.toString()
}

function periodQuery(from: Date, to: Date): string {
  return `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`
}

/** The map reads U03's canonical filter state on the server; never filter a capped map response in the browser. */
function mapQuery(filter?: DataQuery): string {
  if (!filter) return ''
  return publicQuery({
    eventKinds: filter.eventKinds.join(',') || undefined,
    entityKinds: filter.entityKinds.join(',') || undefined,
    eventCategories: filter.eventCategories.join(',') || undefined,
    categoryIds: filter.categoryIds.join(',') || undefined,
    classIds: filter.classIds.join(',') || undefined,
    familyIds: filter.familyIds.join(',') || undefined,
    modelIds: filter.modelIds.join(',') || undefined,
    sourceIds: filter.sourceIds.join(',') || undefined,
    regionId: filter.regionId,
    q: filter.q,
    status: filter.status,
    confidence: filter.confidence,
    location: filter.location,
    hasResults: filter.hasResults,
  })
}

function withQuery(path: string, query: string): string {
  return query ? `${path}${path.includes('?') ? '&' : '?'}${query}` : path
}

export const api = {
  /** The live windows (lifetime choices, feed depth) the server applies; the client prunes with the same numbers. */
  mapConfig: () => get<MapConfigDto>('/api/map/config'),
  snapshot: (at?: Date, activeOnly = true, filter?: DataQuery, signal?: AbortSignal) =>
    get<SnapshotDto>(withQuery(`/api/snapshot?activeOnly=${activeOnly}${at ? `&at=${encodeURIComponent(at.toISOString())}` : ''}`, mapQuery(filter)), signal),
  track: (id: MapId) => get<TrackDetailsDto>(`/api/tracks/${id}`),
  /** One report with its message, source and kinematic links. */
  target: (id: MapId) => get<TargetDto>(`/api/targets/${id}`),
  /** Every probable predecessor of the track's newest report, `depth` generations back, with probabilities. */
  predecessors: (trackId: MapId, depth = 2) => get<PredecessorsDto>(`/api/tracks/${trackId}/predecessors?depth=${depth}`),
  /** Polygon of any place (hromada, raion) — for alerts below the levels the regions payload carries. */
  placeGeometry: (id: number) => get<Geometry>(`/api/places/${id}/geometry`),
  targets: (sinceHours = 6, limit = 300) =>
    get<TargetDto[]>(`/api/targets?since=${encodeURIComponent(new Date(Date.now() - sinceHours * 3600_000).toISOString())}&limit=${limit}`),
  /** Every target inside a replay window, newest first. */
  targetsBetween: (from: Date, to: Date, limit = 5000) =>
    get<TargetDto[]>(`/api/targets?since=${encodeURIComponent(from.toISOString())}&until=${encodeURIComponent(to.toISOString())}&limit=${limit}`),
  regions: () => get<RegionDto[]>('/api/places/regions'),
  sources: () => get<SourceDto[]>('/api/sources'),
  taxonomy: () => get<TaxonomyDto>('/api/taxonomy'),
  eventKinds: () => get<EventKindDto[]>('/api/event-kinds'),
  /** U04 catalogue: string IDs preserve bigint direct links; cursors are opaque and bound to the current filters/dataset. */
  publicEntities: (params: Record<string, string | number | boolean | undefined> = {}) => get<PublicEntityPageDto>(`/api/public/entities?${publicQuery(params)}`),
  publicEntity: (kind: PublicEntityKind, id: string, dataset = 'live') => get<PublicEntityDetailsDto>(`/api/public/entities/${kind}/${encodeURIComponent(id)}?${publicQuery({ dataset })}`),
  publicEvidence: (kind: PublicEntityKind, id: string, cursor?: string, dataset = 'live') => get<PublicCollectionPageDto<PublicEvidenceDto>>(`/api/public/entities/${kind}/${encodeURIComponent(id)}/evidence?${publicQuery({ cursor, dataset })}`),
  publicMessages: (kind: PublicEntityKind, id: string, cursor?: string, dataset = 'live') => get<PublicCollectionPageDto<PublicMessageRefDto>>(`/api/public/entities/${kind}/${encodeURIComponent(id)}/messages?${publicQuery({ cursor, dataset })}`),
  publicRelations: (kind: PublicEntityKind, id: string, cursor?: string, dataset = 'live') => get<PublicCollectionPageDto<PublicEntityRefDto>>(`/api/public/entities/${kind}/${encodeURIComponent(id)}/relations?${publicQuery({ cursor, dataset })}`),
  /** U05 source-message catalogue: one row per persisted source revision, with string bigint IDs. */
  publicMessagesIndex: (params: Record<string, string | number | boolean | undefined> = {}, signal?: AbortSignal) => get<PublicMessagePageDto>(`/api/public/messages?${publicQuery(params)}`, signal),
  publicMessage: (id: string, dataset = 'live', signal?: AbortSignal) => get<PublicMessageDetailsDto>(`/api/public/messages/${encodeURIComponent(id)}?${publicQuery({ dataset })}`, signal),
  publicMessageResults: (id: string, cursor?: string, dataset = 'live', signal?: AbortSignal) => get<PublicCollectionPageDto<PublicMessageResultDto>>(`/api/public/messages/${encodeURIComponent(id)}/results?${publicQuery({ cursor, dataset})}`, signal),
  publicMessageRevisions: (id: string, cursor?: string, dataset = 'live', signal?: AbortSignal) => get<PublicCollectionPageDto<PublicMessageRevisionDto>>(`/api/public/messages/${encodeURIComponent(id)}/revisions?${publicQuery({ cursor, dataset})}`, signal),
  publicMessageText: (id: string, cursor?: string, dataset = 'live', signal?: AbortSignal) => get<PublicMessageTextChunkDto>(`/api/public/messages/${encodeURIComponent(id)}/text?${publicQuery({ cursor, dataset})}`, signal),
  /** Alerts of one place over the last `hours`, ended ones included, newest first. */
  alertsHistory: (placeId: number, hours = 24) => get<AlertDto[]>(`/api/alerts/history?placeId=${placeId}&hours=${hours}`),
  searchPlaces: (q: string) => get<PlaceDto[]>(`/api/places/search?q=${encodeURIComponent(q)}&limit=8`),
  /** Every track of a replay window with all its reported positions (one payload for the whole timelapse). */
  replay: (from: Date, to: Date, filter?: DataQuery, signal?: AbortSignal) => get<ReplayDto>(withQuery(`/api/replay?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`, mapQuery(filter)), signal),
  /** The statistics page, one payload per tab for one period (server-cached, the same for everyone). */
  stats: {
    targets: (from: Date, to: Date) => get<StatsTargetsDto>(`/api/stats/targets?${periodQuery(from, to)}`),
    alerts: (from: Date, to: Date) => get<StatsAlertsDto>(`/api/stats/alerts?${periodQuery(from, to)}`),
    sources: (from: Date, to: Date) => get<StatsSourcesDto>(`/api/stats/sources?${periodQuery(from, to)}`),
    recognition: (from: Date, to: Date) => get<StatsRecognitionDto>(`/api/stats/recognition?${periodQuery(from, to)}`),
  },
  timeline: (from: Date, to: Date, bucketMinutes: number, filter?: DataQuery, signal?: AbortSignal) =>
    get<TimelineBucketDto[]>(
      withQuery(`/api/timeline?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&bucketMinutes=${bucketMinutes}`, mapQuery(filter)), signal,
    ),
}
