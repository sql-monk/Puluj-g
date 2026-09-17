import * as maplibregl from 'maplibre-gl'
import type { GeoJSONSource, MapLayerMouseEvent } from 'maplibre-gl'
import type { FeatureCollection, Geometry, Position } from 'geojson'
import type { MapId } from '../api/types'
import type { TrackLayers } from './geojson'
import { DISPLAY_MODES, type MapPalette } from './palette'

export const STYLE_LIGHT = 'https://tiles.openfreemap.org/styles/positron'
export const STYLE_DARK = 'https://tiles.openfreemap.org/styles/dark'
export const ATTRIBUTION = 'Дані: alerts.in.ua, ПС ЗСУ, OSM / geoBoundaries, GeoNames'
/** A font stack the basemap's glyph server actually serves (the MapLibre default 404s there, and a symbol layer
 * waiting on glyphs holds up every layer of its source). */
export const TEXT_FONT = ['Noto Sans Regular']

export function setData(map: maplibregl.Map, id: string, data: FeatureCollection<Geometry, unknown>) {
  const src = map.getSource(id) as GeoJSONSource | undefined
  src?.setData(data as FeatureCollection)
}

/** Pushes every track collection into its source. */
export function setTrackData(map: maplibregl.Map, layers: TrackLayers) {
  setData(map, 'track-points', layers.points)
  setData(map, 'track-fixes', layers.fixes)
  setData(map, 'track-forecasts', layers.forecasts)
  setData(map, 'track-areas', layers.areas)
  setData(map, 'track-predecessors', layers.predecessors)
}

/** Fill / outline colours for alert polygons: yellow level = target (drones), red or unknown level = full alert. */
export function alertPaint(p: MapPalette): { fill: maplibregl.ExpressionSpecification; line: maplibregl.ExpressionSpecification } {
  return {
    fill: ['match', ['get', 'level'], 'Yellow', p.alertYellowFill, p.alertRedFill],
    line: ['match', ['get', 'level'], 'Yellow', p.alertYellowLine, p.alertRedLine],
  }
}

/** Arrow / dot / chevron icons per display mode, hatch patterns for the forecast cones and the two badge pills,
 * all drawn on a canvas so no sprite or font glyph is needed. */
export function addIcons(map: maplibregl.Map, p: MapPalette) {
  // Images are redrawn on every (re)style so a theme change recolours the glyphs too.
  const put = (id: string, img: ImageData) => {
    if (map.hasImage(id)) map.removeImage(id)
    map.addImage(id, img, { pixelRatio: 2 })
  }
  for (const mode of DISPLAY_MODES) {
    const color = p.marker[mode]
    const edge = p.markerEdge[mode]
    const selected = p.selected[mode]
    put(`arrow-${mode}`, drawIcon(color, 'arrow', p, edge))
    put(`dot-${mode}`, drawIcon(color, 'dot', p, edge))
    // Hovered: the same glyph with a wider light halo; the hover layer also draws it larger.
    put(`hov-arrow-${mode}`, drawIcon(color, 'arrow', p, edge, HOVER_HALO))
    put(`hov-dot-${mode}`, drawIcon(color, 'dot', p, edge, HOVER_HALO))
    // Selected: the glyph flips to the class's opposite colour, edged in the class colour so the class stays readable.
    put(`sel-arrow-${mode}`, drawIcon(selected, 'arrow', p, color))
    put(`sel-dot-${mode}`, drawIcon(selected, 'dot', p, color))
    // The selected target's forecast follows its selection colour; every other forecast is the theme's neutral grey.
    put(`head-sel-${mode}`, drawIcon(selected, 'head', p))
    put(`hatch-sel-${mode}`, drawHatch(selected))
  }
  put('head-muted', drawIcon(p.vectorMuted, 'head', p))
  put('hatch-muted', drawHatch(p.vectorMuted))
  // Badges are whole images (pill + number), not text: a symbol layer with text stalls the tile whenever the basemap's
  // glyph server is unreachable, and these must never take the markers down with them.
  for (let n = 1; n <= BADGE_MAX; n++) {
    const src = n === BADGE_MAX ? `${BADGE_MAX - 1}+` : String(n)
    if (!map.hasImage(`badge-src-${n}`)) map.addImage(`badge-src-${n}`, drawPill('#1e40af', '#ffffff', '#ffffff', src), { pixelRatio: 2 })
    if (!map.hasImage(`badge-cnt-${n}`)) map.addImage(`badge-cnt-${n}`, drawPill('#ffffff', '#0f172a', '#0f172a', `×${src}`), { pixelRatio: 2 })
  }
}

