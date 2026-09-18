import { describe, expect, it } from 'vitest'
import { ago, bucketLabel, fmtBytes, fmtDuration, fmtMs, fmtNum, fmtPercent, secondsSince } from './format'

const NOW = new Date('2026-09-15T10:00:00Z').getTime()
const plain = (s: string) => s.replace(/\s/g, ' ')

describe('numbers', () => {
  it('formats with the Ukrainian grouping and a dash for nothing', () => {
    expect(plain(fmtNum(1234567))).toBe('1 234 567')
    expect(fmtNum(0)).toBe('0')
    expect(fmtNum(null)).toBe('—')
    expect(fmtNum(undefined)).toBe('—')
    expect(fmtNum(Number.NaN)).toBe('—')
  })

  it('shows milliseconds whole and seconds with a decimal', () => {
    expect(fmtMs(12.4)).toBe('12 мс')
    expect(fmtMs(999)).toBe('999 мс')
    expect(plain(fmtMs(1500))).toBe('1,5 с')
    expect(plain(fmtMs(12345))).toBe('12,3 с')
    expect(fmtMs(undefined)).toBe('—')
  })

  it('formats percentages and bytes', () => {
    expect(plain(fmtPercent(12.345))).toBe('12,3 %')
    expect(plain(fmtPercent(50, 0))).toBe('50 %')
    expect(fmtPercent(null)).toBe('—')
    expect(fmtBytes(512)).toBe('512 Б')
    expect(fmtBytes(10 * 1024)).toBe('10 КБ')
    expect(fmtBytes(1.5 * 1024 * 1024)).toBe('1.5 МБ')
    expect(fmtBytes(3 * 1024 * 1024 * 1024)).toBe('3.00 ГБ')
    expect(fmtBytes(undefined)).toBe('—')
  })
})

describe('times', () => {
  it('says how long ago in the coarsest unit that fits', () => {
    expect(ago('2026-09-15T09:59:55Z', NOW)).toBe('5 с тому')
    expect(ago('2026-09-15T09:48:00Z', NOW)).toBe('12 хв тому')
    expect(ago('2026-09-15T08:30:00Z', NOW)).toBe('1.5 год тому')
    expect(ago('2026-09-12T10:00:00Z', NOW)).toBe('3 д тому')
    expect(ago('2026-09-15T10:00:30Z', NOW)).toBe('0 с тому') // a clock ahead of ours is never negative
    expect(ago(undefined, NOW)).toBe('—')
  })

  it('formats an uptime from its start', () => {
    expect(fmtDuration('2026-09-15T09:59:15Z', NOW)).toBe('45 с')
    expect(fmtDuration('2026-09-15T09:48:00Z', NOW)).toBe('12 хв')
    expect(fmtDuration('2026-09-15T05:48:00Z', NOW)).toBe('4 год 12 хв')
    expect(fmtDuration('2026-09-12T06:00:00Z', NOW)).toBe('3 д 4 год')
    expect(fmtDuration(null, NOW)).toBe('—')
  })

  it('counts whole seconds since an instant', () => {
    expect(secondsSince('2026-09-15T09:59:57.400Z', NOW)).toBe(3)
    expect(secondsSince('2026-09-15T10:00:01Z', NOW)).toBe(0)
  })

  it('labels buckets by hour or by day', () => {
    expect(bucketLabel('2026-09-15T10:00:00Z', 'hour')).toMatch(/^\d{2}:\d{2}$/)
    expect(bucketLabel('2026-09-15T10:00:00Z', 'day')).toMatch(/^\d{2}\.\d{2}$/)
  })
})
