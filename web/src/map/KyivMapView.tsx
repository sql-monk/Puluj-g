import * as maplibregl from 'maplibre-gl'
import type { MapLayerMouseEvent } from 'maplibre-gl'
import type { Feature, FeatureCollection, Geometry, Position } from 'geojson'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { RegionDto } from '../api/types'
import type { MapPalette } from './palette'
import LinkPopup from '../components/LinkPopup'
import RegionPopup from '../components/RegionPopup'
import TrackPopup from '../components/TrackPopup'
import IncidentLegend from '../components/IncidentLegend'
import IncidentPopup from '../components/IncidentPopup'
import { incidentHitAt, INCIDENT_HIT_LAYERS } from './incidentLayer'
import { useIncidentLayer } from './useIncidentLayer'
import { effectiveNow, useStore, type Theme } from '../store/useStore'
import { getPalette } from './palette'
import { replay } from '../replay/engine'
import { buildAlertLayer, buildReplayLayers, buildTrackLayers, emptyCollection, isPolygonAlert, visibleTracks } from './geojson'
import { ATTRIBUTION, STYLE_DARK, STYLE_LIGHT, TEXT_FONT, addIcons, addTrackLayers, addTrackSources, alertPaint, pointerCursor, regionHover, hitAt, setData, setTrackData, trackHover } from './layers'

/** The city itself; the view opens on it with a margin of surroundings. */
const KYIV_BOUNDS: [[number, number], [number, number]] = [
  [30.23, 50.21],
  [30.83, 50.6],
]
/** How far the page lets you pan: ~80 km west/east and ~50 km north/south of the city, enough to see vectors coming in. */
const PAGE_BOUNDS: [[number, number], [number, number]] = [
  [29.3, 49.7],
  [31.8, 51.1],
]
/** Tracks are shown when their position or their forecast end falls inside this box (a bit wider than the page). */
const DATA_BOUNDS: [[number, number], [number, number]] = [
  [29.0, 49.5],
  [32.1, 51.3],
]

interface Props {
  dark: boolean
  theme: Theme
  onDetails: (trackId: import('../api/types').MapId) => void
}

