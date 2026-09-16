/**
 * Catalog adapter (plan §8.6, P11): layers, legend, filters, icons and colours of incident kinds come from
 * `GET /api/event-kinds`, never from an enum in the client. Meaning is never carried by colour alone: every kind has a
 * shape (drawn glyph) and a text label. Catalog visibility (`mapVisible`) and the viewer's own filter are two settings.
 */

/** The full read-side catalog entry (a superset of the store's `EventKindDto`, which carries only a few fields). */
export interface CatalogKindDto {
  id: number
  code: string
  nameUk: string
  category: string
  defaultSeverity?: string
  stateModel?: string
  requiresLocationForMap: boolean
  renderMode?: string
  mapColor?: string
  mapIcon?: string
  /** .NET TimeSpan as serialised by the API: "hh:mm:ss" or "d.hh:mm:ss". */
  mapLifetime?: string
  createsIncident: boolean
  mapVisible: boolean
  sortOrder: number
  legacyEventType?: string
  policyVersion: number
}

export type IncidentShape = 'burst' | 'flame' | 'bolt' | 'square' | 'shield' | 'chevron' | 'circle'

export interface CatalogKind {
  code: string
  name: string
  category: string
  color: string
  icon: string
  shape: IncidentShape
  renderMode: 'marker' | 'area' | 'feed'
  lifetimeMinutes: number
  createsIncident: boolean
  mapVisible: boolean
  sortOrder: number
  legacyEventType?: string
}

export interface LegendEntry {
  code: string
  label: string
  color: string
  shape: IncidentShape
}

export interface Catalog {
  kinds: Map<string, CatalogKind>
  /** Map-visible incident kinds in catalog order: the legend and the per-kind filter list. */
  legend: LegendEntry[]
  kindOf: (code: string) => CatalogKind
  colorOf: (code: string) => string
  shapeOf: (code: string) => IncidentShape
  /** How long an incident stays on the map after its last report (catalog `mapLifetime`; a default when the kind has none). */
  lifetimeMinutesOf: (code: string) => number
  /** Legacy `EventType` names whose facts now live as incidents: the old event markers of these are hidden when the incident layer is on. */
  legacyEventTypesOfIncidents: Set<string>
  /** Colour of a legacy `EventType` marker from the kind it maps to (the legacy event layer no longer carries its own colour table). */
  colorOfLegacy: (eventType: string) => string | undefined
}

export const DEFAULT_INCIDENT_LIFETIME_MINUTES = 6 * 60

const CATEGORY_COLOR: Record<string, string> = { incident: '#dc2626', target: '#2563eb', alert: '#b45309', info: '#64748b' }
/** Catalog `mapIcon` → glyph shape (the server's icon vocabulary; anything unknown draws a circle). */
export const ICON_SHAPE: Record<string, IncidentShape> = {
  'air-defence': 'shield',
  'alert-off': 'circle',
  explosion: 'burst',
  impact: 'burst',
  fire: 'flame',
  outage: 'bolt',
  damage: 'square',
  interception: 'shield',
  alert: 'shield',
  launch: 'chevron',
  cancel: 'circle',
  target: 'circle',
  notice: 'circle',
  evacuation: 'square',
  unknown: 'circle',
}

/** "02:00:00" | "1.12:00:00" → minutes; anything unreadable → the default. */
export function timeSpanMinutes(value: string | undefined, fallback = DEFAULT_INCIDENT_LIFETIME_MINUTES): number {
  if (!value) return fallback
  const m = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})/.exec(value)
  if (!m) return fallback
  const days = Number(m[1] ?? 0)
  const minutes = days * 1440 + Number(m[2]) * 60 + Number(m[3]) + Number(m[4]) / 60
  return minutes > 0 ? minutes : fallback
}

function unknownKind(code: string): CatalogKind {
  return { code, name: code, category: 'incident', color: CATEGORY_COLOR.incident, icon: 'unknown', shape: 'circle', renderMode: 'marker', lifetimeMinutes: DEFAULT_INCIDENT_LIFETIME_MINUTES, createsIncident: false, mapVisible: true, sortOrder: 999 }
}

export function buildCatalog(dtos: CatalogKindDto[]): Catalog {
  const kinds = new Map<string, CatalogKind>()
  for (const k of dtos) {
    const icon = k.mapIcon ?? 'unknown'
    const renderMode = k.renderMode === 'area' || k.renderMode === 'feed' ? k.renderMode : 'marker'
    kinds.set(k.code, {
      code: k.code,
      name: k.nameUk,
      category: k.category,
      color: k.mapColor ?? CATEGORY_COLOR[k.category] ?? CATEGORY_COLOR.info,
      icon,
      shape: ICON_SHAPE[icon] ?? 'circle',
      renderMode,
      lifetimeMinutes: timeSpanMinutes(k.mapLifetime),
      createsIncident: k.createsIncident,
      mapVisible: k.mapVisible,
      sortOrder: k.sortOrder,
      legacyEventType: k.legacyEventType ?? undefined,
    })
  }
  const legend = [...kinds.values()]
    .filter((k) => k.createsIncident && k.mapVisible)
    .sort((a, b) => a.sortOrder - b.sortOrder || a.code.localeCompare(b.code))
    .map((k) => ({ code: k.code, label: k.name, color: k.color, shape: k.shape }))
  const legacy = new Set<string>()
  for (const k of kinds.values()) if (k.createsIncident && k.legacyEventType) legacy.add(k.legacyEventType)
  const kindOf = (code: string) => kinds.get(code) ?? unknownKind(code)
  const byLegacy = new Map<string, CatalogKind>()
  for (const k of kinds.values()) if (k.legacyEventType) byLegacy.set(k.legacyEventType, k)
  return {
    kinds,
    legend,
    kindOf,
    colorOf: (code) => kindOf(code).color,
    shapeOf: (code) => kindOf(code).shape,
    lifetimeMinutesOf: (code) => kindOf(code).lifetimeMinutes,
    legacyEventTypesOfIncidents: legacy,
    colorOfLegacy: (eventType) => byLegacy.get(eventType)?.color,
  }
}

export const EMPTY_CATALOG: Catalog = buildCatalog([])

/** Catalog visibility × the viewer's filter: a kind the catalog hides never shows, whatever the viewer toggled. */
export function isKindShown(catalog: Catalog, code: string, userHidden: ReadonlySet<string>): boolean {
  return catalog.kindOf(code).mapVisible && !userHidden.has(code)
}