/** Badge images exist for 1..BADGE_MAX-1 and "BADGE_MAX-1+". */
const BADGE_MAX = 31
/** Halo width (image px) of the plain glyph and of the hovered one. */
const GLYPH_HALO = 8
const HOVER_HALO = 14
/** How far (css px) from the cursor a target is still picked up by a click or a hover. */
export const HIT_RADIUS = 12

/** Marker glyphs, all pointing "up" (north); the layer rotates them by the course. A light halo, then the class's own
 * light outline around its dark fill, keeps them readable on both basemaps and over alert fills. */
function drawIcon(color: string, shape: 'arrow' | 'dot' | 'head', p: MapPalette, edge?: string, halo = GLYPH_HALO): ImageData {
  const size = 64
  const c = size / 2
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')!
  ctx.lineJoin = 'round'
  ctx.lineCap = 'round'
  const path = () => {
    ctx.beginPath()
    if (shape === 'arrow') {
      // Delta-wing silhouette: long nose, swept wings, notched tail.
      ctx.moveTo(c, 4)
      ctx.lineTo(size - 8, size - 12)
      ctx.lineTo(c, size - 22)
      ctx.lineTo(8, size - 12)
      ctx.closePath()
    } else if (shape === 'head') {
      // Open chevron for the end of the forecast corridor.
      ctx.moveTo(10, size - 14)
      ctx.lineTo(c, 10)
      ctx.lineTo(size - 10, size - 14)
    } else {
      ctx.arc(c, c, 15, 0, Math.PI * 2)
    }
  }
  if (shape === 'head') {
    path()
    ctx.strokeStyle = p.glyphHalo
    ctx.lineWidth = 8
    ctx.stroke()
    path()
    ctx.strokeStyle = color
    ctx.lineWidth = 3.5
    ctx.stroke()
    return ctx.getImageData(0, 0, size, size)
  }
  // A wide light halo, then the class outline: a dark fill must still stand out on a red-level alert fill.
  path()
  ctx.strokeStyle = p.glyphHalo
  ctx.lineWidth = halo
  ctx.stroke()
  path()
  ctx.strokeStyle = edge ?? p.glyphEdge
  ctx.lineWidth = 4.5
  ctx.stroke()
  path()
  ctx.fillStyle = color
  ctx.fill()
  return ctx.getImageData(0, 0, size, size)
}

/** Diagonal stripes on a transparent ground: tiled by fill-pattern over the forecast cone. */
function drawHatch(color: string): ImageData {
  const size = 24
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')!
  ctx.strokeStyle = color
  ctx.lineWidth = 1.6
  ctx.lineCap = 'butt'
  // Three parallel strokes so the tile repeats seamlessly.
  for (const offset of [-size, 0, size]) {
    ctx.beginPath()
    ctx.moveTo(offset, size)
    ctx.lineTo(offset + size, 0)
    ctx.stroke()
  }
  return ctx.getImageData(0, 0, size, size)
}

/** A pill with a short label inside, drawn at 2x (pixelRatio 2 => ~13 css px tall). */
function drawPill(fill: string, stroke: string, text: string, label: string): ImageData {
  const h = 26
  const canvas = document.createElement('canvas')
  const ctx = canvas.getContext('2d')!
  ctx.font = 'bold 15px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif'
  const w = Math.max(h, Math.ceil(ctx.measureText(label).width) + 14)
  canvas.width = w
  canvas.height = h
  const c = canvas.getContext('2d')!
  c.beginPath()
  c.roundRect(1.5, 1.5, w - 3, h - 3, h / 2)
  c.fillStyle = fill
  c.fill()
  c.lineWidth = 2
  c.strokeStyle = stroke
  c.stroke()
  c.font = 'bold 15px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif'
  c.textAlign = 'center'
  c.textBaseline = 'middle'
  c.fillStyle = text
  c.fillText(label, w / 2, h / 2 + 0.5)
  return c.getImageData(0, 0, w, h)
}

