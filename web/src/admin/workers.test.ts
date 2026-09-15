import { describe, expect, it } from 'vitest'
import type { PipelineSourceDto, ProcessingStatusDto, StageTimingDto, WorkerInstanceDto, WorkerStatusDto } from '../api/admin'
import { instanceHealth, processorSummary, share, sortSources, timingBars } from './workers'

const NOW = new Date('2026-09-15T10:00:00Z').getTime()

const timing = (p50: number, p90: number): StageTimingDto => ({ samples: 10, meanMs: (p50 + p90) / 2, p50Ms: p50, p90Ms: p90, maxMs: p90 * 2 })

const processing = (over: Partial<ProcessingStatusDto> = {}): ProcessingStatusDto => ({
  concurrency: 2,
  processed: 100,
  skipped: 0,
  failed: 0,
  retried: 0,
  retriedTransient: 0,
  perMinute1: 4,
  perMinute5: 3,
  parse: timing(10, 20),
  lock: timing(1, 2),
  store: timing(25, 50),
  total: timing(36, 72),
  claims: [],
  ...over,
})

const status = (over: Partial<WorkerStatusDto> = {}): WorkerStatusDto => ({
  instance: 'processor-616c2ab99756',
  host: '616c2ab99756',
  roles: ['processing'],
  version: '1.0.0',
  builtAt: '2026-09-15T05:50:00Z',
  startedAt: '2026-09-15T06:00:00Z',
  at: '2026-09-15T09:59:55Z',
  pid: 1,
  workingSetBytes: 100_000_000,
  cpuPercent: 3,
  threads: 30,
  processing: processing(),
  ...over,
})

const worker = (over: Partial<WorkerInstanceDto> = {}): WorkerInstanceDto => ({
  name: 'processor-616c2ab99756',
  kind: 'processor',
  alive: true,
  heartbeatAt: '2026-09-15T09:59:40Z',
  status: status(),
  processed24h: 500,
  inProgress: 1,
  ...over,
})

describe('timingBars', () => {
  it('scales every stage to the largest p90 so the rows share one axis', () => {
    const bars = timingBars({ parse: timing(10, 20), lock: timing(1, 2), store: timing(25, 50), total: timing(36, 72) })
    expect(bars.total.p90).toBe(1)
    expect(bars.total.p50).toBeCloseTo(0.5)
    expect(bars.store.p90).toBeCloseTo(50 / 72)
    expect(bars.lock.p50).toBeCloseTo(1 / 72)
  })

  it('never divides by zero', () => {
    const bars = timingBars({ parse: timing(0, 0), lock: timing(0, 0), store: timing(0, 0), total: timing(0, 0) })
    expect(bars.total).toEqual({ p50: 0, p90: 0 })
  })
})

describe('instanceHealth', () => {
  it('is green for a fresh heartbeat and status', () => {
    expect(instanceHealth(worker(), NOW)).toEqual({ ok: true, text: 'працює' })
  })

  it('reports a stale heartbeat in minutes', () => {
    expect(instanceHealth(worker({ alive: false, heartbeatAt: '2026-09-15T09:48:00Z' }), NOW)).toEqual({ ok: false, text: 'heartbeat застарів на 12 хв' })
    expect(instanceHealth(worker({ alive: false, heartbeatAt: undefined }), NOW).text).toBe('heartbeat відсутній')
  })

  it('prefers the pause reasons over everything but the heartbeat', () => {
    expect(instanceHealth(worker({ status: status({ paused: 'history load' }) }), NOW)).toEqual({ ok: null, text: 'обробку призупинено: history load' })
    expect(instanceHealth(worker({ status: status({ paused: 'history load: 2 channel(s) since 2022-02-24' }) }), NOW)).toEqual({ ok: null, text: 'Telegram дочитує історію' })
    expect(instanceHealth(worker({ status: status({ llm: { enabled: true, model: 'm', pausedUntil: '2026-09-15T11:00:00Z', calls: 1, failures: 1 } }) }), NOW).text).toBe('LLM на паузі')
    expect(instanceHealth(worker({ status: status({ llm: { enabled: true, model: 'm', pausedUntil: '2026-09-15T09:00:00Z', calls: 1, failures: 1 } }) }), NOW).text).toBe('працює')
  })

  it('shows a stopped container and a missing or stale status document', () => {
    expect(instanceHealth(worker({ containerState: 'exited' }), NOW)).toEqual({ ok: false, text: 'контейнер exited' })
    expect(instanceHealth(worker({ status: undefined }), NOW)).toEqual({ ok: true, text: 'працює (без статусу)' })
    expect(instanceHealth(worker({ status: status({ at: '2026-09-15T09:57:00Z' }) }), NOW)).toEqual({ ok: null, text: 'статус застарів на 180 с' })
  })
})

