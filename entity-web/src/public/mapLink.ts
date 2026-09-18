import type { Geometry } from 'geojson'
import type { PublicEntityKind, PublicMapLocatorDto } from '../api/types'
import { publicHash, type MapMode, type PublicRoute } from './routes'

export type MapSelectionKind = PublicEntityKind

export interface MapSelection {
  kind: MapSelectionKind
  /** Opaque decimal identity.  It is intentionally never coerced to number. */
  id: string
}

export interface MapFocus {
  selection: MapSelection
  locators: PublicMapLocatorDto[]
  label: string
  unavailable?: string
}

const selectionKinds = new Set<MapSelectionKind>(['track', 'alert', 'observation'])

export function parseMapSelection(query: URLSearchParams): MapSelection | null {
  const value = query.get('select')
  if (!value) return null
  const index = value.indexOf(':')
  if (index <= 0 || index === value.length - 1) return null
  const kind = value.slice(0, index) as MapSelectionKind
  const id = value.slice(index + 1)
  // API long values and raw revision identities are decimal strings.  Do not accept a path/query injection here.
  return selectionKinds.has(kind) && /^\d+$/.test(id) ? { kind, id } : null
}

/** Only a canonical local hash may be used as a return target. */
export function parseMapReturn(query: URLSearchParams): string | null {
  const value = query.get('return')
  if (!value || !value.startsWith('#/')) return null
  const path = value.slice(1).split('?', 1)[0]
  return /^(\/map\/(?:live|history)|\/entities(?:\/(?:track|alert|observation)\/\d+)?|\/analytics)$/.test(path) ? value : null
}

function returnHash(route: PublicRoute): string {
  const query = new URLSearchParams(route.query)
  query.delete('select')
  query.delete('return')
  return publicHash({ ...route, query })
}

function historyQuery(query: URLSearchParams, at: string): URLSearchParams {
  const next = new URLSearchParams(query)
  const instant = new Date(at)
  if (Number.isNaN(instant.getTime())) return next
  // A 24-hour reconstructed window is within the SnapshotService replay bound.  `to` remains exclusive.
  next.set('from', new Date(instant.getTime() - 12 * 60 * 60_000).toISOString())
  next.set('to', new Date(instant.getTime() + 12 * 60 * 60_000).toISOString())
  next.set('at', instant.toISOString())
  return next
}

export function mapHref(source: PublicRoute, selection: MapSelection, locator: Pick<PublicMapLocatorDto, 'at'>, active = false): string {
  const query = active ? new URLSearchParams() : historyQuery(source.query, locator.at ?? '')
  query.set('select', `${selection.kind}:${selection.id}`)
  query.set('return', returnHash(source))
  const mapMode: MapMode = active ? 'live' : 'history'
  return publicHash({ section: 'map', mapMode, query })
}

export function geometryForFocus(locators: PublicMapLocatorDto[]): Geometry | undefined {
  const geometries = locators.map((x) => x.geometry).filter((x): x is Geometry => x !== undefined)
  if (geometries.length === 0) return undefined
  return geometries.length === 1 ? geometries[0] : { type: 'GeometryCollection', geometries }
}
