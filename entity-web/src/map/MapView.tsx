import * as maplibregl from 'maplibre-gl'
import type { MapLayerMouseEvent } from 'maplibre-gl'
// maplibre-gl v6 resolves its worker with a dynamic `new URL(...)` that bundlers cannot follow; Vite bundles the
// worker entry explicitly here and MapLibre is pointed at it.
import maplibreWorkerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { RegionDto } from '../api/types'
import type { MapPalette } from './palette'
import RegionPopup from '../components/RegionPopup'
import { useStore, type Theme } from '../store/useStore'
import { getPalette } from './palette'
import { emptyCollection } from './geojson'
import { ATTRIBUTION, STYLE_DARK, STYLE_LIGHT, addIcons, pointerCursor, regionHover, setData } from './layers'
import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import type { Geometry } from 'geojson'
import { addEntityLayers, ENTITY_HIT_LAYERS, setEntityData } from './entityLayers'
import { publicHash } from '../public/routes'
import { filterEntityItems } from './entityFilters'

maplibregl.setWorkerUrl(maplibreWorkerUrl)

const UKRAINE_CENTER: [number, number] = [31.2, 48.8]
const KYIV_BOUNDS: [[number, number], [number, number]] = [[30.23, 50.21], [30.83, 50.6]]
const KYIV_PAGE_BOUNDS: [[number, number], [number, number]] = [[29.3, 49.7], [31.8, 51.1]]
const KYIV_DATA_BOUNDS: [[number, number], [number, number]] = [[29.0, 49.5], [32.1, 51.3]]

interface Props {
  dark: boolean
  theme: Theme
  onPickHome: ((lon: number, lat: number) => void) | null
  layoutKey?: string
  entityDefinitions: EntityDefinition[]
  entityItems: EntityItem[]
  entityKinds?: string[]
  from?: Date
  to?: Date
  kyiv?: boolean
}

