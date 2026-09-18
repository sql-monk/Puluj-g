import { describe, expect, it } from 'vitest'
import { normalizePollSeconds, refreshIsDue } from './useEntityPolling'

describe('Entity Extractor polling policy', () => {
  it('accepts only the four supported persisted intervals', () => {
    expect([15, 30, 60, 120].map(normalizePollSeconds)).toEqual([15, 30, 60, 120])
    expect(normalizePollSeconds(3)).toBe(30)
  })

  it('does not poll hidden, inactive, or overlapping pages', () => {
    expect(refreshIsDue(true, false, false, 0, 60_000, 15)).toBe(false)
    expect(refreshIsDue(false, true, false, 0, 60_000, 15)).toBe(false)
    expect(refreshIsDue(true, true, true, 0, 60_000, 15)).toBe(false)
  })

  it('refreshes once the selected interval is overdue', () => {
    expect(refreshIsDue(true, true, false, 10_000, 24_999, 15)).toBe(false)
    expect(refreshIsDue(true, true, false, 10_000, 25_000, 15)).toBe(true)
  })
})