describe('processorSummary', () => {
  it('counts replicas, takes the concurrency and sums the 5-minute rate over the live ones', () => {
    const s = processorSummary([
      worker(),
      worker({ name: 'processor-b', status: status({ processing: processing({ concurrency: 2, perMinute5: 1.5 }) }) }),
      worker({ name: 'processor-dead', alive: false, status: undefined }),
      worker({ name: 'collector-alerts', kind: 'collector-alerts', status: status({ processing: undefined }) }),
    ])
    expect(s).toEqual({ replicas: 3, alive: 2, concurrency: 2, perMinute: 4.5 })
  })

  it('has no concurrency without a status document', () => {
    expect(processorSummary([worker({ status: undefined })])).toEqual({ replicas: 1, alive: 1, concurrency: null, perMinute: 0 })
    expect(processorSummary([])).toEqual({ replicas: 0, alive: 0, concurrency: null, perMinute: 0 })
  })
})

describe('sortSources', () => {
  const src = (over: Partial<PipelineSourceDto>): PipelineSourceDto => ({
    sourceId: 1,
    code: 'a',
    name: 'A',
    type: 'Telegram',
    enabled: true,
    received: 0,
    processed: 0,
    skipped: 0,
    failed: 0,
    pending: 0,
    withTargets: 0,
    targets: 0,
    tracks: 0,
    series: [],
    ...over,
  })
  const list = [
    src({ sourceId: 1, name: 'Bravo', received: 10, processed: 10, withTargets: 5, p50Ms: 30 }),
    src({ sourceId: 2, name: 'Alpha', received: 30, processed: 0, withTargets: 0 }),
    src({ sourceId: 3, name: 'Charlie', received: 20, processed: 20, withTargets: 20, p50Ms: 10 }),
  ]

  it('sorts numbers descending and strings ascending', () => {
    expect(sortSources(list, 'received', false).map((s) => s.name)).toEqual(['Alpha', 'Charlie', 'Bravo'])
    expect(sortSources(list, 'received', true).map((s) => s.name)).toEqual(['Bravo', 'Charlie', 'Alpha'])
    expect(sortSources(list, 'name', true).map((s) => s.name)).toEqual(['Alpha', 'Bravo', 'Charlie'])
  })

  it('puts missing values last either way and derives the share of messages with targets', () => {
    expect(sortSources(list, 'p50Ms', false).map((s) => s.name)).toEqual(['Bravo', 'Charlie', 'Alpha'])
    expect(sortSources(list, 'p50Ms', true).map((s) => s.name)).toEqual(['Charlie', 'Bravo', 'Alpha'])
    expect(sortSources(list, 'withTargetsShare', false).map((s) => s.name)).toEqual(['Charlie', 'Bravo', 'Alpha'])
  })

  it('does not mutate the input', () => {
    const copy = [...list]
    sortSources(list, 'name', false)
    expect(list).toEqual(copy)
  })
})

describe('share', () => {
  it('is a percentage or null for an empty whole', () => {
    expect(share(1, 4)).toBe(25)
    expect(share(0, 0)).toBeNull()
  })
})
