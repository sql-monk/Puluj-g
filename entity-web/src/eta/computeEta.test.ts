import { describe, expect, it } from 'vitest'
import type { TrackDto } from '../api/types'
import { angleDiffDeg, computeEta, fadeOpacity } from './computeEta'

const KYIV = { lon: 30.52, lat: 50.45 }
const NOW = new Date('2026-09-11T01:40:00Z')

function track(overrides: Partial<TrackDto> = {}): TrackDto {
  return {
    id: 1,
    status: 'Active',
    type: {
      categoryCode: 'UAV',
      categoryName: 'БпЛА',
      classCode: 'STRIKE_UAV',
      className: 'Ударний БпЛА',
      familyCode: 'SHAHED',
      familyName: 'Shahed family',
      displayMode: 'uav',
      fadeMinutes: 20,
      speedProfile: { minKmh: 150, maxKmh: 200, etaEnabled: true },
      label: 'Shahed family',
    },
    modelConfidence: 'Medium',
    trackConfidence: 'High',
    firstSeenAt: '2026-09-11T01:00:00Z',
    lastSeenAt: '2026-09-11T01:35:00Z',
    updatedAt: '2026-09-11T01:35:00Z',
    sourceIds: [1],
    fixes: [],
    messageIds: [],
    // Chernihiv region centroid, ~150 km NNE of Kyiv, heading SW.
    lastLocation: { kind: 'Region', placeId: 1, placeName: 'Чернігівська область', point: { type: 'Point', coordinates: [31.9, 51.4] }, accuracyKm: 40 },
    direction: { degrees: 225, kind: 'Compass', confidence: 'High' },
    targetCount: 2,
    distinctSourceCount: 1,
    ...overrides,
  }
}

describe('computeEta', () => {
  it('returns a range rounded to 5 minutes for a UAV heading towards home', () => {
    const r = computeEta(track(), KYIV, NOW)
    expect(r.kind).toBe('range')
    if (r.kind === 'range') {
      expect(r.minMinutes % 5).toBe(0)
      expect(r.maxMinutes % 5).toBe(0)
      expect(r.minMinutes).toBeLessThan(r.maxMinutes)
      // ~140 km at 150–200 km/h minus 5 min elapsed: roughly 25–70 min.
      expect(r.minMinutes).toBeGreaterThanOrEqual(20)
      expect(r.maxMinutes).toBeLessThanOrEqual(80)
      expect(r.confidence).toBe('Medium') // High lowered once for a region-level location
    }
  })

  it('never produces false precision', () => {
    const r = computeEta(track(), KYIV, NOW)
    expect(r.kind === 'range' && Number.isInteger(r.minMinutes) && r.minMinutes % 5 === 0).toBe(true)
  })

  it('reports "not towards you" when heading away', () => {
    const r = computeEta(track({ direction: { degrees: 45, kind: 'Compass', confidence: 'High' } }), KYIV, NOW)
    expect(r.kind).toBe('notTowards')
  })

  it('is unknown for ballistic targets (ETA disabled)', () => {
    const t = track()
    t.type = { ...t.type, speedProfile: { etaEnabled: false }, displayMode: 'ballistic' }
    expect(computeEta(t, KYIV, NOW)).toEqual({ kind: 'unknown', reason: 'disabled' })
  })

  it('is unknown without a usable location', () => {
    expect(computeEta(track({ lastLocation: { kind: 'DirectionOnly' } }), KYIV, NOW).kind).toBe('unknown')
    expect(computeEta(track({ lastLocation: undefined }), KYIV, NOW).kind).toBe('unknown')
  })

  it('is imminent when the user is inside the reported area', () => {
    const r = computeEta(track({ lastLocation: { kind: 'City', point: { type: 'Point', coordinates: [30.5, 50.45] }, accuracyKm: 15 } }), KYIV, NOW)
    expect(r.kind).toBe('imminent')
  })

  it('becomes stale long after the last report', () => {
    const r = computeEta(track(), KYIV, new Date('2026-09-11T02:30:00Z'))
    expect(r).toMatchObject({ kind: 'unknown', reason: 'stale' })
  })

  it('lowers confidence when no direction was reported', () => {
    const r = computeEta(track({ direction: undefined }), KYIV, NOW)
    expect(r.kind === 'range' && r.confidence).toBe('Low')
  })
})

describe('fadeOpacity', () => {
  it('fades from 1 to 0 over two fade windows', () => {
    const at = (min: number) => new Date(new Date('2026-09-11T01:00:00Z').getTime() + min * 60000)
    expect(fadeOpacity('2026-09-11T01:00:00Z', 20, at(0))).toBe(1)
    expect(fadeOpacity('2026-09-11T01:00:00Z', 20, at(10))).toBeCloseTo(0.6)
    expect(fadeOpacity('2026-09-11T01:00:00Z', 20, at(20))).toBeCloseTo(0.2)
    expect(fadeOpacity('2026-09-11T01:00:00Z', 20, at(40))).toBe(0)
  })
})

describe('angleDiffDeg', () => {
  it('wraps around', () => {
    expect(angleDiffDeg(350, 10)).toBe(20)
    expect(angleDiffDeg(0, 180)).toBe(180)
  })
})

describe('region polygons', () => {
  // A square oblast ~110 km across whose covering radius would cover the viewer, who is 55 km outside its edge.
  const region = {
    id: 1,
    name: 'Область',
    level: 'Region',
    countryCode: 'UA',
    geometry: { type: 'Polygon' as const, coordinates: [[[31.4, 50.9], [32.9, 50.9], [32.9, 51.9], [31.4, 51.9], [31.4, 50.9]]] },
  }
  const regions = new Map([[1, region]])
  const nearby = { lon: 32.0, lat: 50.4 }

  it('measures the distance to the edge, zero inside', () => {
    // Without the polygon the covering radius alone says "could be here already".
    const coarse = track({ direction: undefined, lastLocation: { kind: 'Region', placeId: 1, placeName: 'Область', point: { type: 'Point', coordinates: [32.15, 51.4] }, accuracyKm: 80 } })
    expect(computeEta(coarse, nearby, NOW).kind).toBe('imminent')
    const eta = computeEta(coarse, nearby, NOW, regions)
    expect(eta.kind).toBe('range')
    if (eta.kind === 'range') expect(eta.minMinutes).toBeGreaterThanOrEqual(10)
    const inside = computeEta(coarse, { lon: 32.0, lat: 51.2 }, NOW, regions)
    expect(inside.kind).toBe('imminent')
  })
})
