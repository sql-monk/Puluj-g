import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { IncidentDto } from '../api/incidents'
import { buildCatalog } from '../catalog/catalog'
import { incidentFixture } from './incidentFixtures'
import { bindIncidentRealtime, createPushBatcher, isGone, supersedes, useIncidentStore } from './useIncidentStore'
import { useStore } from './useStore'

const now = new Date('2026-09-16T12:00:00Z')
const minutesAgo = (m: number) => new Date(now.getTime() - m * 60_000).toISOString()

const incident = (id: number, revision: number, extra: Partial<IncidentDto> = {}) => incidentFixture(id, revision, now, extra)

beforeEach(() => {
  useIncidentStore.setState({ byId: {}, selectedId: null, checkpoint: null, hiddenKinds: new Set(), catalog: buildCatalog([]) })
})

describe('incident store', () => {
  it('applies a first sight and newer revisions, ignores stale and out-of-order ones', () => {
    const s = useIncidentStore.getState()
    expect(s.apply(incident(1, 1))).toBe(true)
    expect(s.apply(incident(1, 3, { state: 'confirmed' }))).toBe(true)
    expect(s.apply(incident(1, 2))).toBe(false) // arrived late: ignored
    expect(s.apply(incident(1, 3))).toBe(false) // the same again (a redelivery or a second replica's push)
    expect(useIncidentStore.getState().byId[1].state).toBe('confirmed')
    expect(useIncidentStore.getState().byId[1].revision).toBe(3)
    expect(supersedes(undefined, incident(9, 1))).toBe(true)
    expect(supersedes(incident(9, 2), incident(9, 2))).toBe(false)
  })

  it('drops retracted, suppressed and merged incidents and clears their selection', () => {
    const s = useIncidentStore.getState()
    s.apply(incident(1, 1))
    s.apply(incident(2, 1))
    s.apply(incident(3, 1))
    s.select(1)
    expect(s.apply(incident(1, 2, { state: 'retracted' }))).toBe(true)
    expect(s.apply(incident(2, 2, { suppressed: true }))).toBe(true)
    expect(s.apply(incident(3, 2, { state: 'retracted', mergedIntoIncidentId: 4 }))).toBe(true)
    expect(Object.keys(useIncidentStore.getState().byId)).toEqual([])
    expect(useIncidentStore.getState().selectedId).toBeNull()
    expect(s.apply(incident(5, 1, { suppressed: true }))).toBe(false) // never seen and already gone: nothing to do
    expect(isGone(incident(5, 1, { suppressed: true }))).toBe(true)
  })

  it('batches a burst of a thousand pushes into one store update', () => {
    vi.useFakeTimers()
    const applyMany = vi.fn((dtos: IncidentDto[]) => useIncidentStore.getState().applyMany(dtos))
    const batcher = createPushBatcher({ applyMany }, 300)
    for (let i = 0; i < 1000; i++) batcher.push(incident(100 + (i % 100), 1 + Math.floor(i / 100)))
    expect(applyMany).not.toHaveBeenCalled()
    vi.advanceTimersByTime(300)
    expect(applyMany).toHaveBeenCalledTimes(1)
    expect(Object.keys(useIncidentStore.getState().byId)).toHaveLength(100)
    expect(useIncidentStore.getState().byId[100].revision).toBe(10)
    vi.useRealTimers()
  })

  it('prunes by the kind lifetime from the catalog', () => {
    const s = useIncidentStore.getState()
    s.setCatalog([{ id: 1, code: 'impact.explosion.reported', nameUk: 'Вибух', category: 'incident', requiresLocationForMap: true, createsIncident: true, mapVisible: true, sortOrder: 1, policyVersion: 2, mapLifetime: '02:00:00' }])
    s.apply(incident(1, 1, { lastReportedAt: minutesAgo(30) }))
    s.apply(incident(2, 1, { lastReportedAt: minutesAgo(130) }))
    useIncidentStore.getState().prune(now)
    expect(Object.keys(useIncidentStore.getState().byId)).toEqual(['1'])
  })

  it('a window reload replaces the set (what the server no longer lists is gone)', async () => {
    const s = useIncidentStore.getState()
    s.apply(incident(1, 1))
    s.apply(incident(2, 1))
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ from: '', to: '2026-09-16T12:00:00Z', mode: 'effective', items: [incident(2, 2)], truncated: false }) }))
    vi.stubGlobal('fetch', fetchMock)
    await useIncidentStore.getState().reloadWindow()
    expect(Object.keys(useIncidentStore.getState().byId)).toEqual(['2'])
    expect(useIncidentStore.getState().byId[2].revision).toBe(2)
    expect(useIncidentStore.getState().checkpoint).toBe('2026-09-16T12:00:00Z')
    // The delta continues from the checkpoint minus the slack.
    await useIncidentStore.getState().reloadDelta()
    const url = String((fetchMock.mock.calls[1] as unknown[])[0])
    expect(url).toContain('/api/incidents?from=2026-09-16T11%3A55%3A00.000Z')
    vi.unstubAllGlobals()
  })
})

