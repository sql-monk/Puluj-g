import destination from '@turf/destination'
import difference from '@turf/difference'
import distance from '@turf/distance'
import { point } from '@turf/helpers'
import type { Feature, FeatureCollection, GeoJsonProperties, Geometry, LineString, Point, Polygon, MultiPolygon, Position } from 'geojson'
import type { AlertDto, AlertLevel, Confidence, MapId, PredecessorLinkDto, PredecessorsDto, RegionDto, TargetDto, TrackDto } from '../api/types'
import { computeEta, distanceToRegionKm, type Home } from '../eta/computeEta'
import { effectiveLevel } from '../lib/alerts'
import type { ReplayPosition } from '../replay/engine'
import { displayModeEnabled, sourceEnabled, type Filters, type SelectedLink } from '../store/useStore'

import { getPalette, type MapPalette } from './palette'

/** Why a track is highlighted for the viewer's own point: it is close by, or it is heading this way. */
export type HazardKind = 'near' | 'towards' | ''

export interface TrackProps {
  id: MapId
  label: string
  mode: string
  /** Marker / last-known-area colour: the class colour, or the class's selection colour when selected. */
  color: string
  /** Colour of the forecast vector: the selection colour of the selected target, the theme's neutral grey otherwise. */
  vector: string
  opacity: number
  rotation: number
  hasDirection: boolean
  status: string
  kind: string
  /** Position is an approach-zone anchor ("на Конотоп"), not a fix. */
  approx: boolean
  ageMin: number
  /** Independent sources behind the track (badge). */
  sources: number
  /** Objects in the group when the report counted more than one (badge), else 0. */
  count: number
  hazard: HazardKind
  selected: boolean
}

/** A crumb: where the target was reported earlier, with the time; or the dotted link between crumbs. */
export interface FixProps {
  id: MapId
  mode: string
  /** The selected target's selection colour (crumbs and predecessors exist only for the selected target). */
  vector: string
  /** "Ромни 21:40" — the place (if known) and the time of that report. */
  label: string
  opacity: number
  approach: boolean
  /** 0..1 from the kinematic link (1 for the marker itself). */
  probability: number
  /** The node's reported course, when it had one: the glyph is an arrow turned by it, else a dot. */
  hasDirection: boolean
  rotation: number
  /** Glyph size (icon-size before the map's icon scale); 0 on a leg. */
  size: number
  /** Generations above the head (family only): 1 = parent's level, 2 = grandparent's, 0 = the head's own. */
  generation?: number
  /** A family leg: the two reports it joins, its own and its path probability, its kind, and whether it is the clicked one. */
  from?: MapId
  to?: MapId
  linkProbability?: number
  pathProbability?: number
  linkKind?: string
  selectedLink?: boolean
}

export interface TrackLayers {
  points: FeatureCollection<Point, TrackProps>
  /** Crumbs (points) and the dotted links from crumb to crumb to the marker. */
  fixes: FeatureCollection<Point | LineString, FixProps>
  /** Dashed forecast centreline, hatched probability cone and the chevron at its end. */
  forecasts: FeatureCollection<LineString | Point | Polygon, TrackProps>
  areas: FeatureCollection<Polygon | MultiPolygon, TrackProps>
  predecessors: FeatureCollection<Point | LineString, FixProps>
}

/** A point-in-time public report. Unlike a target track, it has no inferred course, path, or forecast. */
export interface EventProps {
  id: MapId
  eventType: string
  color: string
  opacity: number
}

export function buildEventLayer(events: TargetDto[], now: Date, filters: Filters, colorOf?: (eventType: string) => string | undefined): FeatureCollection<Point, EventProps> {
  if (!filters.events) return emptyCollection() as FeatureCollection<Point, EventProps>
  const features: Feature<Point, EventProps>[] = []
  for (const event of events) {
    const point = event.location?.point
    if (!point || !sourceEnabled(event.source.id, filters)) continue
    const age = Math.max(0, (now.getTime() - new Date(event.observedAt).getTime()) / 60000)
    if (age > filters.lifetimeMinutes) continue
    const color = colorOf?.(event.eventType) ?? (event.eventType === 'ExplosionReport' ? '#dc2626' : event.eventType === 'AirDefenseActivity' ? '#2563eb' : '#16a34a')
    features.push({ type: 'Feature', id: event.id, geometry: point, properties: { id: event.id, eventType: event.eventType, color, opacity: Math.max(0.35, 1 - 0.6 * age / Math.max(1, filters.lifetimeMinutes)) } })
  }
  return { type: 'FeatureCollection', features }
}

