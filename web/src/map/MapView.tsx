import * as maplibregl from 'maplibre-gl'
import type { MapLayerMouseEvent } from 'maplibre-gl'
// maplibre-gl v6 resolves its worker with a dynamic `new URL(...)` that bundlers cannot follow; Vite bundles the
// worker entry explicitly here and MapLibre is pointed at it.
import maplibreWorkerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { RegionDto } from '../api/types'
import type { MapPalette } from './palette'
import LinkPopup from '../components/LinkPopup'
import EventPopup from '../components/EventPopup'
import IncidentLegend from '../components/IncidentLegend'
import IncidentPopup from '../components/IncidentPopup'
import { useIncidentStore } from '../store/useIncidentStore'
import { incidentHitAt, withoutIncidentEvents, INCIDENT_HIT_LAYERS } from './incidentLayer'
import { useIncidentLayer } from './useIncidentLayer'
import RegionPopup from '../components/RegionPopup'
import TrackPopup from '../components/TrackPopup'
import { effectiveNow, useStore, type Theme } from '../store/useStore'
import { getPalette } from './palette'
import { replay } from '../replay/engine'
import { buildAlertLayer, buildEventLayer, buildReplayLayers, buildTrackLayers, emptyCollection, visibleTracks } from './geojson'
import { ATTRIBUTION, STYLE_DARK, STYLE_LIGHT, addEventLayers, addIcons, addTrackLayers, addTrackSources, alertPaint, pointerCursor, regionHover, hitAt, setData, setTrackData, trackHover } from './layers'

maplibregl.setWorkerUrl(maplibreWorkerUrl)

const UKRAINE_CENTER: [number, number] = [31.2, 48.8]

interface Props {
  dark: boolean
  theme: Theme
  onPickHome: ((lon: number, lat: number) => void) | null
  onDetails: (trackId: number) => void
}

