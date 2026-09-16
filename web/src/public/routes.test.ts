import { describe, expect, it } from 'vitest'
import { historyWindow, parsePublicHash, publicHash } from './routes'

describe('public route contract', () => {
  it('normalizes legacy links without discarding custom stats instants', () => {
    const parsed = parsePublicHash('#/stats?tab=alerts&from=2026-09-01T00%3A00%3A00.000Z&to=2026-09-02T00%3A00%3A00.000Z')
    expect(parsed.route.section).toBe('analytics')
    expect(parsed.canonicalHash).toBe('#/analytics?metric=alerts&from=2026-09-01T00%3A00%3A00.000Z&to=2026-09-02T00%3A00%3A00.000Z')
    expect(parsed.shouldReplace).toBe(true)
  })

  it('turns Kyiv into a map preset', () => {
    const parsed = parsePublicHash('#/kyiv?regionId=8')
    expect(parsed.route).toMatchObject({ section: 'map', mapMode: 'live', preset: 'kyiv' })
    expect(parsed.canonicalHash).toBe('#/map/live?preset=kyiv&regionId=8')
  })

  it('keeps canonical details and rejects unknown hashes safely', () => {
    expect(parsePublicHash('#/entities/observation/9007199254740993').route).toMatchObject({ section: 'entities', detail: { kind: 'observation', id: '9007199254740993' } })
    expect(parsePublicHash('#/not-a-page?regionId=4').canonicalHash).toBe('#/map/live')
  })

  it('serializes an explicit history route', () => {
    expect(publicHash({ section: 'map', mapMode: 'history', query: new URLSearchParams('from=a&to=b') })).toBe('#/map/history?from=a&to=b')
  })

  it('uses the exclusive endpoint for the initial history frame', () => {
    const window = historyWindow(new URLSearchParams('from=2026-09-15T09:00:00.000Z&to=2026-09-15T10:00:00.000Z'))
    expect(window.from.toISOString()).toBe('2026-09-15T09:00:00.000Z')
    expect(window.at.toISOString()).toBe('2026-09-15T09:59:59.999Z')
  })

  it('defaults a history route to a 24-hour window', () => {
    const window = historyWindow(new URLSearchParams(), new Date('2026-09-15T10:00:00.000Z'))
    expect(window.from.toISOString()).toBe('2026-09-14T10:00:00.000Z')
    expect(window.at.toISOString()).toBe('2026-09-15T09:59:59.999Z')
  })

  it('falls back safely for a malformed detail identifier', () => {
    expect(parsePublicHash('#/entities/track/%').canonicalHash).toBe('#/map/live')
  })
})
