import type { IncidentDto } from '../api/incidents'

/** Test fixture: a city-level explosion report in Kharkiv at the given revision (shared by the store, layer and catalog tests). */
export function incidentFixture(id: number, revision: number, now: Date, extra: Partial<IncidentDto> = {}): IncidentDto {
  const minutesAgo = (m: number) => new Date(now.getTime() - m * 60_000).toISOString()
  return {
    id,
    kind: 'impact.explosion.reported',
    kindName: 'Вибух',
    category: 'incident',
    state: 'reported',
    suppressed: false,
    eventAt: minutesAgo(10),
    firstReportedAt: minutesAgo(10),
    lastReportedAt: minutesAgo(5),
    location: { kind: 'city', placeId: 3126, placeName: 'Харків', point: { type: 'Point', coordinates: [36.23, 49.99] }, accuracyKm: 15, precision: 'city' },
    confidence: 'medium',
    sourceCount: 1,
    revision,
    provenance: { observationCount: 1, sourceIds: [1], generationId: 'g' },
    ...extra,
  }
}
