import { describe, expect, it } from 'vitest'
import { mergeMessagePage, messageParams } from './MessageCatalogue'

describe('message catalogue query and cursor pages', () => {
  it('uses U05 allow-listed filters while preserving bigint IDs as strings', () => {
    const query = { eventKinds: ['Explosion'], entityKinds: [], eventCategories: [], categoryIds: [4], classIds: [], familyIds: [], modelIds: [], sourceIds: [7], regionId: 3, from: new Date('2026-01-01T00:00:00Z'), to: new Date('2026-01-02T00:00:00Z'), q: 'Київ', outcome: 'parsed_projection_pending', location: 'located', hasResults: true, cursor: 'opaque', pageSize: 25 }
    expect(messageParams(query)).toMatchObject({ eventKinds: 'Explosion', categoryIds: '4', sourceIds: '7', outcome: 'parsed_projection_pending', cursor: 'opaque', from: '2026-01-01T00:00:00.000Z' })
  })

  it('does not duplicate adjacent bigint string IDs while appending a cursor page', () => {
    const row = { id: '9007199254740993', sourceId: 7, publishedAt: '2026-01-01T00:00:00Z', receivedAt: '2026-01-01T00:00:00Z', sourceMessageKey: 'key', sourceRevision: 'r1', revisionGroup: '7:key', outcome: 'completed', outcomeSource: 'stage_result' as const, resultCount: 1, matchedResultCount: 1, locatedResultCount: 1, unlocatedResultCount: 0, hasText: true }
    expect(mergeMessagePage([row], [row, { ...row, id: '9007199254740994' }]).map((item) => item.id)).toEqual(['9007199254740993', '9007199254740994'])
  })

  it('keeps a hand-written no-results URL valid by omitting contradictory derived filters', () => {
    const query = { eventKinds: ['Explosion'], entityKinds: [], eventCategories: [], categoryIds: [4], classIds: [], familyIds: [], modelIds: [], sourceIds: [], regionId: 3, confidence: 'high', location: 'located', hasResults: false }
    expect(messageParams(query)).toMatchObject({ hasResults: false, eventKinds: undefined, categoryIds: undefined, regionId: undefined, confidence: undefined, location: undefined })
  })
})
