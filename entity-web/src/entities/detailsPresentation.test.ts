import { describe, expect, it } from 'vitest'
import { detailRows, displayValue, entityTitle, externalUrl, geometrySummary, sortEntityItemsNewestFirst } from './detailsPresentation'

describe('public entity presentation', () => {
  it('turns structured fields into readable Ukrainian rows instead of JSON', () => {
    const item = { entity: 'alert', table: 'ee_alerts', id: '7', values: { label: 'Повітряна тривога', status: 'active', attributes: { regions: ['Київ', 'Черкаси'], count: 2 }, geometry: { type: 'Polygon' } } }
    expect(entityTitle(item)).toBe('Повітряна тривога')
    expect(detailRows(item)).toEqual([
      { key: 'status', label: 'Стан', value: 'активна' },
      { key: 'attributes.regions', label: 'Області', value: 'Київ, Черкаси' },
      { key: 'attributes.count', label: 'Кількість', value: '2' },
    ])
    expect(displayValue({ role: 'target', count: 2 })).toBe('Роль: ціль; Кількість: 2')
  })

  it('summarises geometry and orders feed entries newest first', () => {
    expect(geometrySummary({ type: 'Point', coordinates: [30.5234, 50.4501] })).toContain('50,4501, 30,5234')
    expect(geometrySummary({ type: 'Polygon', coordinates: [[[1, 2], [3, 4], [1, 2]]] })).toBe('Полігон · 3 точок')
    const old = { entity: 'alert', table: 'ee_alerts', id: '1', occurredAt: '2026-09-24T08:00:00Z', values: {} }
    const fresh = { ...old, id: '2', occurredAt: '2026-09-24T09:00:00Z' }
    expect(sortEntityItemsNewestFirst([old, fresh]).map((item) => item.id)).toEqual(['2', '1'])
    expect(externalUrl('https://example.com/post')).toBe('https://example.com/post')
    expect(externalUrl('javascript:alert(1)')).toBeUndefined()
  })
})
