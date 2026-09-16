import type * as maplibregl from 'maplibre-gl'
import type { Feature, FeatureCollection, Geometry, Point, Polygon } from 'geojson'
import type { IncidentDto } from '../api/incidents'
import type { RegionDto, TargetDto } from '../api/types'
import type { Catalog, IncidentShape } from '../catalog/catalog'
import { isKindShown } from '../catalog/catalog'
import { TEXT_FONT } from './layers'
import type { MapPalette } from './palette'

/**
 * Incident layer (plan §8.5–8.6, P11). Geometry follows the precision the API states: `point`/`city` → a glyph at the
 * reported point (a city marker, labelled as such — never an address); `district`/`region` → the place polygon when the
 * client has it, else a circle of the error radius, with the glyph as the click anchor at the centroid and an explicit
 * "≈" precision label; `unknown`/no location → not on the map at all (feed/admin only). Clusters when zoomed out.
 * The same builder serves MapView and KyivMapView.
 */

export interface IncidentPointProps {
  id: number
  kind: string
  state: string
  icon: string
  precision: string
  /** "" for a located point/city, "≈" for an area anchor. */
  approx: string
  opacity: number
}

export interface IncidentAreaProps {
  id: number
  kind: string
  color: string
  opacity: number
}

export interface IncidentLayers {
  points: FeatureCollection<Point, IncidentPointProps>
  areas: FeatureCollection<Polygon | Geometry, IncidentAreaProps>
}

export interface IncidentLayerOptions {
  /** Oblast/raion/city polygons the client already holds (from /api/places/regions), by place id. */
  regionsById?: ReadonlyMap<number, RegionDto>
  /** Polygons fetched on demand (hromadas, towns), by place id. */
  placeGeometries?: Record<number, Geometry>
  /** Asks the store to fetch a polygon it does not have yet (drawn on the next pass; the circle fills in meanwhile). */
  ensurePlaceGeometry?: (placeId: number) => void
  /** The viewer's own per-kind switches; catalog visibility applies on top (they are different settings). */
  hiddenKinds?: ReadonlySet<string>
  /** The layer switch: off → nothing is drawn (and the legacy event markers come back). */
  enabled?: boolean
}

export const INCIDENT_SOURCES = { points: 'incident-points', areas: 'incident-areas' } as const
export const INCIDENT_HIT_LAYERS = ['incident-icons', 'incident-clusters']

export function emptyIncidentLayers(): IncidentLayers {
  return { points: { type: 'FeatureCollection', features: [] }, areas: { type: 'FeatureCollection', features: [] } }
}

/** True while the incident is inside its kind's map lifetime (the client's window; the server's window bounds what it sends). */
export function isOnMap(dto: IncidentDto, catalog: Catalog, now: Date): boolean {
  if (dto.state === 'retracted' || dto.suppressed || dto.mergedIntoIncidentId !== undefined) return false
  const age = (now.getTime() - new Date(dto.lastReportedAt).getTime()) / 60_000
  return age >= -5 && age <= catalog.lifetimeMinutesOf(dto.kind)
}

/** A polygon approximating the circle of `radiusKm` around the point (64 vertices), for areas without a known polygon. */
export function circlePolygon(lon: number, lat: number, radiusKm: number, steps = 64): Polygon {
  const coords: [number, number][] = []
  const latRad = (lat * Math.PI) / 180
  const dLat = radiusKm / 111.32
  const dLon = radiusKm / (111.32 * Math.max(0.1, Math.cos(latRad)))
  for (let i = 0; i <= steps; i++) {
    const a = (2 * Math.PI * i) / steps
    coords.push([lon + dLon * Math.cos(a), lat + dLat * Math.sin(a)])
  }
  return { type: 'Polygon', coordinates: [coords] }
}