/** The forecast reaches this far ahead: a short pointer, not a flight plan. */
const FORECAST_MINUTES = 6
const MIN_FORECAST_KM = 8
const DEFAULT_FORECAST_KM = 20
/** The tail shows at most this much of the observed path behind the marker, and at most TAIL_SEGMENTS legs. */
export const TAIL_MAX_KM = 25
export const TAIL_SEGMENTS = 3
/** Half-angle of the forecast cone by confidence in the reported course. */
const CONE_HALF_ANGLE: Record<Confidence, number> = { Confirmed: 10, High: 12, Medium: 18, Low: 28, Unknown: 28 }
/** A track this close to the viewer's point counts as "near" regardless of its course. */
const NEAR_KM = 25

export interface TrackLayerOptions {
  home?: Home | null
  selectedId?: MapId | null
  /** Theme palette; the light one when omitted. */
  palette?: MapPalette
  /** The predecessor fork of the selected track, drawn instead of its crumbs. */
  predecessors?: PredecessorsDto | null
  /** The leg the viewer clicked, drawn with a halo. */
  selectedLink?: SelectedLink | null
}

/** Tracks that pass the class, status and source filters and are not fully faded. */
/** Minutes since the track's last message (never negative). */
export function ageMinutes(t: TrackDto, now: Date): number {
  return Math.max(0, (now.getTime() - new Date(t.lastSeenAt).getTime()) / 60000)
}

export function visibleTracks(tracks: Record<number, TrackDto>, filters: Filters, now: Date): TrackDto[] {
  return Object.values(tracks).filter((t) => {
    if (!displayModeEnabled(t.type.displayMode, filters)) return false
    if (filters.activeOnly && t.status !== 'Active') return false
    if (filters.sources !== null && !t.sourceIds.some((id) => filters.sources!.includes(id))) return false
    // A target lives on the map for the viewer's chosen time after its last message, whatever its status.
    return ageMinutes(t, now) <= filters.lifetimeMinutes
  })
}

/**
 * Is the track close to the viewer's point, or plausibly heading towards it? Pure, per track: cheap enough per render.
 * "Near" for a region-level report means the point lies inside that region (its polygon, when known): the region's
 * covering radius would otherwise call a whole neighbouring oblast "near".
 */
export function hazardKind(t: TrackDto, home: Home, now: Date, regionsById?: Map<number, RegionDto>): HazardKind {
  const loc = t.lastLocation
  if (!loc?.point || t.status !== 'Active') return ''
  const km = distance(point(loc.point.coordinates), point([home.lon, home.lat]), { units: 'kilometers' })
  let near: boolean
  if (loc.kind === 'Region' || loc.kind === 'Area') {
    // Inside the region, or within NEAR_KM of its edge (Kyiv city is a hole in the Kyiv oblast polygon).
    const region = loc.placeId ? regionsById?.get(loc.placeId) : undefined
    const edge = region ? distanceToRegionKm(home, region.geometry) : null
    near = edge !== null ? edge <= NEAR_KM : km <= NEAR_KM
  } else {
    near = km <= NEAR_KM + Math.min(loc.accuracyKm ?? 0, NEAR_KM)
  }
  if (near) return 'near'
  const eta = computeEta(t, home, now, regionsById)
  return eta.kind === 'range' || eta.kind === 'imminent' ? 'towards' : ''
}