/** Country-wide MapLibre map with all Puluj layers. Data flows one way: store -> GeoJSON sources. */
export default function MapView({ dark, theme, onPickHome, layoutKey, entityDefinitions, entityItems, entityKinds = [], from, to, kyiv = false }: Props) {
  const container = useRef<HTMLDivElement>(null)
  const mapRef = useRef<maplibregl.Map | null>(null)
  const [mapInstance, setMapInstance] = useState<maplibregl.Map | null>(null)
  const [selectedEntity, setSelectedEntity] = useState<EntityItem | null>(null)
  // Name of the raion / oblast under the cursor, moved by the hover handler directly (no render per mouse move).
  const tip = useRef<HTMLDivElement>(null)
  const styleLoaded = useRef(false)
  const applyDataRef = useRef<(() => void) | null>(null)
  const pickRef = useRef(onPickHome)
  pickRef.current = onPickHome
  const entityItemsRef = useRef(entityItems)
  entityItemsRef.current = entityItems
  const palette = getPalette(theme)
  const paletteRef = useRef(palette)
  paletteRef.current = palette
  // Where the viewer clicked to select a region: the region window opens there.
  const [regionClickAt, setRegionClickAt] = useState<[number, number] | null>(null)

  const regions = useStore((s) => s.regions)
  const filters = useStore((s) => s.filters)
  const home = useStore((s) => s.home)
  const mode = useStore((s) => s.mode)
  const at = useStore((s) => s.at)
  const now = useStore((s) => s.now)
  const selectedRegionId = useStore((s) => s.selectedRegionId)
  const selectRegion = useStore((s) => s.selectRegion)

  const regionsById = useMemo(() => new Map<number, RegionDto>(regions.map((r) => [r.id, r])), [regions])
  const regionsRef = useRef(regionsById)
  regionsRef.current = regionsById

  useEffect(() => {
    if (!container.current || mapRef.current) return
    const map = new maplibregl.Map({
      container: container.current,
      style: dark ? STYLE_DARK : STYLE_LIGHT,
      ...(kyiv ? { bounds: KYIV_BOUNDS, fitBoundsOptions: { padding: { top: 70, bottom: 40, left: 300, right: 400 } }, maxBounds: KYIV_PAGE_BOUNDS, minZoom: 8 } : { center: UKRAINE_CENTER, zoom: 5.2 }),
      attributionControl: { compact: true, customAttribution: ATTRIBUTION },
    })
    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right')
    map.addControl(new maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-left')
    map.on('style.load', () => {
      addIcons(map, paletteRef.current)
      addLayers(map, paletteRef.current)
      styleLoaded.current = true
      applyDataRef.current?.()
    })
    map.on('click', (e: MapLayerMouseEvent) => {
      if (pickRef.current) {
        pickRef.current(e.lngLat.lng, e.lngLat.lat)
        return
      }
      const entityLayers = ENTITY_HIT_LAYERS.filter((id) => map.getLayer(id))
      const entityHit = entityLayers.length ? map.queryRenderedFeatures(e.point, { layers: entityLayers })[0] : undefined
      const entityName = entityHit?.properties?.entity == null ? undefined : String(entityHit.properties.entity)
      const entityId = entityHit?.properties?.id == null ? undefined : String(entityHit.properties.id)
      const entity = entityName && entityId ? entityItemsRef.current.find((item) => item.entity === entityName && item.id === entityId) : undefined
      setSelectedEntity(entity ?? null)
      if (entity) { selectRegion(null); setRegionClickAt(null); return }
      setRegionClickAt([e.lngLat.lng, e.lngLat.lat])
      const ob = map.queryRenderedFeatures(e.point, { layers: ['raions-fill', 'oblasts-fill'] })[0]
      const regionId = ob?.properties?.id
      selectRegion(regionId === undefined ? null : Number(regionId))
    })
    const regionLayers = ['raions-fill', 'oblasts-fill']
    pointerCursor(map, [...regionLayers, ...ENTITY_HIT_LAYERS])
    const stopHover = regionHover(map, tip.current!, regionLayers, (hits) => {
      const byId = regionsRef.current
      const regionsHit = hits.map((hit) => byId.get(Number(hit.properties?.id))).filter((r): r is RegionDto => !!r)
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
      stopHover()
      map.remove()
      mapRef.current = null
      setMapInstance(null)
      styleLoaded.current = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kyiv])

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
      if (!map.getSource('ee-entities')) return
      // Raions of the 2020 reform: thin outlines, and the click target for the region window.
      const kyivRegion = regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ')
      const raions = regions.filter((r) => r.level === 'District' && r.countryCode === 'UA' && r.parentId !== undefined && (kyiv ? r.parentId === kyivRegion?.id : regionsById.get(r.parentId)?.level === 'Region'))
      setData(map, 'raions', { type: 'FeatureCollection', features: raions.map((r) => ({ type: 'Feature', geometry: r.geometry, properties: { id: r.id, name: r.name } })) })
      const ukraine = regions.find((r) => r.level === 'Country' && r.countryCode === 'UA')
      setData(map, 'ukraine', ukraine ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: ukraine.geometry, properties: {} }] } : emptyCollection())
      // Oblast polygons inside Ukraine (regions + Kyiv/Sevastopol city-regions): filled with our own land colour.
      const oblasts = regions.filter((r) => r.countryCode === 'UA' && (r.level === 'Region' || r.level === 'City'))
      setData(map, 'oblasts', { type: 'FeatureCollection', features: oblasts.map((r) => ({ type: 'Feature', geometry: r.geometry, properties: { id: r.id } })) })
      const selected = regionsById.get(selectedRegionId ?? -1)
      setData(map, 'selected-region', selected ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: selected.geometry, properties: {} }] } : emptyCollection())
      setData(map, 'home', home ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [home.lon, home.lat] }, properties: {} }] } : emptyCollection())
      const filtered = filterEntityItems(entityItems, filters, entityDefinitions, mode === 'history' && at ? at : now, entityKinds, from, to)
      setEntityData(map, entityDefinitions, kyiv ? filtered.filter((item) => item.geometry && boundsIntersect(geometryBounds(item.geometry), KYIV_DATA_BOUNDS)) : filtered)
    }
    applyDataRef.current = apply
    if (styleLoaded.current) apply()
    return () => { if (applyDataRef.current === apply) applyDataRef.current = null }
  }, [regions, regionsById, home, selectedRegionId, entityDefinitions, entityItems, entityKinds, from, to, filters, mode, at, now, kyiv])

  useEffect(() => { mapRef.current?.resize() }, [layoutKey])

  // MapLibre's own (unlayered) CSS sets position on .maplibregl-map and would override Tailwind's layered
  // utilities, so the positioned wrapper is a separate element.
  return (
    <div className="absolute inset-0">
      <div ref={container} className="h-full w-full" />
      <div ref={tip} hidden className="pointer-events-none absolute z-10 whitespace-nowrap rounded bg-white/95 px-2 py-1 text-xs shadow dark:bg-slate-900/95 dark:text-slate-100" />
      {selectedEntity && <aside className="pointer-events-auto absolute bottom-14 left-3 z-20 max-h-[45vh] w-80 overflow-auto rounded-xl bg-white/95 p-4 shadow-xl dark:bg-slate-900/95 dark:text-slate-100"><button className="float-right rounded px-2" aria-label="Закрити" onClick={() => setSelectedEntity(null)}>×</button><h2 className="font-semibold">{selectedEntity.entity} #{selectedEntity.id}</h2><dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">{Object.entries(selectedEntity.values).map(([key, value]) => <div className="contents" key={key}><dt className="font-medium">{key}</dt><dd className="break-all">{value == null ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value)}</dd></div>)}</dl><a className="mt-3 inline-block underline" href={publicHash({ section: 'entities', detail: { kind: selectedEntity.entity, id: selectedEntity.id }, query: new URLSearchParams() })}>Повні деталі та пов’язані записи</a></aside>}
      {mapInstance && !selectedEntity && selectedRegionId !== null && regionClickAt && <RegionPopup map={mapInstance} placeId={selectedRegionId} anchor={regionClickAt} onClose={() => selectRegion(null)} />}
    </div>
  )
}

