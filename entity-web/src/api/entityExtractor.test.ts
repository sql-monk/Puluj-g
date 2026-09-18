import { describe, expect, it } from 'vitest'
import { mergeEntityPages, type EntityPage } from './entityExtractor'

describe('multi-kind EE catalogue', () => {
  it('merges every selected kind and carries independent cursors', () => {
    const pages: EntityPage[] = [
      { items: [{ entity: 'target', table: 'ee_targets', id: '1', occurredAt: '2026-09-18T10:00:00Z', values: {} }], nextCursor: '10', totalCount: 20 },
      { items: [{ entity: 'explosion', table: 'ee_explosions', id: '2', occurredAt: '2026-09-18T11:00:00Z', values: {} }], nextCursor: '7', totalCount: 9 },
    ]
    const merged = mergeEntityPages(['target', 'explosion'], pages)
    expect(merged.items.map((item) => item.entity)).toEqual(['explosion', 'target'])
    expect(JSON.parse(merged.nextCursor ?? '{}')).toEqual({ target: '10', explosion: '7' })
    expect(merged.totalCount).toBe(29)
  })
})
