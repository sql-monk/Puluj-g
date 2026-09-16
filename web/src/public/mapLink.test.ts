import { describe, expect, it } from 'vitest'
import { geometryForFocus, mapHref, parseMapReturn, parseMapSelection } from './mapLink'

describe('typed map links', () => {
  const source = { section: 'entities' as const, detail: { kind: 'observation', id: '9007199254740993' }, query: new URLSearchParams('regionId=7') }

  it('keeps a bigint selection as a string and creates an exact historical frame', () => {
    const href = mapHref(source, { kind: 'observation', id: '9007199254740993' }, { at: '2026-09-15T10:00:00.000Z' })
    expect(href).toContain('#/map/history?')
    const query = new URLSearchParams(href.split('?', 2)[1])
    expect(parseMapSelection(query)).toEqual({ kind: 'observation', id: '9007199254740993' })
    expect(query.get('at')).toBe('2026-09-15T10:00:00.000Z')
    expect(parseMapReturn(query)).toBe('#/entities/observation/9007199254740993?regionId=7')
  })

  it('rejects external and malformed return/selection values', () => {
    expect(parseMapSelection(new URLSearchParams('select=track:1%2Fetc'))).toBeNull()
    expect(parseMapReturn(new URLSearchParams('return=https%3A%2F%2Fevil.test'))).toBeNull()
  })

  it('preserves every available geometry in a message selection', () => {
    expect(geometryForFocus([{ geometry: { type: 'Point', coordinates: [30, 50] } }, { geometry: { type: 'Point', coordinates: [31, 51] } }])).toMatchObject({ type: 'GeometryCollection' })
  })
})
