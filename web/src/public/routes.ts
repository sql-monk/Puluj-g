import { canonicalizeQuery } from './query'

export type PublicSection = 'map' | 'analytics' | 'entities'
export type MapMode = 'live' | 'history'

export interface PublicRoute {
  section: PublicSection
  mapMode?: MapMode
  /** A legacy Kyiv link is a map presentation preset, not a separate section. */
  preset?: 'kyiv'
  detail?: { kind?: string; id: string }
  query: URLSearchParams
}

export interface ParsedRoute {
  route: PublicRoute
  canonicalHash: string
  shouldReplace: boolean
}

export interface HistoryWindow {
  from: Date
  to: Date
  /** The exclusive upper bound is not itself a historical frame. */
  at: Date
}

function queryOf(hash: string): URLSearchParams {
  const index = hash.indexOf('?')
  return new URLSearchParams(index >= 0 ? hash.slice(index + 1) : '')
}

function pathOf(hash: string): string {
  const start = hash.startsWith('#') ? hash.slice(1) : hash
  return (start.split('?', 1)[0] || '/').replace(/\/+$/, '') || '/'
}

function isEntityKind(value: string): value is 'track' | 'alert' | 'observation' {
  return value === 'track' || value === 'alert' || value === 'observation'
}

function analyticsQuery(query: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(query)
  const legacyTab = next.get('tab')
  const legacyPreset = next.get('p')
  if (legacyTab !== null) {
    next.delete('tab')
    if (!next.has('metric')) next.set('metric', legacyTab)
  }
  if (legacyPreset !== null) {
    next.delete('p')
    if (!next.has('preset')) next.set('preset', legacyPreset)
  }
  return next
}

function decodedId(value: string): string | null {
  try {
    return decodeURIComponent(value)
  } catch {
    return null
  }
}

/** Serializes routes through U03's one canonical query codec. */
export function publicHash(route: PublicRoute): string {
  const base =
    route.section === 'map'
      ? `#/map/${route.mapMode ?? 'live'}`
      : route.section === 'entities' && route.detail
        ? `#/entities/${route.detail.kind ?? 'observation'}/${route.detail.id}`
        : `#/${route.section}`
  const query = new URLSearchParams(route.query)
  if (route.preset === 'kyiv') query.set('preset', 'kyiv')
  const encoded = canonicalizeQuery(query).toString()
  return encoded ? `${base}?${encoded}` : base
}

/**
 * Parses canonical routes and preserves legacy query state during a one-time replace normalization.
 * Query interpretation itself remains the single U03 codec responsibility.
 */
export function parsePublicHash(hash: string): ParsedRoute {
  const query = queryOf(hash)
  const path = pathOf(hash)
  let route: PublicRoute

  if (path === '/' || path === '') {
    route = { section: 'map', mapMode: 'live', query }
  } else if (path === '/kyiv') {
    route = { section: 'map', mapMode: 'live', preset: 'kyiv', query }
  } else if (path === '/stats') {
    route = { section: 'analytics', query: analyticsQuery(query) }
  } else if (path === '/map/live' || path === '/map/history') {
    route = { section: 'map', mapMode: path.endsWith('/history') ? 'history' : 'live', preset: query.get('preset') === 'kyiv' ? 'kyiv' : undefined, query }
  } else if (path === '/analytics') {
    route = { section: 'analytics', query: analyticsQuery(query) }
  } else if (path === '/entities') {
    route = { section: 'entities', query }
  } else {
    const entity = path.match(/^\/entities\/(track|alert|observation)\/([^/]+)$/)
    const entityId = entity ? decodedId(entity[2]) : null
    if (entity && entityId && isEntityKind(entity[1])) route = { section: 'entities', detail: { kind: entity[1], id: entityId }, query }
    else route = { section: 'map', mapMode: 'live', query: new URLSearchParams() }
  }

  const canonicalHash = publicHash(route)
  return { route, canonicalHash, shouldReplace: hash !== canonicalHash }
}

export function isMapRoute(route: PublicRoute): boolean {
  return route.section === 'map'
}

/**
 * A history hash describes a half-open UTC interval. `at` is optional but, once
 * present, is the frozen frame and survives unrelated filter changes.
 */
export function historyWindow(query: URLSearchParams, now = new Date()): HistoryWindow {
  const suppliedFrom = query.get('from')
  const suppliedTo = query.get('to')
  const suppliedAt = query.get('at')
  const from = suppliedFrom ? new Date(suppliedFrom) : null
  const to = suppliedTo ? new Date(suppliedTo) : null
  const valid = from && to && !Number.isNaN(from.getTime()) && !Number.isNaN(to.getTime()) && from < to && from <= now
  const end = valid ? new Date(Math.min(to.getTime(), now.getTime())) : new Date(now)
  const start = valid && from < end ? from : new Date(end.getTime() - 24 * 3600_000)
  const candidateAt = suppliedAt ? new Date(suppliedAt) : null
  const at = candidateAt && !Number.isNaN(candidateAt.getTime()) && candidateAt >= start && candidateAt < end ? candidateAt : new Date(end.getTime() - 1)
  return { from: start, to: end, at }
}
