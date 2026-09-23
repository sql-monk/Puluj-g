import { describe, expect, it } from 'vitest'
import { snapshotReferenceTime } from './selection'

describe('map snapshot reference time', () => {
  const now = new Date('2026-09-24T12:00:00Z')
  const requested = new Date('2026-09-24T11:00:00Z')
  const received = '2026-09-24T10:30:00Z'
  it('ages a lagging historical snapshot against its actual frame', () => {
    expect(snapshotReferenceTime(true, now, requested, received)).toEqual(new Date(received))
  })
  it('falls back to requested frame only without a valid received frame', () => {
    expect(snapshotReferenceTime(true, now, requested)).toBe(requested)
    expect(snapshotReferenceTime(true, now, requested, 'invalid')).toBe(requested)
    expect(snapshotReferenceTime(true, now, null)).toBe(now)
  })
  it('ignores a remembered historical frame on the live map', () => {
    expect(snapshotReferenceTime(false, now, requested, received)).toBe(now)
  })
})