function addLayers(map: maplibregl.Map, p: MapPalette) {
  const empty = emptyCollection()
  map.addSource('ukraine', { type: 'geojson', data: empty })
  map.addSource('oblasts', { type: 'geojson', data: empty })
  map.addSource('raions', { type: 'geojson', data: empty })
  map.addSource('selected-region', { type: 'geojson', data: empty })
  map.addSource('home', { type: 'geojson', data: empty })
  addEntityLayers(map)

  // Ukraine gets its own land colour, oblast borders and a firm state border with a contrasting halo.
  // These go *under* the basemap's label layers so place names stay readable.
  const firstSymbol = map.getStyle().layers.find((l) => l.type === 'symbol')?.id
  map.addLayer(
    { id: 'oblasts-fill', type: 'fill', source: 'oblasts', paint: { 'fill-color': p.land, 'fill-opacity': 0.7 } },
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
  map.addLayer({ id: 'selected-region-fill', type: 'fill', source: 'selected-region', paint: { 'fill-color': p.selectedRegion, 'fill-opacity': 0.18 } }, firstSymbol)
  map.addLayer({ id: 'selected-region-line', type: 'line', source: 'selected-region', paint: { 'line-color': p.selectedRegion, 'line-width': 3 } }, firstSymbol)
  map.addLayer({ id: 'home-point', type: 'circle', source: 'home', paint: { 'circle-radius': 7, 'circle-color': p.home, 'circle-stroke-color': p.borderHalo, 'circle-stroke-width': 2 } })
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

}

function geometryBounds(geometry: Geometry): [[number, number], [number, number]] | null {
  const points: number[][] = []
  const collect = (value: unknown) => {
    if (!Array.isArray(value)) return
    if (value.length >= 2 && typeof value[0] === 'number' && typeof value[1] === 'number') { points.push(value as number[]); return }
    for (const item of value) collect(item)
  }
  if (geometry.type === 'GeometryCollection') for (const item of geometry.geometries) { const bounds = geometryBounds(item); if (bounds) points.push(bounds[0], bounds[1]) }
  else collect(geometry.coordinates)
  if (!points.length) return null
  return [[Math.min(...points.map((p) => p[0])), Math.min(...points.map((p) => p[1]))], [Math.max(...points.map((p) => p[0])), Math.max(...points.map((p) => p[1]))]]
}

function boundsIntersect(value: [[number, number], [number, number]] | null, expected: [[number, number], [number, number]]) {
  return !!value && value[1][0] >= expected[0][0] && value[0][0] <= expected[1][0] && value[1][1] >= expected[0][1] && value[0][1] <= expected[1][1]
}
