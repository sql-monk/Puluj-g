import { describe, expect, it } from 'vitest'
import { entityParams, mergeEntityPage } from './EntityCatalogue'

describe('entity catalogue query and cursor pages', () => {
  it('preserves all canonical filters without Date objects in the API contract', () => {
    const query = { eventKinds: ['Explosion'], entityKinds: ['observation'], eventCategories: [], categoryIds: [], classIds: [], familyIds: [], modelIds: [], sourceIds: [7], regionId: 3, from: new Date('2026-01-01T00:00:00Z'), to: new Date('2026-01-02T00:00:00Z'), q: 'Київ', status: 'open', confidence: 'High', location: 'located', hasResults: true, sort: '-at', cursor: 'opaque', pageSize: 25 }
    expect(entityParams(query)).toMatchObject({ eventKinds: 'Explosion', entityKinds: 'observation', sourceIds: '7', regionId: 3, cursor: 'opaque', from: '2026-01-01T00:00:00.000Z' })
  })
  it('does not duplicate a string bigint entity across cursor pages', () => {
    const row = { kind: 'observation' as const, id: '9007199254740993', title: 'x', at: '2026-01-01T00:00:00Z', sourceIds: [], totalEvidenceCount: 1, matchedEvidenceCount: 1, mapAvailable: false, map: {} }
    expect(mergeEntityPage([row], [row, { ...row, id: '9007199254740994' }]).map((x) => x.id)).toEqual(['9007199254740993', '9007199254740994'])
  })
})