export function buildIncidentLayers(incidents: Iterable<IncidentDto>, catalog: Catalog, now: Date, opts: IncidentLayerOptions = {}): IncidentLayers {
  const layers = emptyIncidentLayers()
  if (opts.enabled === false) return layers
  const hidden = opts.hiddenKinds ?? new Set<string>()
  for (const dto of incidents) {
    if (!isOnMap(dto, catalog, now) || !isKindShown(catalog, dto.kind, hidden)) continue
    const loc = dto.location
    if (!loc || loc.precision === 'unknown' || !loc.point) continue // §8.5: no invented coordinates
    const kind = catalog.kindOf(dto.kind)
    const age = (now.getTime() - new Date(dto.lastReportedAt).getTime()) / 60_000
    const opacity = Math.max(0.4, 1 - 0.6 * age / Math.max(1, kind.lifetimeMinutes))
    const isArea = loc.precision === 'district' || loc.precision === 'region'
    if (isArea) {
      let polygon: Geometry | undefined = loc.placeId !== undefined ? (opts.regionsById?.get(loc.placeId)?.geometry ?? opts.placeGeometries?.[loc.placeId]) : undefined
      if (!polygon && loc.placeId !== undefined) opts.ensurePlaceGeometry?.(loc.placeId)
      if (!polygon) polygon = circlePolygon(loc.point.coordinates[0], loc.point.coordinates[1], loc.accuracyKm ?? 25)
      layers.areas.features.push({ type: 'Feature', id: dto.id, geometry: polygon, properties: { id: dto.id, kind: dto.kind, color: kind.color, opacity: opacity * 0.35 } })
    }
    const point: Feature<Point, IncidentPointProps> = {
      type: 'Feature',
      id: dto.id,
      geometry: loc.point,
      properties: { id: dto.id, kind: dto.kind, state: dto.state, icon: `incident-${kind.icon}`, precision: loc.precision, approx: isArea ? '≈' : '', opacity },
    }
    layers.points.features.push(point)
  }
  return layers
}

/** Legacy event markers of facts that now live as incidents are hidden while the incident layer is on (review B3: one marker per explosion). */
export function withoutIncidentEvents(events: TargetDto[], catalog: Catalog, incidentLayerOn: boolean): TargetDto[] {
  if (!incidentLayerOn || catalog.legacyEventTypesOfIncidents.size === 0) return events
  return events.filter((e) => !catalog.legacyEventTypesOfIncidents.has(e.eventType))
}

// ---- MapLibre wiring (shared by both maps) ----

export function addIncidentSources(map: maplibregl.Map) {
  const empty = { type: 'FeatureCollection', features: [] } as FeatureCollection
  map.addSource(INCIDENT_SOURCES.areas, { type: 'geojson', data: empty })
  map.addSource(INCIDENT_SOURCES.points, { type: 'geojson', data: empty, cluster: true, clusterRadius: 36, clusterMaxZoom: 9 })
}

/** Glyphs per catalog icon, drawn once per (re)style: shape carries the meaning, the catalog colour tints it. */
export function addIncidentIcons(map: maplibregl.Map, catalog: Catalog, p: MapPalette) {
  const icons = new Map<string, { color: string; shape: IncidentShape }>()
  for (const k of catalog.kinds.values()) icons.set(k.icon, { color: k.color, shape: k.shape })
  if (!icons.has('unknown')) icons.set('unknown', { color: '#dc2626', shape: 'circle' })
  for (const [icon, { color, shape }] of icons) {
    const id = `incident-${icon}`
    if (map.hasImage(id)) map.removeImage(id)
    map.addImage(id, drawIncidentGlyph(color, shape, p), { pixelRatio: 2 })
  }
}

/** The first track layer: incidents (areas and glyphs) sit under the moving targets, above the alert fills. */
export const INCIDENT_BEFORE_LAYER = 'hover-region-fill'

/** Layer specs (exported so a test can check every symbol layer names the style's font — a symbol layer waiting on glyphs holds up its whole source). */
export function incidentLayerSpecs(p: MapPalette): maplibregl.LayerSpecification[] {
  return [
    { id: 'incident-area-fill', type: 'fill', source: INCIDENT_SOURCES.areas, paint: { 'fill-color': ['get', 'color'], 'fill-opacity': ['get', 'opacity'] } },
    { id: 'incident-area-line', type: 'line', source: INCIDENT_SOURCES.areas, paint: { 'line-color': ['get', 'color'], 'line-width': 1.2, 'line-dasharray': [3, 2], 'line-opacity': 0.9 } },
    {
      id: 'incident-clusters',
      type: 'circle',
      source: INCIDENT_SOURCES.points,
      filter: ['has', 'point_count'],
      paint: { 'circle-color': '#dc2626', 'circle-opacity': 0.85, 'circle-radius': ['step', ['get', 'point_count'], 12, 10, 16, 50, 22], 'circle-stroke-color': p.glyphHalo, 'circle-stroke-width': 2 },
    },
    {
      id: 'incident-cluster-count',
      type: 'symbol',
      source: INCIDENT_SOURCES.points,
      filter: ['has', 'point_count'],
      layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-font': TEXT_FONT, 'text-size': 11, 'text-allow-overlap': true },
      paint: { 'text-color': '#ffffff' },
    },
    {
      id: 'incident-icons',
      type: 'symbol',
      source: INCIDENT_SOURCES.points,
      filter: ['!', ['has', 'point_count']],
      layout: { 'icon-image': ['get', 'icon'], 'icon-size': ['match', ['get', 'approx'], '≈', 0.8, 1], 'icon-allow-overlap': true, 'icon-ignore-placement': true },
      paint: { 'icon-opacity': ['get', 'opacity'] },
    },
  ]
}

