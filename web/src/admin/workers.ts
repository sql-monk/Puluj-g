// Pure helpers of the Workers / Pipeline panels: what a card shows for an instance, how timings scale, how a table sorts.
import type { PipelineSourceDto, StageTimingDto, WorkerInstanceDto } from '../api/admin'

export const KIND_LABEL: Record<string, string> = {
  processor: 'Процесор повідомлень',
  'collector-telegram': 'Колектор Telegram',
  'collector-alerts': 'Колектор alerts.in.ua',
  analytics: 'Аналітика джерел',
  worker: 'Worker (усе в одному процесі)',
  migrate: 'Міграції',
  other: 'Інстанс',
}

export const SERVICE_NOTE: Record<string, string> = {
  api: 'карта стане недоступною',
  'collector-telegram': 'нові пости Telegram не збиратимуться',
  'collector-alerts': 'тривоги не оновлюватимуться',
  analytics: 'аналітика джерел не оновлюватиметься',
  processor: 'ця репліка перестане обробляти повідомлення; її claim-и повернуться в чергу за 5 хв',
}

export type Stage = 'parse' | 'lock' | 'store' | 'total'
export const STAGES: { key: Stage; label: string }[] = [
  { key: 'parse', label: 'parse' },
  { key: 'lock', label: 'lock' },
  { key: 'store', label: 'store' },
  { key: 'total', label: 'разом' },
]

/** Bar widths (0…1) of the p50 / p90 of every stage relative to the largest p90, so the four rows share one scale. */
export function timingBars(timings: Record<Stage, StageTimingDto>): Record<Stage, { p50: number; p90: number }> {
  const max = Math.max(1, ...STAGES.map((s) => timings[s.key]?.p90Ms ?? 0))
  const out = {} as Record<Stage, { p50: number; p90: number }>
  for (const s of STAGES) {
    const t = timings[s.key]
    out[s.key] = { p50: t ? t.p50Ms / max : 0, p90: t ? t.p90Ms / max : 0 }
  }
  return out
}

export type Health = { ok: boolean | null; text: string }

/** One line of state for a card: heartbeat first, then what the instance says about itself. */
export function instanceHealth(w: WorkerInstanceDto, now: number = Date.now()): Health {
  if (!w.alive) return { ok: false, text: w.heartbeatAt ? `heartbeat застарів на ${Math.round(Math.max(0, now - new Date(w.heartbeatAt).getTime()) / 60000)} хв` : 'heartbeat відсутній' }
  if (w.status?.paused?.startsWith('history load:')) return { ok: null, text: 'Telegram дочитує історію' }
  if (w.status?.paused) return { ok: null, text: `обробку призупинено: ${w.status.paused}` }
  if (w.status?.llm?.pausedUntil && new Date(w.status.llm.pausedUntil).getTime() > now) return { ok: null, text: 'LLM на паузі' }
  if (w.containerState && w.containerState !== 'running') return { ok: false, text: `контейнер ${w.containerState}` }
  if (!w.status) return { ok: true, text: 'працює (без статусу)' }
  const age = (now - new Date(w.status.at).getTime()) / 1000
  if (age > 60) return { ok: null, text: `статус застарів на ${Math.round(age)} с` }
  return { ok: true, text: 'працює' }
}

/** Live processor count and bounded internal workers for the header. */
export function processorSummary(workers: WorkerInstanceDto[]): { replicas: number; alive: number; concurrency: number | null; perMinute: number } {
  const processors = workers.filter((w) => w.kind === 'processor')
  const alive = processors.filter((w) => w.alive)
  const concurrencies = alive.map((w) => w.status?.processing?.concurrency).filter((c): c is number => typeof c === 'number')
  const concurrency = concurrencies.length ? Math.max(...concurrencies) : null
  const perMinute = alive.reduce((sum, w) => sum + (w.status?.processing?.perMinute5 ?? 0), 0)
  return { replicas: processors.length, alive: alive.length, concurrency, perMinute }
}

export type SourceSortKey = keyof Pick<PipelineSourceDto, 'name' | 'received' | 'processed' | 'skipped' | 'failed' | 'pending' | 'withTargets' | 'targets' | 'tracks' | 'medianLagSeconds' | 'p50Ms' | 'p90Ms'> | 'withTargetsShare'

/** Sort sources by a column; strings ascending, numbers descending by default, nulls last either way. */
export function sortSources(list: PipelineSourceDto[], key: SourceSortKey, asc: boolean): PipelineSourceDto[] {
  const value = (s: PipelineSourceDto): number | string | null | undefined => (key === 'withTargetsShare' ? (s.processed > 0 ? s.withTargets / s.processed : null) : s[key])
  return [...list].sort((a, b) => {
    const va = value(a)
    const vb = value(b)
    if (va === vb) return a.name.localeCompare(b.name, 'uk')
    if (va === null || va === undefined) return 1
    if (vb === null || vb === undefined) return -1
    const cmp = typeof va === 'string' && typeof vb === 'string' ? va.localeCompare(vb, 'uk') : (va as number) - (vb as number)
    return asc ? cmp : -cmp
  })
}

export function share(part: number, whole: number): number | null {
  return whole > 0 ? (part / whole) * 100 : null
}