/** Kyiv page: the city with its ten districts and a ring of surroundings, its own MapLibre instance and layer set. */
export default function KyivMapView({ dark, theme, onDetails }: Props) {
  const container = useRef<HTMLDivElement>(null)
  const mapRef = useRef<maplibregl.Map | null>(null)
  const [mapInstance, setMapInstance] = useState<maplibregl.Map | null>(null)
  // Where the viewer clicked to select the current track: the popup opens there, not at the marker.
  const [clickAt, setClickAt] = useState<[number, number] | null>(null)
  // Name of the city district under the cursor, moved by the hover handler directly (no render per mouse move).
  const tip = useRef<HTMLDivElement>(null)
  const styleLoaded = useRef(false)
  const palette = getPalette(theme)
  const paletteRef = useRef(palette)
  paletteRef.current = palette
  // Where the viewer clicked to select a region: the region window opens there.
  const [regionClickAt, setRegionClickAt] = useState<[number, number] | null>(null)

  const tracks = useStore((s) => s.tracks)
  const alerts = useStore((s) => s.alerts)
  const regions = useStore((s) => s.regions)
  const filters = useStore((s) => s.filters)
  const home = useStore((s) => s.home)
  const mode = useStore((s) => s.mode)
  const at = useStore((s) => s.at)
  const now = useStore((s) => s.now)
  const select = useStore((s) => s.select)
  const selectedTrackId = useStore((s) => s.selectedTrackId)
  const selectedLink = useStore((s) => s.selectedLink)
  const selectLink = useStore((s) => s.selectLink)
  // Where the viewer clicked a family leg: the link window opens there.
  const [linkClickAt, setLinkClickAt] = useState<[number, number] | null>(null)
  const selectedTrack = useStore((s) => (s.selectedTrackId ? s.tracks[s.selectedTrackId] : undefined))
  const predecessors = useStore((s) => s.predecessors)
  const loadPredecessors = useStore((s) => s.loadPredecessors)
  const placeGeometries = useStore((s) => s.placeGeometries)
  const ensurePlaceGeometry = useStore((s) => s.ensurePlaceGeometry)
  const selectedRegionId = useStore((s) => s.selectedRegionId)
  const selectRegion = useStore((s) => s.selectRegion)
  const clock = effectiveNow({ mode, at, now })

  const regionsById = useMemo(() => new Map<number, RegionDto>(regions.map((r) => [r.id, r])), [regions])
  const regionsRef = useRef(regionsById)
  regionsRef.current = regionsById
  const kyiv = useMemo(() => regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ'), [regions])
  const districts = useMemo(() => regions.filter((r) => r.level === 'District' && r.parentId === kyiv?.id), [regions, kyiv])
  // The alert fill is rebuilt only when alerts change (the apply effect runs on every clock tick).
  const alertList = useMemo(() => (filters.alerts ? Object.values(alerts) : []), [alerts, filters.alerts])
  const alertLayer = useMemo(() => buildAlertLayer(alertList, regionsById, placeGeometries), [alertList, regionsById, placeGeometries])
  // P11: the same incident layer as the country map (parity by construction); Kyiv's district polygons come from the regions payload.
  const incidents = useIncidentLayer({ map: mapInstance, palette, clock, regionsById, placeGeometries, ensurePlaceGeometry })
  const incidentSelect = useRef<(id: number | null, at?: [number, number]) => void>(() => {})
  incidentSelect.current = (id, at) => {
    if (id === null) incidents.close()
    else {
      incidents.select(id)
      if (at) incidents.setClickAt(at)
    }
  }

  useEffect(() => {
    if (!container.current || mapRef.current) return
    const map = new maplibregl.Map({
      container: container.current,
      style: dark ? STYLE_DARK : STYLE_LIGHT,
      bounds: KYIV_BOUNDS,
      fitBoundsOptions: { padding: { top: 70, bottom: 40, left: 300, right: 400 } },
      maxBounds: PAGE_BOUNDS,
      minZoom: 8,
      attributionControl: { compact: true, customAttribution: ATTRIBUTION },
    })
    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right')
    map.addControl(new maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-left')
    map.on('style.load', () => {
      addIcons(map, paletteRef.current)
      addLayers(map, paletteRef.current)
      styleLoaded.current = true
    })
    map.on('click', (e: MapLayerMouseEvent) => {
      const hit = hitAt(map, e.point)
      // A leg of the selected target's family opens the link window; the selection itself stays.
      if (hit?.link) {
        selectLink({ trackId: hit.trackId, ...hit.link })
        setLinkClickAt([e.lngLat.lng, e.lngLat.lat])
        return
      }
      const trackId = hit?.trackId ?? null
      select(trackId)
      setClickAt(trackId === null ? null : [e.lngLat.lng, e.lngLat.lat])
      setRegionClickAt(trackId === null ? [e.lngLat.lng, e.lngLat.lat] : null)
      incidentSelect.current(null)
      if (trackId !== null) return
      const incidentId = incidentHitAt(map, e.point)
      if (incidentId !== null) {
        selectRegion(null)
        setRegionClickAt(null)
        incidentSelect.current(incidentId, [e.lngLat.lng, e.lngLat.lat])
        return
      }
      // District under the cursor (also through an alert fill), else the city, else nothing.
      const district = map.queryRenderedFeatures(e.point, { layers: ['districts-hit'] })[0]?.properties?.id
      if (district !== undefined) {
        selectRegion(Number(district))
        return
      }
      const alert = map.queryRenderedFeatures(e.point, { layers: ['alerts-fill'] })[0]?.properties?.placeId
      selectRegion(alert === undefined ? null : Number(alert))
    })
    pointerCursor(map, ['districts-hit', 'alerts-fill', ...INCIDENT_HIT_LAYERS])
    // Target under the cursor (within the hit radius): enlarged glyph, pointer cursor; registered before the district hover.
    const hover = trackHover(map, { fallbackLayers: ['districts-hit', 'alerts-fill'] })
    // Hover: the city district under the cursor (the hit fill covers the districts even under an alert fill); no tip
    // while a target is hovered.
    const stopHover = regionHover(map, tip.current!, ['districts-hit'], (hits) => {
      if (hover.current() !== null) return null
      const id = Number(hits[0]?.properties?.id)
      const district = regionsRef.current.get(id)
      return district ? { id, geometry: district.geometry, label: district.name } : null
    })
    map.on('error', (e) => console.error('[kyiv-map]', e.error?.message ?? e))
    mapRef.current = map
    setMapInstance(map)
    return () => {
      hover.stop()
      stopHover()
      map.remove()
      mapRef.current = null
      setMapInstance(null)
      styleLoaded.current = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Theme switch: reload the base style so icons and layers are re-added in the new palette. Skipped on mount.
  const appliedTheme = useRef(theme)
  useEffect(() => {
    const map = mapRef.current
    if (!map || appliedTheme.current === theme) return
    appliedTheme.current = theme
    styleLoaded.current = false
    map.setStyle(dark ? STYLE_DARK : STYLE_LIGHT)
  }, [theme, dark])

  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    const apply = () => {
      if (!map.getSource('track-points')) return
      // Replay: the markers at their reconstructed positions for the replay clock; live: the snapshot's tracks.
      const layers =
        mode === 'history'
          ? buildReplayLayers(replay.positions(replay.t || clock.getTime(), filters), palette, selectedTrackId)
          : buildTrackLayers(visibleTracks(tracks, filters, clock), clock, regionsById, filters, { home, selectedId: selectedTrackId, palette, predecessors, selectedLink })
      // Keep only tracks that touch the page: their marker or the end of their forecast lies inside the data box.
      const near = new Set<string>()
      for (const f of layers.points.features) if (inBox(f.geometry.coordinates)) near.add(String(f.id))
      for (const f of layers.forecasts.features) if (f.geometry.type === 'Point' && inBox(f.geometry.coordinates)) near.add(String(f.id))
      const only = <G extends Geometry, P>(fc: FeatureCollection<G, P>): FeatureCollection<G, P> => ({ type: 'FeatureCollection', features: fc.features.filter((f) => near.has(String((f.properties as { id: unknown }).id))) })
      setTrackData(map, { points: only(layers.points), fixes: only(layers.fixes), forecasts: only(layers.forecasts), areas: only(layers.areas), predecessors: only(layers.predecessors) })

      for (const a of alertList) if (!regionsById.has(a.placeId) && !placeGeometries[a.placeId]) ensurePlaceGeometry(a.placeId)
      const alerted = new Set(alertList.filter((a) => isPolygonAlert(a, regionsById, placeGeometries)).map((a) => a.placeId))
      setData(map, 'alerts', alertLayer)
      setData(map, 'kyiv', kyiv ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: kyiv.geometry, properties: {} }] } : emptyCollection())
      const cityAlerted = kyiv ? alerted.has(kyiv.id) : false
      setData(map, 'districts', {
        type: 'FeatureCollection',
        features: districts.map(
          (r): Feature<Geometry, { id: number; name: string; alerted: boolean }> => ({
            type: 'Feature',
            id: r.id,
            geometry: r.geometry,
            properties: { id: r.id, name: r.name.replace(' район', ''), alerted: cityAlerted || alerted.has(r.id) },
          }),
        ),
      })
      const selected = regionsById.get(selectedRegionId ?? -1)
      setData(map, 'selected-region', selected ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: selected.geometry, properties: {} }] } : emptyCollection())
      setData(map, 'home', home ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [home.lon, home.lat] }, properties: {} }] } : emptyCollection())
    }
    if (styleLoaded.current) apply()
    else map.once('style.load', apply)
  }, [mode, tracks, alertList, alertLayer, regions, regionsById, kyiv, districts, filters, home, clock, selectedRegionId, selectedTrackId, selectedLink, palette, predecessors, placeGeometries, ensurePlaceGeometry])

  // Replay: every frame of the replay clock moves the markers, straight into the source, without a render.
  useEffect(() => {
    if (mode !== 'history') return
    return replay.subscribe((t) => {
      const map = mapRef.current
      if (!map || !styleLoaded.current || !map.getSource('track-points')) return
      const points = buildReplayLayers(replay.positions(t, filters), palette, selectedTrackId).points
      setData(map, 'track-points', { type: 'FeatureCollection', features: points.features.filter((f) => inBox(f.geometry.coordinates)) })
    })
  }, [mode, filters, palette, selectedTrackId])

  const selectedSeenAt = selectedTrack?.lastSeenAt
  useEffect(() => {
    loadPredecessors(selectedTrackId)
  }, [selectedTrackId, selectedSeenAt, loadPredecessors])

  return (
    <div className="absolute inset-0">
      <div ref={container} className="h-full w-full" />
      <div ref={tip} hidden className="pointer-events-none absolute z-10 whitespace-nowrap rounded bg-white/95 px-2 py-1 text-xs shadow dark:bg-slate-900/95 dark:text-slate-100" />
      {mapInstance && selectedTrack && !selectedLink && <TrackPopup map={mapInstance} track={selectedTrack} anchor={clickAt} onDetails={() => onDetails(selectedTrack.id)} onClose={() => select(null)} />}
      {mapInstance && selectedLink && linkClickAt && <LinkPopup map={mapInstance} link={selectedLink} anchor={linkClickAt} onClose={() => selectLink(null)} />}
      {mapInstance && !selectedTrack && selectedRegionId !== null && regionClickAt && <RegionPopup map={mapInstance} placeId={selectedRegionId} anchor={regionClickAt} onClose={() => selectRegion(null)} />}
      {mapInstance && incidents.selected && incidents.clickAt && <IncidentPopup map={mapInstance} incident={incidents.selected} anchor={incidents.clickAt} onClose={incidents.close} />}
      {filters.events && <IncidentLegend onOpen={(i) => incidents.open(i)} />}
    </div>
  )
}

