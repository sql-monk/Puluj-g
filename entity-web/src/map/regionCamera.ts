import type { Geometry, Position } from 'geojson'

export type Bounds = [[number, number], [number, number]]

export interface Viewport {
  width: number
  height: number
}

export interface EdgeOcclusion {
  left: number
  right: number
  top: number
  bottom: number
}

/**
 * The usable map rectangle is the map container less the panels actually covering
 * one of its edges. A 15% breathing room is kept on every side of that rectangle.
 */
export function safeCameraPadding(viewport: Viewport, blocked: EdgeOcclusion) {
  const width = Math.max(0, viewport.width - blocked.left - blocked.right)
  const height = Math.max(0, viewport.height - blocked.top - blocked.bottom)
  return {
    left: blocked.left + width * 0.15,
    right: blocked.right + width * 0.15,
    top: blocked.top + height * 0.15,
    bottom: blocked.bottom + height * 0.15,
  }
}

/** Bounds for Polygon/MultiPolygon (and geometry collections returned by an API). Invalid points never become 0,0. */
export function boundsForGeometry(geometry: Geometry | null | undefined): Bounds | null {
  const positions: Position[] = []
  const collect = (item: Geometry | null | undefined) => {
    if (!item) return
    if (item.type === 'GeometryCollection') {
      item.geometries.forEach(collect)
      return
    }
    const visit = (value: unknown): void => {
      if (!Array.isArray(value)) return
      if (value.length >= 2 && typeof value[0] === 'number' && typeof value[1] === 'number') {
        const [lon, lat] = value
        if (Number.isFinite(lon) && Number.isFinite(lat) && lon >= -180 && lon <= 180 && lat >= -90 && lat <= 90) positions.push([lon, lat])
        return
      }
      value.forEach(visit)
    }
    visit(item.coordinates)
  }
  collect(geometry)
  if (positions.length === 0) return null
  let west = Infinity; let east = -Infinity; let south = Infinity; let north = -Infinity
  for (const [lon, lat] of positions) {
    west = Math.min(west, lon); east = Math.max(east, lon); south = Math.min(south, lat); north = Math.max(north, lat)
  }
  return Number.isFinite(west) && Number.isFinite(east) && Number.isFinite(south) && Number.isFinite(north) ? [[west, south], [east, north]] : null
}