export function buildTrackLayers(tracks: TrackDto[], now: Date, regionsById: Map<number, RegionDto>, filters: Filters, opts: TrackLayerOptions = {}): TrackLayers {
  const points: Feature<Point, TrackProps>[] = []
  const fixes: Feature<Point | LineString, FixProps>[] = []
  const forecasts: Feature<LineString | Point | Polygon, TrackProps>[] = []
  const areas: Feature<Polygon | MultiPolygon, TrackProps>[] = []
  const predecessors: Feature<Point | LineString, FixProps>[] = []
  const home = filters.highlightTargets ? (opts.home ?? null) : null
  const palette = opts.palette ?? getPalette('light')

  for (const t of tracks) {
    // Fades over the viewer's lifetime setting, never below 0.45: a faint marker is unreadable, and the age is on the card.
    const opacity = t.status === 'Active' ? Math.max(0.45, 1 - 0.55 * (ageMinutes(t, now) / Math.max(1, filters.lifetimeMinutes))) : 0.25
    const selected = opts.selectedId === t.id
    const mode = t.type.displayMode
    const selectedColor = palette.selected[mode] ?? palette.selected.uav
    const props: TrackProps = {
      id: t.id,
      label: t.type.label,
      mode,
      color: selected ? selectedColor : (palette.marker[mode] ?? palette.marker.uav),
      vector: selected ? selectedColor : palette.vectorMuted,
      opacity,
      rotation: t.direction?.degrees ?? 0,
      hasDirection: !!t.direction,
      status: t.status,
      kind: t.lastLocation?.kind ?? 'Unknown',
      approx: t.lastLocation?.kind === 'DirectionOnly',
      ageMin: Math.round((now.getTime() - new Date(t.lastSeenAt).getTime()) / 60000),
      sources: Math.max(t.distinctSourceCount, t.sourceIds.length),
      count: t.objectCount && t.objectCount > 1 ? t.objectCount : 0,
      hazard: home ? hazardKind(t, home, now, regionsById) : '',
      selected,
    }
    const loc = t.lastLocation
    if (loc?.point) {
      points.push({ type: 'Feature', id: t.id, geometry: loc.point, properties: props })

      // Region / area level locations are drawn as the area itself, never as a precise dot (spec §6).
      if ((loc.kind === 'Region' || loc.kind === 'Area' || loc.kind === 'District') && loc.placeId) {
        const region = regionsById.get(loc.placeId)
        if (region && (region.geometry.type === 'Polygon' || region.geometry.type === 'MultiPolygon')) {
          areas.push({ type: 'Feature', id: t.id, geometry: region.geometry as Polygon | MultiPolygon, properties: props })
        }
      }

      // Forecast: reported course projected a few minutes ahead at the class speed. Dashed centreline (clearly "forecast")
      // inside a hatched cone whose width says how sure the course is; chevron at the end for the heading.
      if (filters.forecast && t.direction && t.type.displayMode !== 'ballistic' && t.status === 'Active') {
        const speed = t.type.speedProfile.maxKmh
        const km = speed ? Math.max(MIN_FORECAST_KM, (speed * FORECAST_MINUTES) / 60) : DEFAULT_FORECAST_KM
        const origin = loc.point.coordinates
        const end = destination(point(origin), km, t.direction.degrees, { units: 'kilometers' })
        forecasts.push({ type: 'Feature', id: t.id, geometry: cone(origin, km, t.direction.degrees, CONE_HALF_ANGLE[t.direction.confidence]), properties: props })
        forecasts.push({ type: 'Feature', id: t.id, geometry: { type: 'LineString', coordinates: [origin, end.geometry.coordinates] }, properties: props })
        forecasts.push({ type: 'Feature', id: t.id, geometry: end.geometry, properties: props })
      }
    }
    // The selected target shows its whole predecessor fork instead of the single best chain.
    const fork = opts.predecessors && opts.predecessors.trackId === t.id && opts.predecessors.links.length > 0 ? opts.predecessors : null
    if (fork && loc?.point) {
      const byId = new Map(fork.targets.map((n) => [n.targetId, n]))
      const headPoint = loc.point
      const pointOf = (id: MapId): Position | null => (id === fork.headTargetId ? headPoint.coordinates : (byId.get(id)?.point?.coordinates ?? null))
      const located = (l: PredecessorLinkDto) => pointOf(l.fromTargetId) !== null && pointOf(l.toTargetId) !== null
      const clamp = (v: number) => Math.max(0.05, Math.min(1, v))
      // Ancestry: every parent, and behind each parent its two most probable grandparents (the rest is in the details
      // list; on the map it would only be a tangle of faint legs).
      const gen2Rank = new Map<MapId, number>()
      const shown = fork.links
        .filter((l) => l.ancestral && located(l))
        .sort((x, y) => x.generation - y.generation || y.pathProbability - x.pathProbability)
        .filter((l) => {
          if (l.generation < 2) return true
          const rank = gen2Rank.get(l.toTargetId) ?? 0
          gen2Rank.set(l.toTargetId, rank + 1)
          return rank < 2
        })
      const ancestors = new Set([fork.headTargetId, ...shown.map((l) => l.fromTargetId)])
      // Relatives: where else those ancestors could have flown — a parent's other successors (siblings), a grandparent's
      // (uncles) and, one step on, an uncle's (cousins). Three per node and nothing further: no relative's own ancestry
      // or later descendants, only what the selected target itself could have been and where else that could have gone.
      const relatives = fork.links.filter((l) => !l.ancestral && located(l) && l.pathProbability >= 0.02).sort((x, y) => y.pathProbability - x.pathProbability)
      const perNode = new Map<MapId, number>()
      const seen = new Set(shown.map((l) => `${l.fromTargetId}>${l.toTargetId}`))
      const drawn = new Set(ancestors)
      const hang = (offAncestors: boolean) => {
        for (const l of relatives) {
          const key = `${l.fromTargetId}>${l.toTargetId}`
          if (seen.has(key) || !drawn.has(l.fromTargetId) || ancestors.has(l.fromTargetId) !== offAncestors) continue
          const rank = perNode.get(l.fromTargetId) ?? 0
          if (rank >= 3) continue
          perNode.set(l.fromTargetId, rank + 1)
          seen.add(key)
          shown.push(l)
          drawn.add(l.toTargetId)
        }
      }
      hang(true) // siblings and uncles, off the ancestors
      hang(false) // cousins, off the uncles
      // The best path through each node: its opacity and the percentage in its label.
      const best = new Map<MapId, number>()
      for (const l of shown) {
        const node = l.ancestral ? l.fromTargetId : l.toTargetId
        best.set(node, Math.max(best.get(node) ?? 0, l.pathProbability))
      }
      // Legs: the more probable it is that the object reported at A is the one reported at B, the thicker and the more
      // opaque the leg (the link's own probability; the path product only goes into the labels).
      const sl = opts.selectedLink
      shown.forEach((l, i) => {
        const p = clamp(l.probability)
        predecessors.push({
          type: 'Feature',
           id: `${t.id}:pred-leg:${i}`,
          geometry: { type: 'LineString', coordinates: [pointOf(l.fromTargetId)!, pointOf(l.toTargetId)!] },
          properties: {
            id: t.id,
            mode: props.mode,
            vector: selectedColor,
            label: `${Math.round(l.probability * 100)}%`,
            opacity: 0.15 + 0.85 * p,
            approach: false,
            probability: p,
            hasDirection: false,
            rotation: 0,
            size: 0,
            generation: l.generation,
            from: l.fromTargetId,
            to: l.toTargetId,
            linkProbability: l.probability,
            pathProbability: l.pathProbability,
            linkKind: l.kind,
            selectedLink: !!sl && sl.fromTargetId === l.fromTargetId && sl.toTargetId === l.toTargetId,
          },
        })
      })
      // Every node a leg touches is drawn as the target it is — its class glyph turned by its course, a dot without
      // one — as big and as opaque as the best path through it, so no leg ends in empty ground.
      let k = 0
      for (const id of drawn) {
        const n = byId.get(id)
        if (id === fork.headTargetId || !n?.point) continue
        const time = new Date(n.at).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
        const p = clamp(best.get(id) ?? 0)
        predecessors.push({
          type: 'Feature',
           id: `${t.id}:pred-node:${k++}`,
          geometry: n.point,
          properties: {
            id: t.id,
            mode: props.mode,
            vector: selectedColor,
            label: `${n.approach ? '→ ' : ''}${n.placeName ?? ''} ${time} · ${Math.round(p * 100)}%`.trim(),
            opacity: 0.3 + 0.7 * p,
            approach: n.approach,
            probability: p,
            hasDirection: n.directionDeg != null,
            rotation: n.directionDeg ?? 0,
            size: 0.36 + 0.24 * p,
            generation: n.generation,
          },
        })
      }
    }
    // Crumbs: the earlier reported positions, each with its time, linked by a dotted line up to the marker.
    // Only the selected target has them (when the fork is not available); the rest of the map shows forecasts alone.
    if (!fork && selected && loc?.point && t.fixes.length >= 2) {
      const previous = t.fixes.slice(0, -1)
      const chain: Position[] = [...previous.map((f) => f.point.coordinates), loc.point.coordinates]
      // Each crumb carries the probability of the link that leads from it to the next position: the crumb and the
      // dotted leg after it are as opaque as that probability, and the label says it in percent.
      previous.forEach((f, i) => {
        const time = new Date(f.at).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
        const p = Math.max(0.05, Math.min(1, f.probability))
        fixes.push({
          type: 'Feature',
           id: `${t.id}:fix:${i}`,
          geometry: f.point,
          properties: { id: t.id, mode: props.mode, vector: selectedColor, label: `${f.approach ? '→ ' : ''}${f.placeName ?? ''} ${time} · ${Math.round(p * 100)}%`.trim(), opacity: 0.3 + 0.7 * p, approach: f.approach, probability: p, hasDirection: false, rotation: 0, size: 0.22 + 0.2 * p },
        })
        fixes.push({ type: 'Feature', id: `${t.id}:fix-link:${i}`, geometry: { type: 'LineString', coordinates: [chain[i], chain[i + 1]] }, properties: { id: t.id, mode: props.mode, vector: selectedColor, label: '', opacity: 0.2 + 0.8 * p, approach: false, probability: p, hasDirection: false, rotation: 0, size: 0 } })
      })
    }
  }
  return {
    points: { type: 'FeatureCollection', features: points },
    fixes: { type: 'FeatureCollection', features: fixes },
    forecasts: { type: 'FeatureCollection', features: forecasts },
    areas: { type: 'FeatureCollection', features: areas },
    predecessors: { type: 'FeatureCollection', features: predecessors },
  }
}

