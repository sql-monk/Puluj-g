import type { Geometry } from 'geojson'
import { isRetiredEntity } from '../entities/presentation'

export interface EntityMapSettings {
  visible: boolean
  renderer: 'point' | 'icon' | 'line' | 'polygon'
  labelField?: string
  timeField?: string
  statusField?: string
  geometryField?: string
  latitudeField?: string
  longitudeField?: string
  lifetimeMinutes?: number
  /** Names what a row is the state of (an alert's area): the server sends only the latest row per key. */
  keyField?: string
  svgIcon?: string
  color?: string
  width?: number
  opacity?: number
  dash?: string
}

export interface EntityDefinition {
  entityName: string
  tableName: string
  fields: { name: string; type: string }[]
  map: EntityMapSettings
  enabled: boolean
}

export interface EntityItem {
  entity: string
  table: string
  id: string
  rawMessageId?: string
  sourceId?: number
  occurredAt?: string
  values: Record<string, unknown>
  geometry?: Geometry
  message?: {
    rawMessageId: string
    sourceId: number
    sourceCode: string
    sourceName: string
    publishedAt: string
    receivedAt: string
    text?: string
    url?: string
    sourceUrl?: string
  }
}

export interface EntitySnapshot { generatedAt: string; at?: string; items: EntityItem[]; truncated: boolean; limitPerEntity: number }
export interface EntityPage { items: EntityItem[]; nextCursor?: string; totalCount: number; excludesRetiredTracks?: boolean; searchCandidateLimit?: number; totalCountExact?: boolean }

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' }, signal })
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`)
  return response.json() as Promise<T>
}

export const entityApi = {
  definitions: async (signal?: AbortSignal) => (await get<EntityDefinition[]>('/api/ee/definitions', signal)).filter(definition => !isRetiredEntity(definition.entityName, definition.tableName)),
  snapshot: async (at?: Date, signal?: AbortSignal) => {
    const snapshot = await get<EntitySnapshot>(`/api/ee/snapshot${at ? `?at=${encodeURIComponent(at.toISOString())}` : ''}`, signal)
    return { ...snapshot, items: snapshot.items.filter(item => !isRetiredEntity(item.entity, item.table)) }
  },
  catalogue: async (params: { kind?: string; kinds?: string[]; q?: string; sourceIds?: number[]; from?: Date; to?: Date; limit?: number; cursor?: string } = {}, signal?: AbortSignal) => {
    if (params.kind && isRetiredEntity(params.kind) || params.kinds?.length && params.kinds.every(kind => isRetiredEntity(kind))) return { items: [], totalCount: 0, totalCountExact: true }
    const query = new URLSearchParams()
    if (params.kind) query.set('kind', params.kind)
    if (params.kinds?.length) query.set('kinds', [...new Set(params.kinds)].filter(kind => !isRetiredEntity(kind)).join(','))
    if (params.q) query.set('q', params.q)
    if (params.sourceIds?.length) query.set('sourceIds', params.sourceIds.join(','))
    if (params.from) query.set('from', params.from.toISOString())
    if (params.to) query.set('to', params.to.toISOString())
    if (params.limit !== undefined) query.set('limit', String(params.limit))
    if (params.cursor) query.set('cursor', params.cursor)
    const page = await get<EntityPage>(`/api/ee/entities?${query}`, signal)
    const items = page.items.filter(item => !isRetiredEntity(item.entity, item.table))
    // An old backend's total includes retired rows outside this page; never invent a corrected total.
    return { ...page, items, totalCountExact: page.excludesRetiredTracks === true && items.length === page.items.length }
  },
  catalogueMany: async (params: { kinds?: string[]; q?: string; sourceIds?: number[]; from?: Date; to?: Date; limit?: number; cursor?: string } = {}, signal?: AbortSignal) => {
    const kinds = [...new Set(params.kinds?.filter(Boolean) ?? [])]
    return entityApi.catalogue({ kinds, q: params.q, sourceIds: params.sourceIds, from: params.from, to: params.to, limit: params.limit, cursor: params.cursor }, signal)
  },
  detail: async (kind: string, id: string, signal?: AbortSignal) => {
    if (isRetiredEntity(kind)) throw new Error('Треки вимкнено. Історичні записи збережено, але їх більше не показано.')
    const item = await get<EntityItem>(`/api/ee/entities/${encodeURIComponent(kind)}/${encodeURIComponent(id)}`, signal)
    if (isRetiredEntity(item.entity, item.table)) throw new Error('Треки вимкнено.')
    return item
  },
  history: async (kind: string, id: string, signal?: AbortSignal) => isRetiredEntity(kind) ? [] : (await get<EntityItem[]>(`/api/ee/entities/${encodeURIComponent(kind)}/${encodeURIComponent(id)}/history`, signal)).filter(item => !isRetiredEntity(item.entity, item.table)),
}

export function mergeEntityPages(kinds: string[], pages: EntityPage[]): EntityPage {
  const items = pages.flatMap((page) => page.items).sort((left, right) => (right.occurredAt ?? '').localeCompare(left.occurredAt ?? '') || right.id.localeCompare(left.id))
  const nextByKind = Object.fromEntries(kinds.map((kind, index) => [kind, pages[index]?.nextCursor ?? null]))
  return { items, totalCount: pages.reduce((sum, page) => sum + page.totalCount, 0), nextCursor: Object.values(nextByKind).some(Boolean) ? JSON.stringify(nextByKind) : undefined }
}