/** Country-wide MapLibre map with all Puluj layers. Data flows one way: store -> GeoJSON sources. */
export default function MapView({ dark, theme, onPickHome, onDetails }: Props) {
  const container = useRef<HTMLDivElement>(null)
  const mapRef = useRef<maplibregl.Map | null>(null)
  const [mapInstance, setMapInstance] = useState<maplibregl.Map | null>(null)
  // Where the viewer clicked to select the current track: the popup opens there, not at the marker.
  const [clickAt, setClickAt] = useState<[number, number] | null>(null)
  const [eventClickAt, setEventClickAt] = useState<[number, number] | null>(null)
  const [selectedEventId, setSelectedEventId] = useState<number | null>(null)
  // Name of the raion / oblast under the cursor, moved by the hover handler directly (no render per mouse move).
  const tip = useRef<HTMLDivElement>(null)
  const styleLoaded = useRef(false)
  const pickRef = useRef(onPickHome)
  pickRef.current = onPickHome
  const palette = getPalette(theme)
  const paletteRef = useRef(palette)
  paletteRef.current = palette
  // Where the viewer clicked to select a region: the region window opens there.
  const [regionClickAt, setRegionClickAt] = useState<[number, number] | null>(null)

  const tracks = useStore((s) => s.tracks)
  const alerts = useStore((s) => s.alerts)
  const events = useStore((s) => s.events)
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
  const selectedEvent = useStore((s) => (s.events[selectedEventId ?? -1]))
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
  // The alert fill (one feature per alerted place, nested polygons cut out) is rebuilt only when alerts change:
  // the apply effect below runs on every clock tick and track update.
  const alertList = useMemo(() => (filters.alerts ? Object.values(alerts) : []), [alerts, filters.alerts])
  const alertLayer = useMemo(() => buildAlertLayer(alertList, regionsById, placeGeometries), [alertList, regionsById, placeGeometries])
  // P11: incidents (the catalog-driven layer); the legacy event markers of incident kinds are hidden while it is on (one marker per explosion).
  const incidents = useIncidentLayer({ map: mapInstance, palette, clock, regionsById, placeGeometries, ensurePlaceGeometry })
  const incidentCatalog = useIncidentStore((s) => s.catalog)
  const legacyEvents = useMemo(() => withoutIncidentEvents(Object.values(events), incidentCatalog, filters.events), [events, incidentCatalog, filters.events])
  // The click handler is registered once; the incident selection goes through a ref so it sees the current hook.
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
      center: UKRAINE_CENTER,
      zoom: 5.2,
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
      if (pickRef.current) {
        pickRef.current(e.lngLat.lng, e.lngLat.lat)
        return
      }
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
      setSelectedEventId(null)
      setEventClickAt(null)
      incidentSelect.current(null)
      if (trackId !== null) return
      const incidentId = incidentHitAt(map, e.point)
      if (incidentId !== null) {
        selectRegion(null)
        setRegionClickAt(null)
        incidentSelect.current(incidentId, [e.lngLat.lng, e.lngLat.lat])
        return
      }
      const event = map.queryRenderedFeatures(e.point, { layers: ['event-points'] })[0]
      if (event?.properties?.id !== undefined) {
        selectRegion(null)
        setRegionClickAt(null)
        setSelectedEventId(Number(event.properties.id))
        setEventClickAt([e.lngLat.lng, e.lngLat.lat])
        return
      }
      // No marker under the cursor: (de)select the oblast for the feed filter and outline highlight.
      // Raion first (the level alerts are published at), the oblast where no raion polygon is drawn.
      const ob = map.queryRenderedFeatures(e.point, { layers: ['alerts-fill', 'raions-fill', 'oblasts-fill'] })[0]
      const regionId = ob?.layer.id === 'alerts-fill' ? ob.properties?.placeId : ob?.properties?.id
      selectRegion(regionId === undefined ? null : Number(regionId))
    })
    const regionLayers = ['raions-fill', 'oblasts-fill', 'alerts-fill']
    pointerCursor(map, [...regionLayers, 'event-points', ...INCIDENT_HIT_LAYERS])
    // Target under the cursor (within the hit radius): enlarged glyph, pointer cursor. Registered before the region
    // hover, whose resolver reads its state on the same mousemove.
    const hover = trackHover(map, { fallbackLayers: regionLayers, enabled: () => !pickRef.current })
    // Hover: the raion under the cursor with its oblast; the oblast alone where no raion polygon is drawn. An alerted
    // oblast is only hit through its alert fill (placeId = the oblast), which sits above the raion fill, so the raion
    // is looked for among every hit before an oblast is accepted. No region tip while a target is hovered.
    const stopHover = regionHover(map, tip.current!, regionLayers, (hits) => {
      if (hover.current() !== null) return null
      const byId = regionsRef.current
      const regionsHit = hits.map((hit) => byId.get(Number(hit.layer.id === 'alerts-fill' ? hit.properties?.placeId : hit.properties?.id))).filter((r): r is RegionDto => !!r)
      const region = regionsHit.find((r) => r.level === 'District') ?? regionsHit.find((r) => r.level === 'Region' || r.level === 'City')
      if (!region) return null
      const parent = region.parentId !== undefined ? byId.get(region.parentId) : undefined
      return { id: region.id, geometry: region.geometry, label: parent ? `${region.name} · ${parent.name}` : region.name }
    })
    map.on('error', (e) => console.error('[map]', e.error?.message ?? e))
    if (import.meta.env.DEV) Object.assign(window, { __map: map, __maplibre: maplibregl })
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

  // Theme switch: swap the base style, layers are re-added on style.load. Skipped on the initial mount.
  // Theme switch: reload the base style so icons and layers are re-added in the new palette. Skipped on mount.
  const appliedTheme = useRef(theme)
  useEffect(() => {
    const map = mapRef.current
    if (!map || appliedTheme.current === theme) return
    appliedTheme.current = theme
    styleLoaded.current = false
    map.setStyle(dark ? STYLE_DARK : STYLE_LIGHT)
  }, [theme, dark])

  // Crosshair cursor while picking a home point.
  useEffect(() => {
    const map = mapRef.current
    if (map) map.getCanvas().style.cursor = onPickHome ? 'crosshair' : ''
  }, [onPickHome])

  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    const apply = () => {
      if (!map.getSource('track-points')) return
      // Replay: the markers at their reconstructed positions for the replay clock (the store's `at` lags it by up to
      // a second while playing); live: the snapshot's tracks with their vectors.
      if (mode === 'history') setTrackData(map, buildReplayLayers(replay.positions(replay.t || clock.getTime(), filters), palette, selectedTrackId))
      else setTrackData(map, buildTrackLayers(visibleTracks(tracks, filters, clock), clock, regionsById, filters, { home, selectedId: selectedTrackId, palette, predecessors, selectedLink }))
      setData(map, 'events', buildEventLayer(legacyEvents, clock, filters))
      // An alerted oblast is drawn by the alert layer instead of the base fill, so the colours never blend;
      // raion / hromada alerts sit on top of the land fill. Hromada and city polygons are fetched on first need.
      for (const a of alertList) if (!regionsById.has(a.placeId) && !placeGeometries[a.placeId]) ensurePlaceGeometry(a.placeId)
      const alerted = new Set(alertList.filter((a) => regionsById.get(a.placeId)?.level === 'Region' || regionsById.get(a.placeId)?.level === 'City').map((a) => a.placeId))
      setData(map, 'alerts', alertLayer)
      // Raions of the 2020 reform: thin outlines, and the click target for the region window.
      const raions = regions.filter((r) => r.level === 'District' && r.countryCode === 'UA' && r.parentId !== undefined && regionsById.get(r.parentId)?.level === 'Region')
      setData(map, 'raions', { type: 'FeatureCollection', features: raions.map((r) => ({ type: 'Feature', geometry: r.geometry, properties: { id: r.id, name: r.name } })) })
      const ukraine = regions.find((r) => r.level === 'Country' && r.countryCode === 'UA')
      setData(map, 'ukraine', ukraine ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: ukraine.geometry, properties: {} }] } : emptyCollection())
      // Oblast polygons inside Ukraine (regions + Kyiv/Sevastopol city-regions): filled with our own land colour.
      const oblasts = regions.filter((r) => r.countryCode === 'UA' && (r.level === 'Region' || r.level === 'City'))
      setData(map, 'oblasts', { type: 'FeatureCollection', features: oblasts.map((r) => ({ type: 'Feature', geometry: r.geometry, properties: { id: r.id, alerted: alerted.has(r.id) } })) })
      const selected = regionsById.get(selectedRegionId ?? -1)
      setData(map, 'selected-region', selected ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: selected.geometry, properties: {} }] } : emptyCollection())
      setData(map, 'home', home ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [home.lon, home.lat] }, properties: {} }] } : emptyCollection())
    }
    if (styleLoaded.current) apply()
    else map.once('style.load', apply)
  }, [mode, tracks, legacyEvents, alertList, alertLayer, regions, regionsById, filters, home, clock, selectedRegionId, selectedTrackId, selectedLink, palette, predecessors, placeGeometries, ensurePlaceGeometry])

  // Replay: every frame of the replay clock moves the markers, straight into the source, without a render.
  useEffect(() => {
    if (mode !== 'history') return
    return replay.subscribe((t) => {
      const map = mapRef.current
      if (!map || !styleLoaded.current || !map.getSource('track-points')) return
      setData(map, 'track-points', buildReplayLayers(replay.positions(t, filters), palette, selectedTrackId).points)
    })
  }, [mode, filters, palette, selectedTrackId])

  // The predecessor fork follows the selection and refreshes when the selected track gets a newer report.
  const selectedSeenAt = selectedTrack?.lastSeenAt
  useEffect(() => {
    loadPredecessors(selectedTrackId)
  }, [selectedTrackId, selectedSeenAt, loadPredecessors])

  // MapLibre's own (unlayered) CSS sets position on .maplibregl-map and would override Tailwind's layered
  // utilities, so the positioned wrapper is a separate element.
  return (
    <div className="absolute inset-0">
      <div ref={container} className="h-full w-full" />
      <div ref={tip} hidden className="pointer-events-none absolute z-10 whitespace-nowrap rounded bg-white/95 px-2 py-1 text-xs shadow dark:bg-slate-900/95 dark:text-slate-100" />
      {mapInstance && selectedTrack && !selectedLink && <TrackPopup map={mapInstance} track={selectedTrack} anchor={clickAt} onDetails={() => onDetails(selectedTrack.id)} onClose={() => select(null)} />}
      {mapInstance && selectedLink && linkClickAt && <LinkPopup map={mapInstance} link={selectedLink} anchor={linkClickAt} onClose={() => selectLink(null)} />}
      {mapInstance && selectedEvent && eventClickAt && <EventPopup map={mapInstance} event={selectedEvent} anchor={eventClickAt} onClose={() => setSelectedEventId(null)} />}
      {mapInstance && incidents.selected && incidents.clickAt && <IncidentPopup map={mapInstance} incident={incidents.selected} anchor={incidents.clickAt} onClose={incidents.close} />}
      {filters.events && <IncidentLegend onOpen={(i) => incidents.open(i)} />}
      {mapInstance && !selectedTrack && selectedRegionId !== null && regionClickAt && <RegionPopup map={mapInstance} placeId={selectedRegionId} anchor={regionClickAt} onClose={() => selectRegion(null)} />}
    </div>
  )
}

