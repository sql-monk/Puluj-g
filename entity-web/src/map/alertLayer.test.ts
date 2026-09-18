import { describe, expect, it } from 'vitest'
import type { Geometry, Polygon } from 'geojson'
import type { AlertDto, AlertLevel, RegionDto } from '../api/types'
import { buildAlertLayer } from './geojson'

const box = (x0: number, y0: number, x1: number, y1: number): Polygon => ({ type: 'Polygon', coordinates: [[[x0, y0], [x1, y0], [x1, y1], [x0, y1], [x0, y0]]] })
const region = (id: number, level: string, geometry: Geometry, parentId?: number): RegionDto => ({ id, name: `#${id}`, level, countryCode: 'UA', parentId, geometry })
// Oblast 5 = 0..10 square; raion 32597 = 2..4 square inside it; raion 32596 = 6..8 square inside it.
const regions = new Map<number, RegionDto>([
  [5, region(5, 'Region', box(0, 0, 10, 10))],
  [32597, region(32597, 'District', box(2, 2, 4, 4), 5)],
  [32596, region(32596, 'District', box(6, 6, 8, 8), 5)],
])

let seq = 0
const alert = (placeId: number, ancestorIds: number[], level: AlertLevel, extra: Partial<AlertDto> = {}): AlertDto => ({
  id: ++seq,
  placeId,
  placeName: `#${placeId}`,
  alertType: 'AirRaid',
  level,
  startedAt: `2026-09-15T02:0${seq % 10}:00Z`,
  ancestorIds,
  ...extra,
})

describe('buildAlertLayer', () => {
  it('draws one fill per place at the effective level of its alerts', () => {
    const fc = buildAlertLayer([alert(32597, [5], 'Yellow'), alert(32597, [5], 'Unknown')], regions)
    expect(fc.features).toHaveLength(1)
    expect(fc.features[0].properties.level).toBe('Yellow')
    expect(fc.features[0].properties.placeId).toBe(32597)
  })

  it('drops a raion whose level the oblast already shows', () => {
    const fc = buildAlertLayer([alert(5, [], 'Yellow'), alert(32597, [5], 'Yellow')], regions)
    expect(fc.features.map((f) => f.properties.placeId)).toEqual([5])
    expect(fc.features[0].geometry).toEqual(box(0, 0, 10, 10))
  })

  it('cuts a raion of another level out of the oblast fill and draws it on its own', () => {
    const fc = buildAlertLayer([alert(32597, [5], 'Red'), alert(5, [], 'Yellow'), alert(32596, [5], 'Yellow')], regions)
    expect(fc.features.map((f) => [f.properties.placeId, f.properties.level])).toEqual([
      [5, 'Yellow'],
      [32597, 'Red'],
    ])
    const oblast = fc.features[0].geometry
    expect(oblast.type).toBe('Polygon')
    expect((oblast as Polygon).coordinates).toHaveLength(2) // outer ring + the hole of the red raion
  })

  it('an unlevelled oblast alert keeps its red fill around a yellow raion', () => {
    const fc = buildAlertLayer([alert(5, [], 'Unknown'), alert(32597, [5], 'Yellow')], regions)
    expect(fc.features.map((f) => [f.properties.placeId, f.properties.level])).toEqual([
      [5, 'Unknown'],
      [32597, 'Yellow'],
    ])
  })

  it('draws a circle for a place without a polygon, and none for one without a point either', () => {
    const fc = buildAlertLayer(
      [
        alert(445, [19], 'Unknown', { location: { kind: 'City', placeId: 445, point: { type: 'Point', coordinates: [34, 45] }, accuracyKm: 5 } }),
        alert(446, [19], 'Unknown'),
      ],
      regions,
    )
    expect(fc.features).toHaveLength(1)
    expect(fc.features[0].properties.placeId).toBe(445)
    expect(fc.features[0].geometry.type).toBe('Polygon')
  })

  it('leaves out an outer fill its drawn descendants cover completely', () => {
    const same = new Map<number, RegionDto>([
      [5, region(5, 'Region', box(0, 0, 10, 10))],
      [7, region(7, 'District', box(0, 0, 10, 10), 5)],
    ])
    const fc = buildAlertLayer([alert(5, [], 'Yellow'), alert(7, [5], 'Red')], same)
    expect(fc.features.map((f) => f.properties.placeId)).toEqual([7])
  })

  it('takes the earliest alert of the place as the feature identity', () => {
    const late = alert(32597, [5], 'Yellow', { startedAt: '2026-09-15T03:00:00Z' })
    const early = alert(32597, [5], 'Unknown', { startedAt: '2026-09-15T01:00:00Z' })
    const fc = buildAlertLayer([late, early], regions)
    expect(fc.features[0].id).toBe(early.id)
    expect(fc.features[0].properties.startedAt).toBe(early.startedAt)
  })
})
