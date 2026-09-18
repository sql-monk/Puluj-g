import { describe, expect, it } from 'vitest'
import { boundsForGeometry, safeCameraPadding } from './regionCamera'
import { edgeOcclusionsForRects } from './useRegionCamera'

describe('region camera geometry and safe viewport', () => {
  it('uses the specified 70% safe rectangle with asymmetric occlusions', () => {
    expect(safeCameraPadding({ width: 1000, height: 800 }, { left: 200, right: 100, top: 50, bottom: 150 }))
      .toEqual({ left: 305, right: 205, top: 140, bottom: 240 })
  })

  it('counts inset desktop panels once at their nearest edge', () => {
    const box = { left: 0, top: 0, right: 1000, bottom: 800, width: 1000, height: 800 }
    const left = { left: 12, top: 56, right: 312, bottom: 788, width: 300, height: 732 }
    const right = { left: 604, top: 56, right: 988, bottom: 788, width: 384, height: 732 }
    const replay = { left: 280, top: 650, right: 720, bottom: 788, width: 440, height: 138 }
    expect(edgeOcclusionsForRects(box, [left, right, replay])).toEqual({ left: 312, right: 396, top: 0, bottom: 150 })
  })

  it('finds bounds across all polygons in a MultiPolygon', () => {
    expect(boundsForGeometry({ type: 'MultiPolygon', coordinates: [[[[30, 50], [31, 50], [31, 51], [30, 50]]], [[[24, 48], [25, 48], [25, 49], [24, 48]]]] }))
      .toEqual([[24, 48], [31, 51]])
  })

  it('does not invent an origin for malformed geometry', () => {
    expect(boundsForGeometry({ type: 'Polygon', coordinates: [[[999, 999], [NaN, 50]]] })).toBeNull()
  })
})
