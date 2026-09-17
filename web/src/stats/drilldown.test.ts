import { describe, expect, it } from 'vitest'
import { statsRequestPath } from '../api/client'
import { canSourceMessagesDrillDown, sourceMessagesHref } from './drilldown'
import type { DataQuery } from '../public/query'

const filter: DataQuery = {
  eventKinds: ['aircraft'], entityKinds: [], eventCategories: [], categoryIds: [], classIds: [], familyIds: [], modelIds: [], sourceIds: [9, 2], regionId: 7,
}

describe('analytics request and catalogue links', () => {
  it('uses one UTC period and the canonical U03 filter fields in a stats request', () => {
    expect(statsRequestPath('sources', new Date('2026-09-16T00:00:00Z'), new Date('2026-09-17T00:00:00Z'), filter)).toBe(
      '/api/stats/sources?from=2026-09-16T00%3A00%3A00.000Z&to=2026-09-17T00%3A00%3A00.000Z&eventKinds=aircraft&sourceIds=9%2C2&regionId=7',
    )
  })

  it('links a source only to the matching raw-message population', () => {
    expect(sourceMessagesHref(3, { from: '2026-09-16T00:00:00Z', to: '2026-09-17T00:00:00Z' }, filter)).toBe(
      '#/messages?eventKinds=aircraft&sourceIds=3&regionId=7&from=2026-09-16T00%3A00%3A00.000Z&to=2026-09-17T00%3A00%3A00.000Z',
    )
  })

  it('keeps the aggregate table for result-derived or unavailable filters', () => {
    expect(canSourceMessagesDrillDown({ applied: ['eventKinds=target.observed'], unavailable: [], timeBasis: 'publishedAt', population: 'raw' })).toBe(false)
    expect(canSourceMessagesDrillDown({ applied: ['sourceIds=3'], unavailable: [{ key: 'q', reason: 'unsupported' }], timeBasis: 'publishedAt', population: 'raw' })).toBe(false)
  })
})
