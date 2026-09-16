import type * as maplibregl from 'maplibre-gl'
import type { Geometry } from 'geojson'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { IncidentDto } from '../api/incidents'
import type { RegionDto } from '../api/types'
import type { CatalogKindDto } from '../catalog/catalog'
import { useIncidentStore } from '../store/useIncidentStore'
import { useStore } from '../store/useStore'
import { addIncidentIcons, addIncidentLayers, addIncidentSources, buildIncidentLayers, setIncidentData } from './incidentLayer'
import type { MapPalette } from './palette'

interface Options {
  map: maplibregl.Map | null
  palette: MapPalette
  clock: Date
  regionsById: ReadonlyMap<number, RegionDto>
  placeGeometries: Record<number, Geometry>
  ensurePlaceGeometry: (placeId: number) => void
}

let catalogLoaded = false

/**
 * The incident layer on one map (MapView and KyivMapView share it — parity by construction): sources/layers/icons on
 * every (re)style, data from the incident store on every change, the selection with its popup anchor. The catalog is
 * fetched once per page; the window is loaded here so a map without realtime (history mode) still shows incidents.
 */
export function useIncidentLayer({ map, palette, clock, regionsById, placeGeometries, ensurePlaceGeometry }: Options) {
  const byId = useIncidentStore((s) => s.byId)
  const catalog = useIncidentStore((s) => s.catalog)
  const hiddenKinds = useIncidentStore((s) => s.hiddenKinds)
  const selectedId = useIncidentStore((s) => s.selectedId)
  const select = useIncidentStore((s) => s.select)
  const enabled = useStore((s) => s.filters.events)
  const [clickAt, setClickAt] = useState<[number, number] | null>(null)
  const ready = useRef(false)

  useEffect(() => {
    if (import.meta.env.DEV) Object.assign(window, { __incidents: useIncidentStore }) // E2E/debug hook (both maps set __map below)
    if (catalogLoaded) return
    catalogLoaded = true
    fetch('/api/event-kinds', { headers: { Accept: 'application/json' } })
      .then((r) => (r.ok ? (r.json() as Promise<CatalogKindDto[]>) : Promise.reject(new Error(String(r.status)))))
      .then((kinds) => useIncidentStore.getState().setCatalog(kinds))
      .then(() => useIncidentStore.getState().reloadWindow())
      .catch((e) => {
        catalogLoaded = false // a transient failure must not leave the page without a catalog: the next mount/effect retries
        console.warn('[incidents] catalog', e)
      })
  }, [])

  // Layers live in the style: re-added after every style change (theme switch), icons recoloured with the catalog.
  useEffect(() => {
    if (!map) return
    if (import.meta.env.DEV) Object.assign(window, { __map: map }) // E2E: whichever map is mounted (MapView or KyivMapView)
    const install = () => {
      if (map.getSource('incident-points')) return
      addIncidentSources(map)
      addIncidentIcons(map, catalog, palette)
      addIncidentLayers(map, palette)
      ready.current = true
    }
    if (map.isStyleLoaded()) install()
    map.on('style.load', install)
    return () => {
      map.off('style.load', install)
    }
  }, [map, palette, catalog])

  // Glyphs are redrawn only when the catalog or the theme changes (never per clock tick: an image swap re-lays out every symbol layer).
  useEffect(() => {
    if (!map || !map.getSource('incident-points')) return
    addIncidentIcons(map, catalog, palette)
  }, [map, catalog, palette])

  useEffect(() => {
    if (!map) return
    const apply = () => {
      if (!map.getSource('incident-points')) return
      setIncidentData(map, buildIncidentLayers(Object.values(byId), catalog, clock, { regionsById, placeGeometries, ensurePlaceGeometry, hiddenKinds, enabled }))
    }
    if (map.getSource('incident-points')) apply()
    else map.once('style.load', apply)
  }, [map, byId, catalog, hiddenKinds, clock, regionsById, placeGeometries, ensurePlaceGeometry, enabled])

  // Prune what the layer would not draw anyway (bounded memory in a long-open tab).
  useEffect(() => {
    useIncidentStore.getState().prune(clock)
  }, [clock])

  const selected: IncidentDto | undefined = selectedId === null ? undefined : byId[selectedId]
  const open = useCallback(
    (incident: IncidentDto, at?: [number, number]) => {
      // An unlocated incident has no anchor: the popup opens at the map centre (it is never drawn, only listed).
      const located = incident.location?.point ? ([incident.location.point.coordinates[0], incident.location.point.coordinates[1]] as [number, number]) : null
      const point = at ?? located ?? (map ? ([map.getCenter().lng, map.getCenter().lat] as [number, number]) : null)
      if (!point) return
      select(incident.id)
      setClickAt(point)
      if (!at && located && map) map.easeTo({ center: located })
    },
    [map, select],
  )
  const close = useCallback(() => {
    select(null)
    setClickAt(null)
  }, [select])

  return { selected, clickAt, open, close, select, setClickAt }
}