export const TRACK_SOURCES = ['track-areas', 'track-fixes', 'track-predecessors', 'track-forecasts', 'track-points', 'home'] as const

export function addTrackSources(map: maplibregl.Map, cluster = true) {
  const empty: FeatureCollection = { type: 'FeatureCollection', features: [] }
  map.addSource('hover-region', { type: 'geojson', data: empty })
  map.addSource('selected-region', { type: 'geojson', data: empty })
  map.addSource('track-areas', { type: 'geojson', data: empty })
  map.addSource('track-fixes', { type: 'geojson', data: empty })
  map.addSource('track-predecessors', { type: 'geojson', data: empty })
  map.addSource('track-forecasts', { type: 'geojson', data: empty })
  // Clustering only folds markers that practically coincide (two reports on one oblast centroid), never neighbours.
  map.addSource('track-points', cluster ? { type: 'geojson', data: empty, cluster: true, clusterRadius: 6, clusterMaxZoom: 10 } : { type: 'geojson', data: empty })
  map.addSource('events', { type: 'geojson', data: empty })
  map.addSource('home', { type: 'geojson', data: empty })
  // U10: selected historical evidence is separate from the live aggregate snapshot.
  map.addSource('selected-evidence', { type: 'geojson', data: empty })
}

export function addSelectionLayers(map: maplibregl.Map, p: MapPalette) {
  map.addLayer({ id: 'selected-evidence-fill', type: 'fill', source: 'selected-evidence', filter: ['!=', ['geometry-type'], 'Point'], paint: { 'fill-color': p.glyphHalo, 'fill-opacity': 0.18 } })
  map.addLayer({ id: 'selected-evidence-line', type: 'line', source: 'selected-evidence', filter: ['!=', ['geometry-type'], 'Point'], paint: { 'line-color': p.glyphEdge, 'line-width': 3 } })
  map.addLayer({ id: 'selected-evidence-point', type: 'circle', source: 'selected-evidence', filter: ['==', ['geometry-type'], 'Point'], paint: { 'circle-color': p.glyphHalo, 'circle-radius': 10, 'circle-stroke-color': p.glyphEdge, 'circle-stroke-width': 3 } })
}

/** Localized reports that are not moving targets: a halo makes them legible without borrowing target glyphs. */
export function addEventLayers(map: maplibregl.Map, p: MapPalette) {
  map.addLayer({
    id: 'event-halo',
    type: 'circle',
    source: 'events',
    paint: { 'circle-color': p.glyphHalo, 'circle-radius': 9, 'circle-opacity': ['get', 'opacity'] },
  })
  map.addLayer({
    id: 'event-points',
    type: 'circle',
    source: 'events',
    paint: {
      'circle-color': ['get', 'color'],
      'circle-radius': ['match', ['get', 'eventType'], 'ExplosionReport', 6, 'AirDefenseActivity', 5, 4],
      'circle-opacity': ['get', 'opacity'],
      'circle-stroke-color': p.glyphEdge,
      'circle-stroke-width': 1.5,
    },
  })
}

/** Every layer a click on a track can land on: marker, badges, crumbs, forecast line / cone / chevron. */
export const TRACK_HIT_LAYERS = ['track-points', 'track-badge-sources', 'track-badge-count', 'track-fixes', 'track-pred-nodes', 'track-forecasts', 'track-forecast-heads', 'track-cone', 'track-forecast-hit', 'track-fix-hit', 'track-pred-hit']

/** Track layers shared by every map: hovered / clicked region, last-known area, predecessors, crumbs, forecast cone + line +
 * chevron, hazard rings, markers, badges, labels, home. */
