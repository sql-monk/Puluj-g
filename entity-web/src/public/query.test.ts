import { describe, expect, it } from 'vitest'
import type { TaxonomyDto } from '../api/types'
import { normalizeTaxonomyQuery, parseDataQuery, resetDataQuery, serializeDataQuery } from './query'

const taxonomy: TaxonomyDto = {
  categories: [
    { id: 1, code: 'air', name: 'Повітря', classes: [{ id: 10, code: 'uav', name: 'БпЛА', families: [{ id: 100, code: 'shahed', name: 'Shahed', models: [{ id: 1000, code: '136', name: 'Shahed-136' }] }] }] },
    { id: 2, code: 'ground', name: 'Наземне', classes: [{ id: 20, code: 'ground', name: 'Наземне', families: [{ id: 200, code: 'ground', name: 'Наземне', models: [{ id: 2000, code: 'ground', name: 'Ground' }] }] }] },
  ],
}

describe('public filter query codec', () => {
  it('round-trips a canonical OR-within/AND-between filter object', () => {
    const input = new URLSearchParams('sourceIds=9,2,9&eventKinds=target.observed,alert.started&regionId=13&from=2026-09-15T09%3A00%3A00.000Z&to=2026-09-15T10%3A00%3A00.000Z&sort=-observedAt&cursor=second')
    const parsed = parseDataQuery(input)
    expect(parsed.errors).toEqual([])
    expect(parsed.value.sourceIds).toEqual([2, 9])
    expect(serializeDataQuery(input, parsed.value).toString()).toBe('eventKinds=alert.started%2Ctarget.observed&sourceIds=2%2C9&regionId=13&from=2026-09-15T09%3A00%3A00.000Z&to=2026-09-15T10%3A00%3A00.000Z&sort=-observedAt&cursor=second')
  })

  it('does not turn invalid dates or IDs into a broad, apparently applied filter', () => {
    const parsed = parseDataQuery(new URLSearchParams('sourceIds=7,nope&from=bad&to=2026-09-15T10:00:00Z'))
    expect(parsed.value.sourceIds).toEqual([7])
    expect(parsed.value.from).toBeUndefined()
    expect(parsed.errors).toHaveLength(2)
  })

  it('rejects timezone-ambiguous URL datetimes before they reach Kyiv inputs', () => {
    const parsed = parseDataQuery(new URLSearchParams('from=2026-03-29T03:30&to=2026-03-29T04:30'))
    expect(parsed.value.from).toBeUndefined()
    expect(parsed.errors).toContain('Період має містити коректні UTC початок і кінець')
  })

  it('removes only dependent taxonomy selections when their parent changes', () => {
    const result = normalizeTaxonomyQuery({ ...parseDataQuery(new URLSearchParams()).value, categoryIds: [1], classIds: [10, 20], familyIds: [100], modelIds: [1000, 2000] }, taxonomy)
    expect(result.value.classIds).toEqual([10])
    expect(result.value.modelIds).toEqual([1000])
    expect(result.removed).toEqual(['classIds=20', 'modelIds=2000'])
  })

  it('preserves a disabled historical taxonomy ID that the enabled dictionary cannot return', () => {
    const result = normalizeTaxonomyQuery({ ...parseDataQuery(new URLSearchParams()).value, modelIds: [1000, 900719] }, taxonomy)
    expect(result.value.modelIds).toEqual([1000, 900719])
    expect(result.removed).toEqual([])
  })

  it('reset keeps URL-owned sort but clears data filters and pagination', () => {
    const current = parseDataQuery(new URLSearchParams('q=kyiv&sourceIds=7&sort=-observedAt&cursor=page2')).value
    expect(resetDataQuery(current)).toEqual({ eventKinds: [], entityKinds: [], eventCategories: [], categoryIds: [], classIds: [], familyIds: [], modelIds: [], sourceIds: [], sort: '-observedAt' })
  })
})