/**
 * Replay (timelapse) layers: only the markers, at their reconstructed positions, the nose along the movement. No
 * vectors, crumbs, areas, labels or badges — the picture is the movement itself, as in a time-lapse of the night.
 */
export function buildReplayLayers(positions: ReplayPosition[], palette: MapPalette, selectedId: MapId | null | undefined): TrackLayers {
  const empty = <G extends Geometry, P>(): FeatureCollection<G, P> => ({ type: 'FeatureCollection', features: [] })
  const points: Feature<Point, TrackProps>[] = positions.map((p) => {
    const mode = p.type.displayMode
    const selected = selectedId === p.id
    return {
      type: 'Feature',
      id: p.id,
      geometry: { type: 'Point', coordinates: [p.lon, p.lat] },
      properties: {
        id: p.id,
        label: '',
        mode,
        color: selected ? (palette.selected[mode] ?? palette.selected.uav) : (palette.marker[mode] ?? palette.marker.uav),
        vector: palette.vectorMuted,
        opacity: p.opacity,
        rotation: Number.isFinite(p.rotation) ? p.rotation : 0,
        hasDirection: Number.isFinite(p.rotation),
        status: 'Active',
        kind: p.approach ? 'DirectionOnly' : 'Point',
        approx: p.approach,
        ageMin: 0,
        sources: 0,
        count: 0,
        hazard: '',
        selected,
      },
    }
  })
  return { points: { type: 'FeatureCollection', features: points }, fixes: empty(), forecasts: empty(), areas: empty(), predecessors: empty() }
}

