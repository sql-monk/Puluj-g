import type { Geometry } from 'geojson'
import type { AlertDto, EventKindDto, MapConfigDto, TargetDto, PlaceDto, PredecessorsDto, PublicCollectionPageDto, PublicEntityDetailsDto, PublicEntityKind, PublicEntityPageDto, PublicEntityRefDto, PublicEvidenceDto, PublicMessageRefDto, RegionDto, ReplayDto, SnapshotDto, SourceDto, StatsAlertsDto, StatsRecognitionDto, StatsSourcesDto, StatsTargetsDto, TaxonomyDto, TimelineBucketDto, TrackDetailsDto } from './types'

async function get<T>(path: string): Promise<T> {
  const res = await fetch(path, { headers: { Accept: 'application/json' } })
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

export const api = {
  /** The live windows (lifetime choices, feed depth) the server applies; the client prunes with the same numbers. */
  mapConfig: () => get<MapConfigDto>('/api/map/config'),
  snapshot: (at?: Date, activeOnly = true) =>
    get<SnapshotDto>(`/api/snapshot?activeOnly=${activeOnly}${at ? `&at=${encodeURIComponent(at.toISOString())}` : ''}`),
  track: (id: number) => get<TrackDetailsDto>(`/api/tracks/${id}`),
  /** One report with its message, source and kinematic links. */
  target: (id: number) => get<TargetDto>(`/api/targets/${id}`),
  /** Every probable predecessor of the track's newest report, `depth` generations back, with probabilities. */
  predecessors: (trackId: number, depth = 2) => get<PredecessorsDto>(`/api/tracks/${trackId}/predecessors?depth=${depth}`),
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
  /** Alerts of one place over the last `hours`, ended ones included, newest first. */
  alertsHistory: (placeId: number, hours = 24) => get<AlertDto[]>(`/api/alerts/history?placeId=${placeId}&hours=${hours}`),
  searchPlaces: (q: string) => get<PlaceDto[]>(`/api/places/search?q=${encodeURIComponent(q)}&limit=8`),
  /** Every track of a replay window with all its reported positions (one payload for the whole timelapse). */
  replay: (from: Date, to: Date) => get<ReplayDto>(`/api/replay?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
  /** The statistics page, one payload per tab for one period (server-cached, the same for everyone). */
  stats: {
    targets: (from: Date, to: Date) => get<StatsTargetsDto>(`/api/stats/targets?${periodQuery(from, to)}`),
    alerts: (from: Date, to: Date) => get<StatsAlertsDto>(`/api/stats/alerts?${periodQuery(from, to)}`),
    sources: (from: Date, to: Date) => get<StatsSourcesDto>(`/api/stats/sources?${periodQuery(from, to)}`),
    recognition: (from: Date, to: Date) => get<StatsRecognitionDto>(`/api/stats/recognition?${periodQuery(from, to)}`),
  },
  timeline: (from: Date, to: Date, bucketMinutes: number) =>
    get<TimelineBucketDto[]>(
      `/api/timeline?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&bucketMinutes=${bucketMinutes}`,
    ),
}
