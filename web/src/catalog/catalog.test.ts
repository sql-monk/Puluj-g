import { describe, expect, it } from 'vitest'
import { buildCatalog, isKindShown, timeSpanMinutes, type CatalogKindDto } from './catalog'

const kind = (code: string, extra: Partial<CatalogKindDto> = {}): CatalogKindDto => ({
  id: code.length,
  code,
  nameUk: code,
  category: 'incident',
  requiresLocationForMap: true,
  createsIncident: true,
  mapVisible: true,
  sortOrder: 10,
  policyVersion: 2,
  ...extra,
})

describe('catalog adapter', () => {
  it('reads colour, icon shape, render mode and lifetime from the catalog', () => {
    const c = buildCatalog([kind('impact.explosion.reported', { mapColor: '#fb8c00', mapIcon: 'explosion', renderMode: 'area', mapLifetime: '02:00:00', legacyEventType: 'ExplosionReport' })])
    const k = c.kindOf('impact.explosion.reported')
    expect(k.color).toBe('#fb8c00')
    expect(k.shape).toBe('burst')
    expect(k.renderMode).toBe('area')
    expect(k.lifetimeMinutes).toBe(120)
    expect(c.legacyEventTypesOfIncidents.has('ExplosionReport')).toBe(true)
  })

  it('falls back by category and never leaves a kind without a shape or a label', () => {
    const c = buildCatalog([kind('fire.reported', { mapColor: undefined, mapIcon: undefined, mapLifetime: undefined })])
    const k = c.kindOf('fire.reported')
    expect(k.color).toBe('#dc2626')
    expect(k.shape).toBe('circle')
    expect(k.lifetimeMinutes).toBe(360)
    const unknown = c.kindOf('something.new')
    expect(unknown.name).toBe('something.new')
    expect(unknown.shape).toBe('circle')
    expect(c.legend.find((l) => l.code === 'fire.reported')?.label).toBe('fire.reported')
  })

  it('keeps catalog visibility and the viewer filter apart', () => {
    const c = buildCatalog([kind('a', { mapVisible: false }), kind('b')])
    expect(c.legend.map((l) => l.code)).toEqual(['b']) // a hidden kind is not even offered
    expect(isKindShown(c, 'a', new Set())).toBe(false)
    expect(isKindShown(c, 'b', new Set())).toBe(true)
    expect(isKindShown(c, 'b', new Set(['b']))).toBe(false)
  })

  it('orders the legend by the catalog sort order and only lists incident kinds', () => {
    const c = buildCatalog([kind('z', { sortOrder: 1 }), kind('a', { sortOrder: 5 }), kind('target.observed', { category: 'target', createsIncident: false, sortOrder: 0 })])
    expect(c.legend.map((l) => l.code)).toEqual(['z', 'a'])
  })

  it('parses .NET TimeSpans', () => {
    expect(timeSpanMinutes('02:00:00')).toBe(120)
    expect(timeSpanMinutes('1.12:30:00')).toBe(36 * 60 + 30)
    expect(timeSpanMinutes('00:00:00')).toBe(360)
    expect(timeSpanMinutes('garbage', 7)).toBe(7)
  })
})