export function addIncidentLayers(map: maplibregl.Map, p: MapPalette) {
  const beforeId = map.getLayer(INCIDENT_BEFORE_LAYER) ? INCIDENT_BEFORE_LAYER : undefined
  for (const spec of incidentLayerSpecs(p)) map.addLayer(spec, beforeId)
}

export function setIncidentData(map: maplibregl.Map, layers: IncidentLayers) {
  ;(map.getSource(INCIDENT_SOURCES.points) as maplibregl.GeoJSONSource | undefined)?.setData(layers.points as FeatureCollection)
  ;(map.getSource(INCIDENT_SOURCES.areas) as maplibregl.GeoJSONSource | undefined)?.setData(layers.areas as FeatureCollection)
}

/** The incident under a click, or null; a cluster zooms in instead and returns null. */
export function incidentHitAt(map: maplibregl.Map, point: maplibregl.Point): number | null {
  const layers = INCIDENT_HIT_LAYERS.filter((l) => map.getLayer(l))
  if (layers.length === 0) return null
  const f = map.queryRenderedFeatures(point, { layers })[0]
  if (!f) return null
  if (f.properties?.point_count !== undefined) {
    const [lon, lat] = (f.geometry as Point).coordinates
    map.easeTo({ center: [lon, lat], zoom: Math.min(map.getZoom() + 2, 12) })
    return null
  }
  return f.properties?.id === undefined ? null : Number(f.properties.id)
}

const GLYPH = 40
const HALO = 8

function drawIncidentGlyph(color: string, shape: IncidentShape, p: MapPalette): ImageData {
  const canvas = document.createElement('canvas')
  canvas.width = GLYPH
  canvas.height = GLYPH
  const ctx = canvas.getContext('2d')!
  const c = GLYPH / 2
  const r = c - HALO / 2 - 3
  const path = new Path2D()
  switch (shape) {
    case 'burst':
      for (let i = 0; i < 16; i++) {
        const a = (Math.PI * i) / 8
        const rr = i % 2 === 0 ? r : r * 0.55
        const x = c + rr * Math.cos(a)
        const y = c + rr * Math.sin(a)
        if (i === 0) path.moveTo(x, y)
        else path.lineTo(x, y)
      }
      path.closePath()
      break
    case 'flame':
      path.moveTo(c, c - r)
      path.quadraticCurveTo(c + r, c, c, c + r)
      path.quadraticCurveTo(c - r, c, c, c - r)
      break
    case 'bolt':
      path.moveTo(c + r * 0.15, c - r)
      path.lineTo(c - r * 0.55, c + r * 0.1)
      path.lineTo(c, c + r * 0.1)
      path.lineTo(c - r * 0.15, c + r)
      path.lineTo(c + r * 0.55, c - r * 0.1)
      path.lineTo(c, c - r * 0.1)
      path.closePath()
      break
    case 'square':
      path.rect(c - r * 0.8, c - r * 0.8, r * 1.6, r * 1.6)
      break
    case 'shield':
      path.moveTo(c, c - r)
      path.lineTo(c + r, c - r * 0.4)
      path.lineTo(c + r * 0.7, c + r * 0.5)
      path.lineTo(c, c + r)
      path.lineTo(c - r * 0.7, c + r * 0.5)
      path.lineTo(c - r, c - r * 0.4)
      path.closePath()
      break
    case 'chevron':
      path.moveTo(c, c - r)
      path.lineTo(c + r, c + r * 0.6)
      path.lineTo(c, c + r * 0.1)
      path.lineTo(c - r, c + r * 0.6)
      path.closePath()
      break
    default:
      path.arc(c, c, r * 0.8, 0, Math.PI * 2)
  }
  ctx.lineJoin = 'round'
  ctx.strokeStyle = p.glyphHalo
  ctx.lineWidth = HALO
  ctx.stroke(path)
  ctx.fillStyle = color
  ctx.fill(path)
  ctx.strokeStyle = p.glyphEdge
  ctx.lineWidth = 1.5
  ctx.stroke(path)
  return ctx.getImageData(0, 0, GLYPH, GLYPH)
}
