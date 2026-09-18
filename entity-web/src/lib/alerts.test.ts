import { describe, expect, it } from 'vitest'
import type { AlertDto, AlertLevel, RegionDto } from '../api/types'
import { alertsFor, ancestorsOf, effectiveLevel, levelTone } from './alerts'

const region = (id: number, level: string, parentId?: number): RegionDto => ({ id, name: `#${id}`, level, countryCode: 'UA', parentId, geometry: { type: 'Point', coordinates: [30, 50] } })
// Kyiv oblast 5 → raions 32597 (Bucha), 32596 (Brovary); hromada 34000 in Bucha raion; Kyiv 26 → district 5078.
const regions = new Map<number, RegionDto>([
  [5, region(5, 'Region')],
  [32597, region(32597, 'District', 5)],
  [32596, region(32596, 'District', 5)],
  [26, region(26, 'City')],
  [5078, region(5078, 'District', 26)],
])

let seq = 0
const alert = (placeId: number, ancestorIds: number[], level: AlertLevel = 'Unknown'): AlertDto => ({
  id: ++seq,
  placeId,
  placeName: `#${placeId}`,
  alertType: 'AirRaid',
  level,
  startedAt: '2026-09-15T02:00:00Z',
  ancestorIds,
})

describe('effectiveLevel', () => {
  it('red beats yellow, a published level beats none, none alone stands, empty is null', () => {
    expect(effectiveLevel([alert(1, [], 'Yellow'), alert(1, [], 'Red')])).toBe('Red')
    expect(effectiveLevel([alert(1, [], 'Unknown'), alert(1, [], 'Yellow')])).toBe('Yellow')
    expect(effectiveLevel([alert(1, [], 'Unknown')])).toBe('Unknown')
    expect(effectiveLevel([])).toBeNull()
  })

  it('tones: yellow is yellow, red and unlevelled are red', () => {
    expect(levelTone('Yellow')).toBe('yellow')
    expect(levelTone('Red')).toBe('red')
    expect(levelTone('Unknown')).toBe('red')
    expect(levelTone(null)).toBeNull()
  })
})

describe('ancestorsOf', () => {
  it('chains regions through parentId', () => {
    expect(ancestorsOf(32597, regions)).toEqual([5])
    expect(ancestorsOf(5078, regions)).toEqual([26])
    expect(ancestorsOf(5, regions)).toEqual([])
  })

  it('borrows the chain from an alert for a place the payload lacks', () => {
    const hromada = alert(34000, [32597, 5])
    expect(ancestorsOf(34000, regions, [alert(5, []), hromada])).toEqual([32597, 5])
    expect(ancestorsOf(34000, regions, [])).toEqual([])
  })
})

describe('alertsFor', () => {
  const city = alert(26, [], 'Unknown')
  const oblast = alert(5, [], 'Unknown')
  const bucha = alert(32597, [5], 'Yellow')
  const brovary = alert(32596, [5], 'Yellow')
  const hromada = alert(34000, [32597, 5], 'Red')
  const all = [city, oblast, bucha, brovary, hromada]

  it('a Kyiv district is under the city-wide alert', () => {
    expect(alertsFor(all, 5078, ancestorsOf(5078, regions))).toEqual([city])
  })

  it('the oblast sees its own alert first, then the raions and hromadas inside', () => {
    expect(alertsFor(all, 5, [])).toEqual([oblast, bucha, brovary, hromada])
  })

  it('a raion sees its own, the covering oblast, then the hromada inside — not the neighbour', () => {
    expect(alertsFor(all, 32597, [5])).toEqual([bucha, oblast, hromada])
    expect(alertsFor(all, 32596, [5])).toEqual([brovary, oblast])
  })

  it('a hromada sees the raion before the oblast', () => {
    expect(alertsFor(all, 34000, [32597, 5])).toEqual([hromada, bucha, oblast])
  })
})