function addLayers(map: maplibregl.Map, p: MapPalette) {
  const empty = emptyCollection()
  map.addSource('ukraine', { type: 'geojson', data: empty })
  map.addSource('oblasts', { type: 'geojson', data: empty })
  map.addSource('raions', { type: 'geojson', data: empty })
  map.addSource('alerts', { type: 'geojson', data: empty })
  addTrackSources(map)
  addEventLayers(map, p)

  // Ukraine gets its own land colour, oblast borders and a firm state border with a contrasting halo.
  // These go *under* the basemap's label layers so place names stay readable.
  const firstSymbol = map.getStyle().layers.find((l) => l.type === 'symbol')?.id
  map.addLayer(
    { id: 'oblasts-fill', type: 'fill', source: 'oblasts', filter: ['!', ['get', 'alerted']], paint: { 'fill-color': p.land, 'fill-opacity': 0.7 } },
    firstSymbol,
  )
  map.addLayer(
    { id: 'oblasts-line', type: 'line', source: 'oblasts', paint: { 'line-color': p.oblastLine, 'line-width': 0.9, 'line-opacity': 0.6 } },
    firstSymbol,
  )
  // Raions: an invisible fill for hit-testing and a hairline outline that gets a little firmer when zoomed in.
  map.addLayer({ id: 'raions-fill', type: 'fill', source: 'raions', paint: { 'fill-color': p.land, 'fill-opacity': 0 } }, firstSymbol)
  map.addLayer(
    { id: 'raions-line', type: 'line', source: 'raions', paint: { 'line-color': p.oblastLine, 'line-width': ['interpolate', ['linear'], ['zoom'], 5, 0.35, 8, 0.8], 'line-opacity': 0.5 } },
    firstSymbol,
  )
  map.moveLayer('raions-fill', 'oblasts-line')
  map.moveLayer('raions-line', 'oblasts-line')
  map.addLayer({ id: 'ukraine-halo', type: 'line', source: 'ukraine', paint: { 'line-color': p.borderHalo, 'line-width': 7, 'line-opacity': 0.9 } }, firstSymbol)
  map.addLayer(
    {
      id: 'ukraine-line',
      type: 'line',
      source: 'ukraine',
      layout: { 'line-join': 'round', 'line-cap': 'round' },
      paint: { 'line-color': p.border, 'line-width': 3.2, 'line-opacity': 1 },
    },
    firstSymbol,
  )

  // Air-raid alerts replace (not tint) the oblast fill: same opacity, own colour per level.
  const alert = alertPaint(p)
  map.addLayer({ id: 'alerts-fill', type: 'fill', source: 'alerts', paint: { 'fill-color': alert.fill, 'fill-opacity': 0.7 } }, firstSymbol)
  map.addLayer({ id: 'alerts-line', type: 'line', source: 'alerts', paint: { 'line-color': alert.line, 'line-opacity': 0.9, 'line-width': 1.4 } }, firstSymbol)

  addTrackLayers(map, p)
}
