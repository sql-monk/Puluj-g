import { describe, expect, it } from 'vitest'
import type { EntityItem } from '../api/entityExtractor'

function merge(old: EntityItem[], page: EntityItem[]) {
  return [...old, ...page.filter((item) => !old.some((current) => current.entity === item.entity && current.id === item.id))]
}

describe('Entity Extractor catalogue', () => {
  it('keeps bigint identities as opaque strings across cursor pages', () => {
    const row: EntityItem = { entity: 'explosion', table: 'ee_explosions', id: '9007199254740993', values: {} }
    expect(merge([row], [row, { ...row, id: '9007199254740994' }]).map((item) => item.id)).toEqual(['9007199254740993', '9007199254740994'])
  })
})
