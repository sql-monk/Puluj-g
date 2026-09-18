import { describe, expect, it } from 'vitest'
import type { TargetDto, TrackDto } from '../api/types'
import { defaultMapConfig, mergeTargets, mergeTracks, nearestLifetime, pruneTracks } from './liveWindow'

const now = new Date('2026-09-15T12:00:00Z')
const minutesAgo = (m: number) => new Date(now.getTime() - m * 60_000).toISOString()

function track(id: number, lastSeenMinutesAgo: number): TrackDto {
  return { id, lastSeenAt: minutesAgo(lastSeenMinutesAgo) } as unknown as TrackDto
}

function target(id: number, observedMinutesAgo: number): TargetDto {
  return { id, observedAt: minutesAgo(observedMinutesAgo) } as unknown as TargetDto
}

describe('mergeTracks', () => {
  it('drops tracks older than the longest lifetime and keeps the rest', () => {
    const merged = mergeTracks({}, [track(1, 5), track(2, 119), track(3, 121), track(4, 3 * 24 * 60)], now, defaultMapConfig)
    expect(Object.keys(merged).map(Number)).toEqual([1, 2])
  })

  it('returns the same object when nothing qualifies', () => {
    const tracks = { 1: track(1, 5) }
    expect(mergeTracks(tracks, [track(9, 500)], now, defaultMapConfig)).toBe(tracks)
  })

  it('replaces an existing track by id', () => {
    const merged = mergeTracks({ 1: track(1, 30) }, [track(1, 1)], now, defaultMapConfig)
    expect(merged[1].lastSeenAt).toBe(minutesAgo(1))
  })
})

describe('pruneTracks', () => {
  it('removes expired tracks only', () => {
    const tracks = { 1: track(1, 10), 2: track(2, 200) }
    expect(Object.keys(pruneTracks(tracks, now, defaultMapConfig))).toEqual(['1'])
  })

  it('keeps the same object when nothing expired', () => {
    const tracks = { 1: track(1, 10) }
    expect(pruneTracks(tracks, now, defaultMapConfig)).toBe(tracks)
  })
})

describe('mergeTargets', () => {
  it('puts fresh reports first, newest on top, without duplicates, inside the cap', () => {
    const list = [target(10, 30), target(9, 40)]
    const merged = mergeTargets(list, [target(11, 20), target(10, 30), target(12, 1), target(1, 7 * 60)], now, defaultMapConfig, 3)
    expect(merged.map((o) => o.id)).toEqual([12, 11, 10])
  })

  it('returns the same list when every incoming report is stale or known', () => {
    const list = [target(10, 30)]
    expect(mergeTargets(list, [target(10, 30), target(2, 10 * 60)], now, defaultMapConfig)).toBe(list)
  })
})

describe('nearestLifetime', () => {
  it('snaps a remembered value to the closest offered option', () => {
    expect(nearestLifetime(25, [5, 10, 15, 20, 30])).toBe(20)
    expect(nearestLifetime(15, [5, 10, 15, 20, 30])).toBe(15)
    expect(nearestLifetime(999, [5, 10])).toBe(10)
  })
})
