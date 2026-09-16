import { describe, expect, it } from 'vitest'
import { mergeMessagePage, messageParams } from './MessagesCatalogue'

describe('message catalogue query boundary', () => {
  it('translates shared URL filters to the U05 vocabulary without coercing IDs', () => {
    const params = messageParams({ eventKinds: ['explosion'], entityKinds: [], eventCategories: [], categoryIds: [3], classIds: [], familyIds: [], modelIds: [], sourceIds: [7], regionId: 12, q: 'текст', status: 'parsed_projection_pending', confidence: 'high', location: 'known', hasResults: true }, 'opaque-cursor')
    expect(params).toMatchObject({ sourceIds: '7', eventKinds: 'explosion', categoryIds: '3', regionId: 12, q: 'текст', outcome: 'parsed_projection_pending', confidence: 'high', location: 'located', hasResults: true, cursor: 'opaque-cursor' })
    expect(params.entityKinds).toBeUndefined()
  })

  it('deduplicates pages with string IDs beyond the JavaScript safe integer range', () => {
    const row = { id: '9007199254740993', sourceId: 1, publishedAt: '2026-09-17T00:00:00Z', receivedAt: '2026-09-17T00:00:00Z', sourceMessageKey: 'a', sourceRevision: '1', revisionGroup: '1:a', excerpt: 'text', outcome: 'completed', outcomeSource: 'stage_result' as const, resultCount: 1, matchedResultCount: 1, locatedResultCount: 1, unlocatedResultCount: 0, hasText: true }
    expect(mergeMessagePage([row], [row, { ...row, id: '9007199254740994' }]).map((item) => item.id)).toEqual(['9007199254740993', '9007199254740994'])
  })
})
