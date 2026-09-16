import { describe, expect, it } from 'vitest'
import type { Polygon } from 'geojson'
import type { IncidentDto } from '../api/incidents'
import type { RegionDto, TargetDto } from '../api/types'
import { buildCatalog } from '../catalog/catalog'
import { incidentFixture } from '../store/incidentFixtures'
import { buildIncidentLayers, circlePolygon, incidentLayerSpecs, isOnMap, withoutIncidentEvents } from './incidentLayer'
import { getPalette } from './palette'

const now = new Date('2026-09-16T12:00:00Z')
const incident = (id: number, revision: number, extra: Partial<IncidentDto> = {}) => incidentFixture(id, revision, now, extra)
const catalog = buildCatalog([
  { id: 1, code: 'impact.explosion.reported', nameUk: 'Вибух', category: 'incident', requiresLocationForMap: true, createsIncident: true, mapVisible: true, sortOrder: 1, policyVersion: 2, mapLifetime: '02:00:00', mapIcon: 'explosion', mapColor: '#fb8c00', legacyEventType: 'ExplosionReport' },
  { id: 2, code: 'fire.reported', nameUk: 'Пожежа', category: 'incident', requiresLocationForMap: true, createsIncident: true, mapVisible: false, sortOrder: 2, policyVersion: 2, mapIcon: 'fire' },
])
const box: Polygon = { type: 'Polygon', coordinates: [[[36, 49], [37, 49], [37, 50], [36, 50], [36, 49]]] }
const regions = new Map<number, RegionDto>([[8, { id: 8, name: 'Харківська область', level: 'Region', countryCode: 'UA', geometry: box }]])

describe('incident layer geometry (§8.5)', () => {
  it('draws a city-level incident as a glyph at the reported point, labelled as a city marker', () => {
    const layers = buildIncidentLayers([incident(1, 1)], catalog, now, { regionsById: regions })
    expect(layers.points.features).toHaveLength(1)
    expect(layers.areas.features).toHaveLength(0)
    expect(layers.points.features[0].properties).toMatchObject({ id: 1, icon: 'incident-explosion', precision: 'city', approx: '' })
  })

  it('draws a region-level incident as the place polygon with an approximate anchor, never a plain pin', () => {
    const regional = incident(2, 1, { location: { kind: 'region', placeId: 8, placeName: 'Харківська область', point: { type: 'Point', coordinates: [36.5, 49.6] }, accuracyKm: 127.5, precision: 'region' } })
    const layers = buildIncidentLayers([regional], catalog, now, { regionsById: regions })
    expect(layers.areas.features).toHaveLength(1)
    expect(layers.areas.features[0].geometry).toEqual(box)
    expect(layers.points.features[0].properties.approx).toBe('≈')
  })

  it('falls back to the error circle when the polygon is not known yet and asks for it', () => {
    const asked: number[] = []
    const district = incident(3, 1, { location: { kind: 'district', placeId: 500, placeName: 'Ізюмський район', point: { type: 'Point', coordinates: [37.2, 49.2] }, accuracyKm: 40, precision: 'district' } })
    const layers = buildIncidentLayers([district], catalog, now, { regionsById: regions, ensurePlaceGeometry: (id) => asked.push(id) })
    expect(asked).toEqual([500])
    const ring = (layers.areas.features[0].geometry as Polygon).coordinates[0]
    expect(ring).toHaveLength(65)
    expect(Math.abs(ring[0][0] - (37.2 + 40 / (111.32 * Math.cos((49.2 * Math.PI) / 180))))).toBeLessThan(1e-6)
    expect(circlePolygon(0, 0, 111.32).coordinates[0][16][1]).toBeCloseTo(1, 3)
  })

  it('never invents coordinates: no location or unknown precision stays off the map', () => {
    const unlocated = incident(4, 1, { location: undefined })
    const unknown = incident(5, 1, { location: { kind: 'unknown', precision: 'unknown' } })
    const layers = buildIncidentLayers([unlocated, unknown], catalog, now)
    expect(layers.points.features).toHaveLength(0)
    expect(layers.areas.features).toHaveLength(0)
  })

  it('applies catalog visibility, the viewer filter, the kind lifetime and the layer switch', () => {
    const fire = incident(6, 1, { kind: 'fire.reported' })
    const old = incident(7, 1, { lastReportedAt: new Date(now.getTime() - 121 * 60_000).toISOString() })
    const fresh = incident(8, 1)
    expect(buildIncidentLayers([fire, old, fresh], catalog, now).points.features.map((f) => f.id)).toEqual([8])
    expect(buildIncidentLayers([fresh], catalog, now, { hiddenKinds: new Set(['impact.explosion.reported']) }).points.features).toHaveLength(0)
    expect(buildIncidentLayers([fresh], catalog, now, { enabled: false }).points.features).toHaveLength(0)
    expect(isOnMap(incident(9, 2, { state: 'retracted' }), catalog, now)).toBe(false)
    expect(isOnMap(incident(9, 2, { suppressed: true }), catalog, now)).toBe(false)
  })

  it('hides the legacy event markers of incident kinds while the incident layer is on (one marker per explosion)', () => {
    const events = [{ id: 1, eventType: 'ExplosionReport' }, { id: 2, eventType: 'TargetCancelled' }, { id: 3, eventType: 'AirDefenseActivity' }] as unknown as TargetDto[]
    expect(withoutIncidentEvents(events, catalog, true).map((e) => e.id)).toEqual([2, 3]) // AirDefenseActivity keeps its marker: its kind is not in this catalog
    expect(withoutIncidentEvents(events, catalog, false).map((e) => e.id)).toEqual([1, 2, 3])
  })

  it('every symbol layer names the style font (a symbol layer waiting on glyphs holds up its whole source)', () => {
    const specs = incidentLayerSpecs(getPalette('light'))
    for (const spec of specs) {
      if (spec.type !== 'symbol') continue
      const layout = spec.layout as Record<string, unknown>
      if (layout['text-field'] !== undefined) expect(layout['text-font']).toEqual(['Noto Sans Regular'])
    }
    expect(specs.map((s) => s.id)).toEqual(['incident-area-fill', 'incident-area-line', 'incident-clusters', 'incident-cluster-count', 'incident-icons'])
  })
})
