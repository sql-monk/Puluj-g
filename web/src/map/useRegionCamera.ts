import * as maplibregl from 'maplibre-gl'
import { useCallback, useEffect, useRef, type RefObject } from 'react'
import type { Geometry } from 'geojson'
import { boundsForGeometry, safeCameraPadding, type EdgeOcclusion } from './regionCamera'

type Rect = Pick<DOMRect, 'left' | 'right' | 'top' | 'bottom' | 'width' | 'height'>

/** Assign each overlay once, to its nearest container edge. The small visual gutter belongs to that occlusion. */
export function edgeOcclusionsForRects(box: Rect, overlays: Iterable<Rect>): EdgeOcclusion {
  let left = 0; let right = 0; let top = 0; let bottom = 0
  const gutter = Math.max(24, Math.min(box.width, box.height) * 0.05)
  for (const rect of overlays) {
    if (rect.width <= 0 || rect.height <= 0 || rect.right <= box.left || rect.left >= box.right || rect.bottom <= box.top || rect.top >= box.bottom) continue
    const distances = { left: Math.max(0, rect.left - box.left), right: Math.max(0, box.right - rect.right), top: Math.max(0, rect.top - box.top), bottom: Math.max(0, box.bottom - rect.bottom) }
    const closest = Math.min(distances.left, distances.right, distances.top, distances.bottom)
    if (closest > gutter) continue
    // A tall drawer at a corner is a left/right panel; a wide, shallow bar is a top/bottom panel.
    const horizontal = rect.height >= rect.width
    const side = horizontal
      ? (distances.left <= distances.right ? 'left' : 'right')
      : (distances.top <= distances.bottom ? 'top' : 'bottom')
    // Overlay extents are a union/max operation: two panels at an edge do not consume space twice.
    if (side === 'left') left = Math.max(left, Math.min(box.width, rect.right - box.left))
    if (side === 'right') right = Math.max(right, Math.min(box.width, box.right - rect.left))
    if (side === 'top') top = Math.max(top, Math.min(box.height, rect.bottom - box.top))
    if (side === 'bottom') bottom = Math.max(bottom, Math.min(box.height, box.bottom - rect.top))
  }
  return { left, right, top, bottom }
}

function edgeOcclusions(container: HTMLElement): EdgeOcclusion {
  return edgeOcclusionsForRects(container.getBoundingClientRect(), [...document.querySelectorAll<HTMLElement>('[data-map-occlusion="true"]')].map((element) => element.getBoundingClientRect()))
}

interface Options {
  map: maplibregl.Map | null
  container: RefObject<HTMLDivElement | null>
  geometry: Geometry | null | undefined
  placeId: number | null
  ensureGeometry: (placeId: number) => Promise<Geometry | undefined>
  /** Increments for an explicit map/selector/URL selection, including a repeat click on the same region. */
  request: number
  /** Open/close panels and replay are legitimate reasons to re-fit while camera is still automatic. */
  layoutKey: string
}

/** Keeps a selected region inside the measured unobscured part of a MapLibre map. */
export function useRegionCamera({ map, container, geometry, placeId, ensureGeometry, request, layoutKey }: Options) {
  const automatic = useRef(false)
  const applying = useRef(false)
  const latest = useRef({ map, geometry, placeId, request, layoutKey })
  latest.current = { map, geometry, placeId, request, layoutKey }

  const fit = useCallback(() => {
    const current = latest.current
    const node = container.current
    const bounds = boundsForGeometry(current.geometry)
    if (!current.map || !node || !bounds) return false
    const rect = node.getBoundingClientRect()
    if (rect.width <= 1 || rect.height <= 1) return false
    automatic.current = true
    // A phone drawer obscures the whole lower map. Keep the request and perform it after it closes instead of moving
    // the camera to a rectangle the reader cannot see.
    if (window.matchMedia('(max-width: 767px)').matches && document.querySelector('[data-section-panel="open"]')) return false
    current.map.resize()
    const padding = safeCameraPadding({ width: rect.width, height: rect.height }, edgeOcclusions(node))
    const camera = current.map.cameraForBounds(bounds, { padding, maxZoom: 10 })
    if (!camera) return false
    applying.current = true
    const done = () => { applying.current = false }
    current.map.once('moveend', done)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    if (reduced) current.map.jumpTo(camera)
    else current.map.easeTo({ ...camera, duration: 350, essential: false })
    window.setTimeout(done, 800)
    return true
  }, [container])

  useEffect(() => {
    if (request === 0) return
    automatic.current = true
    if (fit() || placeId === null) return
    const selectedRequest = request
    void ensureGeometry(placeId).then((loaded) => {
      // A late A response must never move the camera after B was selected.
      if (loaded && latest.current.request === selectedRequest && latest.current.placeId === placeId) fit()
    })
  }, [ensureGeometry, fit, geometry, placeId, request])

  useEffect(() => {
    if (automatic.current) window.requestAnimationFrame(() => { if (automatic.current) fit() })
  }, [fit, layoutKey])

  useEffect(() => {
    const node = container.current
    if (!node) return
    const observer = new ResizeObserver(() => {
      latest.current.map?.resize()
      if (automatic.current) fit()
    })
    observer.observe(node)
    return () => observer.disconnect()
  }, [container, fit])

  useEffect(() => {
    if (!map) return
    const manual = () => { if (!applying.current) automatic.current = false }
    map.on('dragstart', manual)
    map.on('zoomstart', manual)
    return () => { map.off('dragstart', manual); map.off('zoomstart', manual) }
  }, [map])
}
