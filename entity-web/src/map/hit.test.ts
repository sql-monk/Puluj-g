import { describe, expect, it } from 'vitest'
import type { Position } from 'geojson'
import { nearestHit, type HitCandidate } from './layers'

// Screen = lon/lat × 10, so distances are easy to read.
const project = ([lon, lat]: Position) => ({ x: lon * 10, y: lat * 10 })
const marker = (id: number, lon: number, lat: number): HitCandidate => ({ geometry: { type: 'Point', coordinates: [lon, lat] }, layer: { id: 'track-points' }, properties: { id } })
const line = (id: number): HitCandidate => ({ geometry: { type: 'LineString', coordinates: [[0, 0], [5, 5]] }, layer: { id: 'track-forecast-hit' }, properties: { id } })

describe('nearestHit', () => {
  it('picks the point feature closest to the cursor on screen', () => {
    const hit = nearestHit([marker(1, 1, 1), marker(2, 1.05, 1.05), marker(3, 2, 2)], { x: 11, y: 11 }, project)
    expect(hit?.properties?.id).toBe(2)
  })

  it('prefers any point over a line, and takes the line only when no point is there', () => {
    expect(nearestHit([line(9), marker(1, 3, 3)], { x: 0, y: 0 }, project)?.properties?.id).toBe(1)
    expect(nearestHit([line(9)], { x: 0, y: 0 }, project)?.properties?.id).toBe(9)
    expect(nearestHit([], { x: 0, y: 0 }, project)).toBeUndefined()
  })
})
