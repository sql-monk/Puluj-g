import { describe, expect, it } from 'vitest'
import type { RegionDto } from '../api/types'
import { regionFromHits, selectedRegionFromHit } from './regionSelection'

const region = (id: number, level: string, parentId?: number): RegionDto => ({ id, name: String(id), level, countryCode: 'UA', parentId, geometry: { type: 'Point', coordinates: [0, 0] } })

describe('two-level region selection', () => {
  const oblast = region(1, 'Region')
  const district = region(2, 'District', 1)
  const other = region(3, 'Region')
  const byId = new Map([[1, oblast], [2, district], [3, other]])

  it('requires the parent oblast before a district can be selected', () => {
    expect(selectedRegionFromHit(district, null, byId)).toBe(1)
    expect(selectedRegionFromHit(district, 3, byId)).toBe(1)
    expect(selectedRegionFromHit(district, 1, byId)).toBe(2)
  })

  it('uses a district over an overlapping alert or oblast fill', () => {
    expect(regionFromHits([
      { layer: { id: 'alerts-fill' }, properties: { placeId: 1 } },
      { layer: { id: 'raions-fill' }, properties: { id: 2 } },
      { layer: { id: 'oblasts-fill' }, properties: { id: 1 } },
    ], byId)).toBe(district)
  })
})
