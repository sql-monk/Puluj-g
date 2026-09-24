import { describe, expect, it } from 'vitest'
import { compact, dayTitle, hoursText, lagText, parseStatsHash, pct, percentValue, presetPeriod, roundedNow, statsHash } from './period'

const NOW = new Date('2026-09-15T10:07:42Z')

describe('period presets', () => {
  it('rounds "now" down to 5 minutes so every viewer shares one cache entry', () => {
    expect(roundedNow(NOW).toISOString()).toBe('2026-09-15T10:05:00.000Z')
  })

  it('builds the preset window backwards from the rounded end', () => {
    const p = presetPeriod('7d', NOW)
    expect(p.to.toISOString()).toBe('2026-09-15T10:05:00.000Z')
    expect(p.from.toISOString()).toBe('2026-09-08T10:05:00.000Z')
    expect(p.preset).toBe('7d')
    expect(presetPeriod('90d', NOW).from.toISOString()).toBe('2026-06-17T10:05:00.000Z')
  })
})

describe('stats hash', () => {
  it('defaults to the targets tab and 24 h', () => {
    const r = parseStatsHash('#/stats', NOW)
    expect(r.tab).toBe('targets')
    expect(r.period.preset).toBe('24h')
    expect(statsHash(r)).toBe('#/analytics?metric=targets')
  })

  it('reads the tab and the preset and writes them back the same way', () => {
    const r = parseStatsHash('#/stats?tab=alerts&p=7d', NOW)
    expect(r.tab).toBe('alerts')
    expect(r.period.preset).toBe('7d')
    expect(statsHash(r)).toBe('#/analytics?metric=alerts&preset=7d')
    expect(statsHash({ tab: 'sources', period: presetPeriod('24h', NOW) })).toBe('#/analytics?metric=sources')
    expect(statsHash({ tab: 'targets', period: presetPeriod('30d', NOW) })).toBe('#/analytics?metric=targets&preset=30d')
  })

  it('reads a custom range and keeps it round-trippable', () => {
    const r = parseStatsHash('#/stats?tab=recognition&from=2025-03-01T00%3A00%3A00.000Z&to=2025-04-01T00%3A00%3A00.000Z', NOW)
    expect(r.tab).toBe('recognition')
    expect(r.period.preset).toBe('custom')
    expect(r.period.from.toISOString()).toBe('2025-03-01T00:00:00.000Z')
    expect(r.period.to.toISOString()).toBe('2025-04-01T00:00:00.000Z')
    expect(parseStatsHash(statsHash(r), NOW)).toEqual(r)
  })

  it('falls back on an unknown tab, an unreadable or inverted range', () => {
    expect(parseStatsHash('#/stats?tab=nope&p=7d', NOW).tab).toBe('targets')
    expect(parseStatsHash('#/stats?tab=nope&p=7d', NOW).period.preset).toBe('7d')
    expect(parseStatsHash('#/stats?from=abc&to=def', NOW).period.preset).toBe('24h')
    expect(parseStatsHash('#/stats?from=2025-04-01T00:00:00Z&to=2025-03-01T00:00:00Z', NOW).period.preset).toBe('24h')
    expect(parseStatsHash('#/stats?p=5d', NOW).period.preset).toBe('24h')
    expect(parseStatsHash('#/stats?from=2025-03-01T00:00&to=2025-03-02T00:00', NOW).period.preset).toBe('24h')
  })
})

describe('formatting', () => {
  it('compacts large figures', () => {
    expect(compact(1284)).toBe((1284).toLocaleString('uk-UA'))
    expect(compact(12900)).toContain('тис.')
    expect(compact(4_200_000)).toContain('млн')
  })

  it('prints hours as minutes, hours or days', () => {
    expect(hoursText(0.5)).toBe('30 хв')
    expect(hoursText(2.5)).toContain('год')
    expect(hoursText(50)).toBe('2 д 2 год')
    expect(hoursText(47.99)).toBe('2 д 0 год')
  })

  it('prints lag, shares and Kyiv days', () => {
    expect(lagText(undefined)).toBe('—')
    expect(lagText(45)).toBe('45 с')
    expect(lagText(300)).toBe('5 хв')
    expect(pct(1, 4)).toBe('25%')
    expect(pct(57, 11_585)).toBe('<1%')
    expect(pct(1468, 1475)).toBe('>99%')
    expect(percentValue(99.8)).toBe('>99%')
    expect(pct(1, 0)).toBe('—')
    expect(dayTitle('2026-09-14')).toBe('пн 14.09.2026')
  })
})