describe('realtime binding (reconnect / resync / history throttle)', () => {
  it('reloads on (re)connect, on a mode change, throttles history scrubbing and fetches a delta on the timer', async () => {
    vi.useFakeTimers()
    const calls: string[] = []
    const fetchMock = vi.fn(async (url: string) => {
      calls.push(url)
      return { ok: true, json: async () => ({ from: '', to: '2026-09-16T12:00:00Z', mode: 'effective', items: [], truncated: false }) }
    })
    vi.stubGlobal('fetch', fetchMock)
    useStore.setState({ connection: 'disconnected', mode: 'live', at: null })
    const unbind = bindIncidentRealtime({ delayMs: 1000, historyMs: 300 })
    useStore.setState({ connection: 'reconnecting' })
    useStore.setState({ connection: 'connected' })
    await vi.advanceTimersByTimeAsync(0)
    expect(calls.filter((u) => u.startsWith('/api/incidents?from='))).toHaveLength(1) // the reconnect reload
    // Replay ticks: at changes every second in history mode → one reload per throttle window, not per tick.
    useStore.setState({ mode: 'history', at: new Date('2026-09-16T10:00:00Z') })
    await vi.advanceTimersByTimeAsync(0)
    const before = calls.length
    for (let i = 1; i <= 10; i++) {
      useStore.setState({ at: new Date(new Date('2026-09-16T10:00:00Z').getTime() + i * 1000) })
      await vi.advanceTimersByTimeAsync(50)
    }
    await vi.advanceTimersByTimeAsync(400)
    const historyReloads = calls.slice(before).filter((u) => u.includes('mode=recorded'))
    expect(historyReloads.length).toBeLessThanOrEqual(3)
    expect(historyReloads.length).toBeGreaterThanOrEqual(1)
    // Back to live: the delta timer fetches from the checkpoint.
    useStore.setState({ mode: 'live', at: null })
    await vi.advanceTimersByTimeAsync(0)
    const beforeDelta = calls.length
    await vi.advanceTimersByTimeAsync(1000)
    expect(calls.slice(beforeDelta).some((u) => u.includes('from=2026-09-16T11%3A55%3A00.000Z'))).toBe(true)
    unbind()
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  it('a Resync reloads after a jittered delay; a stale reload never overwrites a newer one', async () => {
    vi.useFakeTimers()
    let resolveFirst: (v: unknown) => void = () => {}
    const first = new Promise((r) => (resolveFirst = r))
    let n = 0
    const fetchMock = vi.fn(async () => {
      n++
      if (n === 1) {
        await first
        return { ok: true, json: async () => ({ from: '', to: 't1', mode: 'effective', items: [incident(1, 1)], truncated: false }) }
      }
      return { ok: true, json: async () => ({ from: '', to: 't2', mode: 'effective', items: [incident(1, 2)], truncated: false }) }
    })
    vi.stubGlobal('fetch', fetchMock)
    useStore.setState({ connection: 'connected', mode: 'live', at: null })
    const s = useIncidentStore.getState()
    const slow = s.reloadWindow() // answers last
    const fast = s.reloadWindow() // answers first
    await fast
    expect(useIncidentStore.getState().byId[1].revision).toBe(2)
    resolveFirst(undefined)
    await slow
    expect(useIncidentStore.getState().byId[1].revision).toBe(2) // the stale answer (rev 1) was discarded
    expect(useIncidentStore.getState().checkpoint).toBe('t2')
    // A push that lands while a reload is in flight keeps its newer revision.
    let resolveThird: (v: unknown) => void = () => {}
    const third = new Promise((r) => (resolveThird = r))
    fetchMock.mockImplementationOnce(async () => {
      await third
      return { ok: true, json: async () => ({ from: '', to: 't3', mode: 'effective', items: [incident(1, 2)], truncated: false }) }
    })
    const reload = s.reloadWindow()
    s.apply(incident(1, 5))
    resolveThird(undefined)
    await reload
    expect(useIncidentStore.getState().byId[1].revision).toBe(5)
    // Resync: nothing at once, one reload inside the jitter window.
    const beforeResync = fetchMock.mock.calls.length
    s.resync(1000)
    expect(fetchMock.mock.calls.length).toBe(beforeResync)
    await vi.advanceTimersByTimeAsync(1000)
    expect(fetchMock.mock.calls.length).toBe(beforeResync + 1)
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })
})