function inBox([lon, lat]: Position): boolean {
  return lon >= DATA_BOUNDS[0][0] && lon <= DATA_BOUNDS[1][0] && lat >= DATA_BOUNDS[0][1] && lat <= DATA_BOUNDS[1][1]
}

function addLayers(map: maplibregl.Map, p: MapPalette) {
  const empty = emptyCollection()
  map.addSource('kyiv', { type: 'geojson', data: empty })
  map.addSource('districts', { type: 'geojson', data: empty })
  map.addSource('alerts', { type: 'geojson', data: empty })
  addTrackSources(map, false)

  const firstSymbol = map.getStyle().layers.find((l) => l.type === 'symbol')?.id
  const alert = alertPaint(p)
  // Districts: own land colour, alternating slightly so neighbours are told apart; an alert replaces the fill.
  map.addLayer(
    {
      id: 'districts-fill',
      type: 'fill',
      source: 'districts',
      filter: ['!', ['get', 'alerted']],
      paint: { 'fill-color': ['case', ['==', ['%', ['get', 'id'], 2], 0], p.landAlt, p.land], 'fill-opacity': 0.75 },
    },
    firstSymbol,
  )
  map.addLayer({ id: 'alerts-fill', type: 'fill', source: 'alerts', paint: { 'fill-color': alert.fill, 'fill-opacity': 0.7 } }, firstSymbol)
  map.addLayer({ id: 'alerts-line', type: 'line', source: 'alerts', paint: { 'line-color': alert.line, 'line-opacity': 0.9, 'line-width': 1.6 } }, firstSymbol)
  map.addLayer(
    { id: 'districts-line', type: 'line', source: 'districts', layout: { 'line-join': 'round' }, paint: { 'line-color': p.oblastLine, 'line-width': 1.8, 'line-opacity': 0.95 } },
    firstSymbol,
  )
  map.addLayer({ id: 'kyiv-halo', type: 'line', source: 'kyiv', paint: { 'line-color': p.borderHalo, 'line-width': 8, 'line-opacity': 0.9 } }, firstSymbol)
  map.addLayer(
    { id: 'kyiv-line', type: 'line', source: 'kyiv', layout: { 'line-join': 'round' }, paint: { 'line-color': p.border, 'line-width': 3.5, 'line-opacity': 1 } },
    firstSymbol,
  )
  // Invisible full-coverage fill so clicks resolve to a district even where an alert fill sits on top.
  map.addLayer({ id: 'districts-hit', type: 'fill', source: 'districts', paint: { 'fill-color': '#000', 'fill-opacity': 0 } })
  map.addLayer({
    id: 'districts-label',
    type: 'symbol',
    source: 'districts',
    layout: { 'text-field': ['get', 'name'], 'text-size': 13, 'text-font': TEXT_FONT, 'text-letter-spacing': 0.04, 'text-allow-overlap': false, 'text-padding': 4 },
    paint: { 'text-color': p.border, 'text-halo-color': p.borderHalo, 'text-halo-width': 1.8 },
  })

  addTrackLayers(map, p, { labelMinZoom: 0, iconScale: 1.15 })
}