/** Sector of a circle: where the object may be after the forecast period if it holds roughly the reported course. */
function cone(origin: Position, km: number, bearing: number, halfAngle: number, steps = 8): Polygon {
  const ring: Position[] = [origin]
  for (let i = 0; i <= steps; i++) {
    const b = bearing - halfAngle + (2 * halfAngle * i) / steps
    ring.push(destination(point(origin), km, b, { units: 'kilometers' }).geometry.coordinates)
  }
  ring.push(origin)
  return { type: 'Polygon', coordinates: [ring] }
}

export interface AlertProps {
  /** The earliest alert on the place: the feature's identity. */
  id: MapId
  placeId: number
  placeName: string
  alertType: string
  /** The effective level at this place (its own alerts and those covering it), the colour of the fill. */
  level: AlertLevel
  startedAt: string
}

/** Alerts drawn as the polygon of their place (oblast, raion, hromada); the rest become circles of the stated radius. */
export function isPolygonAlert(a: AlertDto, regionsById: Map<number, RegionDto>, extra: Record<number, Geometry> = {}): boolean {
  return regionsById.has(a.placeId) || a.placeId in extra
}

function circle(center: Position, km: number, steps = 48): Polygon {
  const ring: Position[] = []
  for (let i = 0; i <= steps; i++) {
    ring.push(destination(point(center), km, (i * 360) / steps, { units: 'kilometers' }).geometry.coordinates)
  }
  return { type: 'Polygon', coordinates: [ring] }
}

