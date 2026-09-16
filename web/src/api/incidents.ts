import type { Point } from 'geojson'

/** P11 (ADR-0011): the incident read-side contracts, additive next to the legacy `events`. */

export type IncidentState = 'reported' | 'confirmed' | 'resolved' | 'retracted'
/** §8.5: how precisely an incident is placed — a point only for located evidence, a city marker, or an area (never a pin at a centroid). */
export type IncidentPrecision = 'point' | 'city' | 'district' | 'region' | 'unknown'

export interface IncidentLocationDto {
  kind: string
  placeId?: number
  placeName?: string
  regionId?: number
  regionName?: string
  point?: Point
  accuracyKm?: number
  precision: IncidentPrecision
}

export interface IncidentProvenanceDto {
  canonicalObservationId?: string
  observationCount: number
  sourceIds: number[]
  policyVersion?: string
  lastEventId?: string
  generationId: string
}

export interface IncidentDto {
  id: number
  kind: string
  kindName: string
  category: string
  state: IncidentState
  suppressed: boolean
  eventAt: string
  firstReportedAt: string
  lastReportedAt: string
  location?: IncidentLocationDto
  confidence: string
  sourceCount: number
  revision: number
  closureReason?: string
  mergedIntoIncidentId?: number
  provenance: IncidentProvenanceDto
}

export interface IncidentObservationDto {
  observationId: string
  targetId?: number
  sourceId: number
  sourceCode?: string
  relation: string
  score: number
  effectiveAt: string
  linkedAt: string
  segmentText?: string
  rawMessage?: { id: number; sourceMessageId: string; publishedAt: string; receivedAt: string; text?: string; url?: string }
}

export interface IncidentRevisionDto {
  revision: number
  change: string
  effectiveAt: string
  recordedAt: string
  actor: string
  reason?: string
}

export interface IncidentDetailsDto {
  incident: IncidentDto
  observations: IncidentObservationDto[]
  revisions: IncidentRevisionDto[]
}

export interface IncidentPageDto {
  from: string
  to: string
  mode: 'effective' | 'recorded'
  items: IncidentDto[]
  nextCursor?: string
  truncated: boolean
}

export interface IncidentQuery {
  from?: Date
  to?: Date
  /** `effective` (default) — today's reconstruction; `recorded` with `asOf` — what the system knew then. */
  mode?: 'effective' | 'recorded'
  asOf?: Date
  kind?: string
  state?: string
  cursor?: string
  limit?: number
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(path, { headers: { Accept: 'application/json' } })
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`)
  return (await res.json()) as T
}

export function incidentsQuery(q: IncidentQuery): string {
  const p = new URLSearchParams()
  if (q.from) p.set('from', q.from.toISOString())
  if (q.to) p.set('to', q.to.toISOString())
  if (q.mode) p.set('mode', q.mode)
  if (q.asOf) p.set('asOf', q.asOf.toISOString())
  if (q.kind) p.set('kind', q.kind)
  if (q.state) p.set('state', q.state)
  if (q.cursor) p.set('cursor', q.cursor)
  if (q.limit) p.set('limit', String(q.limit))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const incidentsApi = {
  page: (q: IncidentQuery) => get<IncidentPageDto>(`/api/incidents${incidentsQuery(q)}`),
  /** Every page of the window, up to `maxPages` (the server caps the span at 7 days and the page at 500). */
  async window(q: IncidentQuery, maxPages = 20): Promise<{ items: IncidentDto[]; to: string; complete: boolean }> {
    const items: IncidentDto[] = []
    let cursor: string | undefined
    let to = ''
    for (let i = 0; i < maxPages; i++) {
      const page = await this.page({ ...q, cursor, limit: q.limit ?? 500 })
      items.push(...page.items)
      to = page.to
      cursor = page.nextCursor
      if (!cursor) return { items, to, complete: true }
    }
    return { items, to, complete: false }
  },
  details: (id: number, revision?: number) => get<IncidentDetailsDto>(`/api/incidents/${id}${revision ? `?revision=${revision}` : ''}`),
}