export function addTrackLayers(map: maplibregl.Map, p: MapPalette, opts: { labelMinZoom?: number; iconScale?: number } = {}) {
  const iconScale = opts.iconScale ?? 1
  // Region under the cursor (raion / oblast / city district): a light tint and a firm outline in the border ink.
  map.addLayer({
    id: 'hover-region-fill',
    type: 'fill',
    source: 'hover-region',
    paint: { 'fill-color': p.border, 'fill-opacity': 0.08 },
  })
  map.addLayer({
    id: 'hover-region-line',
    type: 'line',
    source: 'hover-region',
    layout: { 'line-join': 'round' },
    paint: { 'line-color': p.border, 'line-width': 2, 'line-opacity': 0.85 },
  })
  // Clicked region: bold outline above the fills, below the markers.
  map.addLayer({
    id: 'selected-region-line',
    type: 'line',
    source: 'selected-region',
    layout: { 'line-join': 'round' },
    paint: { 'line-color': p.selectedRegion, 'line-width': 3.5, 'line-opacity': 1 },
  })

  // Last known area for region-level reports (never a dot): outline only, so it is never mistaken for an alert.
  map.addLayer({
    id: 'track-areas',
    type: 'fill',
    source: 'track-areas',
    paint: { 'fill-color': ['get', 'color'], 'fill-opacity': ['*', 0.06, ['get', 'opacity']] },
  })
  map.addLayer({
    id: 'track-areas-line',
    type: 'line',
    source: 'track-areas',
    layout: { 'line-join': 'round' },
    paint: { 'line-color': ['get', 'color'], 'line-opacity': ['*', 0.95, ['get', 'opacity']], 'line-width': 2.5, 'line-dasharray': [2, 1.5] },
  })

  // Forecast cone: hatched, so it reads as "may be here", never as an alert or an observed area (spec §13, §15).
  map.addLayer({
    id: 'track-cone',
    type: 'fill',
    source: 'track-forecasts',
    filter: ['==', ['geometry-type'], 'Polygon'],
    paint: { 'fill-pattern': ['case', ['get', 'selected'], ['concat', 'hatch-sel-', ['get', 'mode']], 'hatch-muted'], 'fill-opacity': ['case', ['get', 'selected'], 0.55, 0.22] },
  })
  map.addLayer({
    id: 'track-cone-line',
    type: 'line',
    source: 'track-forecasts',
    filter: ['==', ['geometry-type'], 'Polygon'],
    layout: { 'line-join': 'round' },
    paint: { 'line-color': ['get', 'vector'], 'line-opacity': ['case', ['get', 'selected'], 0.9, 0.4], 'line-width': ['case', ['get', 'selected'], 1, 0.6], 'line-dasharray': [1, 2] },
  })

  // Family of the selected target: its probable earlier reports (two generations) and where else those could have
  // flown, in the target's selection colour. A leg is as thick as the link's own probability and as opaque as the
  // path's; its label = the link's probability. Every leg ends on a node drawn as the target it is (class glyph, turned
  // by its course), as big and opaque as the best path through it; the node label = that path.
  // The clicked leg: a light solid band under it, so it stands out on any fill.
  map.addLayer({
    id: 'track-pred-link-halo',
    type: 'line',
    source: 'track-predecessors',
    filter: ['all', ['==', ['geometry-type'], 'LineString'], ['==', ['get', 'selectedLink'], true]],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': p.label, 'line-width': ['+', 6, ['*', 6, ['get', 'probability']]], 'line-opacity': 0.95 },
  })
  map.addLayer({
    id: 'track-pred-links',
    type: 'line',
    source: 'track-predecessors',
    filter: ['==', ['geometry-type'], 'LineString'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': ['get', 'vector'], 'line-opacity': ['get', 'opacity'], 'line-width': ['+', 0.5, ['*', 6, ['get', 'probability']]], 'line-dasharray': [0.1, 1.8] },
  })
  map.addLayer({
    id: 'track-pred-hit',
    type: 'line',
    source: 'track-predecessors',
    filter: ['==', ['geometry-type'], 'LineString'],
    paint: { 'line-color': p.vectorMuted, 'line-opacity': 0, 'line-width': 14 },
  })
  map.addLayer({
    id: 'track-pred-link-labels',
    type: 'symbol',
    source: 'track-predecessors',
    filter: ['==', ['geometry-type'], 'LineString'],
    layout: { 'symbol-placement': 'line-center', 'text-field': ['get', 'label'], 'text-font': TEXT_FONT, 'text-size': 9, 'text-offset': [0, -0.8], 'text-allow-overlap': true },
    paint: { 'text-color': p.label, 'text-halo-color': p.labelHalo, 'text-halo-width': 1, 'text-opacity': ['max', 0.45, ['get', 'opacity']] },
  })
  map.addLayer({
    id: 'track-pred-nodes',
    type: 'symbol',
    source: 'track-predecessors',
    filter: ['==', ['geometry-type'], 'Point'],
    layout: {
      'icon-image': ['concat', 'sel-', ['case', ['get', 'hasDirection'], 'arrow-', 'dot-'], ['get', 'mode']],
      'icon-size': ['*', iconScale, ['get', 'size']],
      'icon-rotate': ['get', 'rotation'],
      'icon-rotation-alignment': 'map',
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,
      'text-field': ['get', 'label'],
      'text-font': TEXT_FONT,
      'text-size': 10,
      'text-offset': [0, 0.9],
      'text-anchor': 'top',
      'text-optional': true,
    },
    paint: { 'icon-opacity': ['get', 'opacity'], 'text-color': p.label, 'text-halo-color': p.labelHalo, 'text-halo-width': 1.1, 'text-opacity': ['get', 'opacity'] },
  })

  // Crumbs of the selected target (when it has no predecessor fork): earlier reported positions as small faded glyphs
  // with "place time" labels, joined to the marker by a dotted line. Not a trajectory — a list of where the target
  // was said to be, and when.
  map.addLayer({
    id: 'track-fix-links',
    type: 'line',
    source: 'track-fixes',
    filter: ['==', ['geometry-type'], 'LineString'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      'line-color': ['get', 'vector'],
      'line-opacity': ['get', 'opacity'],
      'line-width': ['+', 0.6, ['*', 3.4, ['get', 'probability']]],
      'line-dasharray': [0.1, 2],
    },
  })
  map.addLayer({
    id: 'track-fix-hit',
    type: 'line',
    source: 'track-fixes',
    filter: ['==', ['geometry-type'], 'LineString'],
    paint: { 'line-color': p.vectorMuted, 'line-opacity': 0, 'line-width': 14 },
  })
  map.addLayer({
    id: 'track-fixes',
    type: 'symbol',
    source: 'track-fixes',
    filter: ['==', ['geometry-type'], 'Point'],
    layout: {
      'icon-image': ['concat', 'sel-dot-', ['get', 'mode']],
      'icon-size': ['*', iconScale, ['get', 'size']],
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,
      'text-field': ['get', 'label'],
      'text-font': TEXT_FONT,
      'text-size': 10,
      'text-offset': [0, 0.9],
      'text-anchor': 'top',
      'text-optional': true,
    },
    paint: { 'icon-opacity': ['get', 'opacity'], 'text-color': p.label, 'text-halo-color': p.labelHalo, 'text-halo-width': 1.1, 'text-opacity': ['get', 'opacity'] },
  })
  // Forecast centreline: a dashed line in the vector colour alone (colour, gap, colour, gap) — no halo.
  map.addLayer({
    id: 'track-forecasts',
    type: 'line',
    source: 'track-forecasts',
    filter: ['==', ['geometry-type'], 'LineString'],
    paint: { 'line-color': ['get', 'vector'], 'line-opacity': ['case', ['get', 'selected'], 1, ['max', 0.7, ['get', 'opacity']]], 'line-width': ['case', ['get', 'selected'], 2.4, 1], 'line-dasharray': [2, 2.2] },
  })
  map.addLayer({
    id: 'track-forecast-hit',
    type: 'line',
    source: 'track-forecasts',
    filter: ['==', ['geometry-type'], 'LineString'],
    paint: { 'line-color': p.vectorMuted, 'line-opacity': 0, 'line-width': 14 },
  })
  map.addLayer({
    id: 'track-forecast-heads',
    type: 'symbol',
    source: 'track-forecasts',
    filter: ['==', ['geometry-type'], 'Point'],
    layout: {
      'icon-image': ['case', ['get', 'selected'], ['concat', 'head-sel-', ['get', 'mode']], 'head-muted'],
      'icon-size': ['case', ['get', 'selected'], 0.34 * iconScale, 0.26 * iconScale],
      'icon-rotate': ['get', 'rotation'],
      'icon-rotation-alignment': 'map',
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,
    },
    paint: { 'icon-opacity': ['max', 0.85, ['get', 'opacity']] },
  })

  map.addLayer({
    id: 'track-clusters',
    type: 'circle',
    source: 'track-points',
    filter: ['has', 'point_count'],
    paint: { 'circle-color': p.cluster, 'circle-opacity': 0.85, 'circle-radius': ['step', ['get', 'point_count'], 14, 5, 18, 20, 24], 'circle-stroke-width': 2, 'circle-stroke-color': p.glyphHalo },
  })
  map.addLayer({
    id: 'track-cluster-count',
    type: 'symbol',
    source: 'track-points',
    filter: ['has', 'point_count'],
    layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-size': 12, 'text-font': TEXT_FONT },
    paint: { 'text-color': p.clusterText },
  })

  // Target ring: red for "near the viewer's point", orange for "heading this way". Only with the highlight on.
  map.addLayer({
    id: 'track-target-ring',
    type: 'circle',
    source: 'track-points',
    filter: ['all', ['!', ['has', 'point_count']], ['!=', ['get', 'hazard'], '']],
    paint: {
      'circle-color': ['match', ['get', 'hazard'], 'near', p.hazardNear, p.hazardTowards],
      'circle-opacity': 0.18,
      'circle-radius': 23 * iconScale,
      'circle-stroke-color': ['match', ['get', 'hazard'], 'near', p.hazardNear, p.hazardTowards],
      'circle-stroke-width': 3,
      'circle-stroke-opacity': 0.95,
    },
  })
  // Position marker with direction arrow (rotated) or a plain circle when direction is unknown. Selection is said by
  // the glyph colour alone (sel-* images): no rings or discs under the marker.
  map.addLayer({
    id: 'track-points',
    type: 'symbol',
    source: 'track-points',
    filter: ['!', ['has', 'point_count']],
    layout: {
      'icon-image': ['concat', ['case', ['get', 'selected'], 'sel-', ''], ['case', ['get', 'hasDirection'], 'arrow-', 'dot-'], ['get', 'mode']],
      'icon-size': ['case', ['get', 'selected'], 0.8 * iconScale, 0.62 * iconScale],
      'icon-rotate': ['get', 'rotation'],
      'icon-rotation-alignment': 'map',
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,
    },
    // An approach-zone anchor is drawn paler: it is where the object is going, not a fix.
    paint: { 'icon-opacity': ['*', ['case', ['get', 'approx'], 0.6, 1], ['get', 'opacity']] },
  })
  // The target under the cursor: its own glyph, larger and with a wider halo, over the plain one (no ring, no disc).
  // The filter is switched by trackHover; -1 matches nothing.
  map.addLayer({
    id: 'track-hover',
    type: 'symbol',
    source: 'track-points',
    filter: ['==', ['get', 'id'], -1],
    layout: {
      'icon-image': ['concat', ['case', ['get', 'selected'], 'sel-', 'hov-'], ['case', ['get', 'hasDirection'], 'arrow-', 'dot-'], ['get', 'mode']],
      'icon-size': 0.9 * iconScale,
      'icon-rotate': ['get', 'rotation'],
      'icon-rotation-alignment': 'map',
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,
    },
    paint: { 'icon-opacity': ['max', 0.85, ['get', 'opacity']] },
  })
  map.addLayer({
    id: 'track-labels',
    type: 'symbol',
    source: 'track-points',
    filter: ['!', ['has', 'point_count']],
    minzoom: opts.labelMinZoom ?? 6,
    layout: { 'text-field': ['get', 'label'], 'text-size': 11, 'text-font': TEXT_FONT, 'text-offset': [0, 1.6], 'text-anchor': 'top', 'text-optional': true },
    paint: { 'text-color': p.label, 'text-halo-color': p.labelHalo, 'text-halo-width': 1.2, 'text-opacity': ['get', 'opacity'] },
  })

  // Badges: sources reporting the target (top-right, blue) and objects in the group (top-left, white). Pure icons.
  const badge = (id: string, prefix: string, prop: string, offset: [number, number], min: number) =>
    map.addLayer({
      id,
      type: 'symbol',
      source: 'track-points',
      filter: ['all', ['!', ['has', 'point_count']], ['>=', ['get', prop], min]],
      layout: {
        'icon-image': ['concat', prefix, ['to-string', ['min', ['get', prop], BADGE_MAX]]],
        'icon-size': 0.95 * iconScale,
        'icon-offset': offset,
        'icon-allow-overlap': true,
        'icon-ignore-placement': true,
      },
      paint: { 'icon-opacity': ['max', 0.7, ['get', 'opacity']] },
    })
  badge('track-badge-sources', 'badge-src-', 'sources', [22, -20], 1)
  badge('track-badge-count', 'badge-cnt-', 'count', [-22, -20], 2)

  map.addLayer({
    id: 'home',
    type: 'circle',
    source: 'home',
    paint: { 'circle-color': p.home, 'circle-radius': 7, 'circle-stroke-color': p.glyphHalo, 'circle-stroke-width': 2.5 },
  })
}

/** What a click on a track landed on: the track, and the family leg when it hit one. */
export interface TrackHit {
  trackId: MapId
  link?: { fromTargetId: MapId; toTargetId: MapId; probability: number; pathProbability: number; kind: string }
}

/**
 * The track under a click or the cursor: its marker, badge, crumb, forecast or a family leg (top-most first, so a
 * marker over a leg wins). Nothing exactly under the point: the nearest target within HIT_RADIUS, so a marker needs
 * no pixel-perfect aim. The dashed last-known area is not a target: a click on empty ground inside an oblast must
 * select the oblast, even when a track is drawn as that oblast.
 */
export function hitAt(map: maplibregl.Map, point: maplibregl.Point, radius = HIT_RADIUS): TrackHit | null {
  const layers = TRACK_HIT_LAYERS.filter((l) => map.getLayer(l))
  if (layers.length === 0) return null
  const exact = map.queryRenderedFeatures(point, { layers })[0]
  const f =
    exact ??
    nearestHit(
      map.queryRenderedFeatures(
        [
          [point.x - radius, point.y - radius],
          [point.x + radius, point.y + radius],
        ],
        { layers },
      ),
      point,
      (lngLat) => map.project(lngLat as [number, number]),
    )
  const props = f?.properties
  if (!f || !props || props.id === undefined) return null
  const trackId = String(props.id)
  if (f.layer.id === 'track-pred-hit' && props.from !== undefined && props.to !== undefined) {
    return { trackId, link: { fromTargetId: String(props.from), toTargetId: String(props.to), probability: Number(props.linkProbability), pathProbability: Number(props.pathProbability), kind: String(props.linkKind ?? '') } }
  }
  return { trackId }
}

/** Something with a geometry and a layer: what queryRenderedFeatures returns, reduced to what nearestHit reads. */
export interface HitCandidate {
  geometry: Geometry
  layer: { id: string }
  properties: Record<string, unknown> | null
}

/**
 * Among the features found in a box around the cursor: the point feature (marker, badge, crumb, node, chevron)
 * closest to the cursor on screen; a line (tail, forecast, leg) only when no point is there. `project` maps
 * lon/lat to screen pixels.
 */
export function nearestHit<T extends HitCandidate>(features: T[], point: { x: number; y: number }, project: (lngLat: Position) => { x: number; y: number }): T | undefined {
  let best: T | undefined
  let bestD = Infinity
  for (const f of features) {
    if (f.geometry.type !== 'Point') continue
    const p = project(f.geometry.coordinates)
    const d = Math.hypot(p.x - point.x, p.y - point.y)
    if (d < bestD) {
      best = f
      bestD = d
    }
  }
  return best ?? features.find((f) => f.geometry.type !== 'Point')
}

export interface TrackHoverOptions {
  /** Region layers that keep the pointer cursor when the cursor leaves a target while still over one of them. */
  fallbackLayers: string[]
  /** False while the map is in another mode (picking a home point): no hover, cursor left alone. */
  enabled?: () => boolean
}

/**
 * Target-under-cursor: switches the `track-hover` layer to the hit track and shows the pointer cursor while one is
 * under (or within HIT_RADIUS of) the cursor. Returns the teardown and a `current()` reader for other hover handlers.
 */
export function trackHover(map: maplibregl.Map, opts: TrackHoverOptions): { stop: () => void; current: () => MapId | null } {
  let current: MapId | null = null
  const apply = (id: MapId | null) => {
    if (id === current) return
    current = id
    if (map.getLayer('track-hover')) map.setFilter('track-hover', ['==', ['get', 'id'], id ?? -1])
  }
  const clear = () => {
    apply(null)
    if (opts.enabled?.() !== false) map.getCanvas().style.cursor = ''
  }
  const move = (e: MapLayerMouseEvent) => {
    if (opts.enabled?.() === false) return apply(null)
    const hit = hitAt(map, e.point)
    apply(hit?.trackId ?? null)
    // Set on every move, not only on change: a region layer's mouseleave may have reset it under a marker on the edge.
    const layers = opts.fallbackLayers.filter((l) => map.getLayer(l))
    map.getCanvas().style.cursor = hit || (layers.length > 0 && map.queryRenderedFeatures(e.point, { layers }).length > 0) ? 'pointer' : ''
  }
  map.on('mousemove', move)
  map.on('mouseout', clear)
  map.on('dragstart', clear)
  return {
    stop: () => {
      map.off('mousemove', move)
      map.off('mouseout', clear)
      map.off('dragstart', clear)
    },
    current: () => current,
  }
}

export function pointerCursor(map: maplibregl.Map, layers: string[]) {
  for (const layer of layers) {
    map.on('mouseenter', layer, () => (map.getCanvas().style.cursor = 'pointer'))
    map.on('mouseleave', layer, () => (map.getCanvas().style.cursor = ''))
  }
}

/** A polygon region the cursor can rest on: what to outline and what to say in the tooltip. */
export interface HoverRegion {
  id: number
  geometry: Geometry
  label: string
}

/**
 * Region-under-cursor: outlines the polygon through the `hover-region` source and moves a tooltip next to the pointer.
 * `resolve` maps the rendered features under the cursor (queried on `hitLayers`, top-most first) to a region, or null.
 * Returns the teardown.
 */
export function regionHover(map: maplibregl.Map, tip: HTMLElement, hitLayers: string[], resolve: (hits: maplibregl.MapGeoJSONFeature[]) => HoverRegion | null): () => void {
  let current: number | null = null
  const clear = () => {
    tip.hidden = true
    if (current === null) return
    current = null
    if (map.getSource('hover-region')) setData(map, 'hover-region', { type: 'FeatureCollection', features: [] })
  }
  const move = (e: MapLayerMouseEvent) => {
    // Layers are missing while a new style loads; the tooltip must not linger with a stale name then.
    if (!map.getSource('hover-region') || hitLayers.some((l) => !map.getLayer(l))) return clear()
    const region = resolve(map.queryRenderedFeatures(e.point, { layers: hitLayers }))
    if (!region) return clear()
    if (region.id !== current) {
      current = region.id
      tip.textContent = region.label
      setData(map, 'hover-region', { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: region.geometry, properties: {} }] })
    }
    // Next to the pointer, flipped to the other side near the right / bottom edge of the map.
    const box = map.getContainer()
    const gap = 14
    tip.hidden = false
    const w = tip.offsetWidth
    const h = tip.offsetHeight
    tip.style.left = `${e.point.x + gap + w > box.clientWidth - 8 ? e.point.x - gap - w : e.point.x + gap}px`
    tip.style.top = `${e.point.y + gap + h > box.clientHeight - 8 ? e.point.y - gap - h : e.point.y + gap}px`
  }
  map.on('mousemove', move)
  map.on('mouseout', clear)
  map.on('dragstart', clear)
  return () => {
    map.off('mousemove', move)
    map.off('mouseout', clear)
    map.off('dragstart', clear)
  }
}