type Area = Polygon | MultiPolygon

/**
 * One fill per alerted place, and one colour at every point of the map. Alerts are grouped by place; a place's level is
 * the effective level of its own alerts and those covering it. A place whose nearest alerted ancestor already shows the
 * same level is left out (a yellow raion in a yellow oblast); the rest are drawn with the polygons of their drawn
 * descendants cut out, so a red raion in a yellow oblast is red, not the orange two translucent fills would blend to.
 * Places without a polygon get a circle of the stated radius.
 */
export function buildAlertLayer(alerts: AlertDto[], regionsById: Map<number, RegionDto>, extra: Record<number, Geometry> = {}): FeatureCollection<Geometry, AlertProps> {
  const byPlace = new Map<number, AlertDto[]>()
  for (const a of alerts) {
    const list = byPlace.get(a.placeId)
    if (list) list.push(a)
    else byPlace.set(a.placeId, [a])
  }
  const geometryOf = (placeId: number): Area | null => {
    const region = regionsById.get(placeId)
    if (region && isArea(region.geometry)) return region.geometry
    const fetched = extra[placeId]
    if (fetched && isArea(fetched)) return fetched
    const loc = byPlace.get(placeId)?.find((a) => a.location?.point)?.location
    return loc?.point ? circle(loc.point.coordinates, Math.max(loc.accuracyKm ?? 0, 20)) : null
  }
  // Alerts on the place and on its ancestors (nearest first) - what the place is under.
  const covering = (placeId: number): AlertDto[] => {
    const own = byPlace.get(placeId) ?? []
    const list = [...own]
    for (const id of own[0]?.ancestorIds ?? []) list.push(...(byPlace.get(id) ?? []))
    return list
  }
  const level = new Map<number, AlertLevel>()
  for (const placeId of byPlace.keys()) level.set(placeId, effectiveLevel(covering(placeId)) ?? 'Unknown')
  const nearestAlertedAncestor = (placeId: number): number | undefined => (byPlace.get(placeId)?.[0]?.ancestorIds ?? []).find((id) => byPlace.has(id))
  // Outer places first: a redundant inner one is dropped, a different one is drawn and cut from every drawn ancestor.
  const drawn = [...byPlace.keys()].filter((placeId) => {
    const parent = nearestAlertedAncestor(placeId)
    return parent === undefined || level.get(parent) !== level.get(placeId)
  })
  const depth = (placeId: number) => byPlace.get(placeId)![0].ancestorIds.length
  drawn.sort((a, b) => depth(a) - depth(b))
  const features: Feature<Geometry, AlertProps>[] = []
  for (const placeId of drawn) {
    let geometry = geometryOf(placeId)
    if (!geometry) continue
    for (const inner of drawn) {
      if (inner === placeId || !byPlace.get(inner)![0].ancestorIds.includes(placeId)) continue
      const hole = geometryOf(inner)
      if (!hole) continue
      const cut = difference({ type: 'FeatureCollection', features: [{ type: 'Feature', geometry, properties: {} }, { type: 'Feature', geometry: hole, properties: {} }] })
      if (!cut) {
        geometry = null
        break
      }
      geometry = cut.geometry
    }
    if (!geometry) continue
    const own = byPlace.get(placeId)!
    const first = own.reduce((m, a) => (a.startedAt < m.startedAt ? a : m), own[0])
    features.push({
      type: 'Feature',
      id: first.id,
      geometry,
      properties: { id: first.id, placeId, placeName: first.placeName, alertType: first.alertType, level: level.get(placeId)!, startedAt: first.startedAt },
    })
  }
  return { type: 'FeatureCollection', features }
}

function isArea(g: Geometry): g is Area {
  return g.type === 'Polygon' || g.type === 'MultiPolygon'
}

export function emptyCollection(): FeatureCollection<Geometry, GeoJsonProperties> {
  return { type: 'FeatureCollection', features: [] }
}
