import type { StatsBucketUnit } from '../api/types'

export type Tab = 'targets' | 'alerts' | 'sources' | 'recognition'

/** The four tabs, each answering one question. */
export const TABS: { id: Tab; label: string; question: string }[] = [
  { id: 'targets', label: 'Цілі', question: 'що летіло' },
  { id: 'alerts', label: 'Тривоги', question: 'скільки сиділи в тривозі' },
  { id: 'sources', label: 'Джерела', question: 'хто повідомляв' },
  { id: 'recognition', label: 'Розпізнавання', question: 'як прочитано' },
]

export type Preset = '24h' | '7d' | '30d' | '90d' | 'custom'

export const PRESETS: { id: Preset; label: string; hours?: number }[] = [
  { id: '24h', label: '24 год', hours: 24 },
  { id: '7d', label: '7 д', hours: 24 * 7 },
  { id: '30d', label: '30 д', hours: 24 * 30 },
  { id: '90d', label: '90 д', hours: 24 * 90 },
  { id: 'custom', label: 'Довільний' },
]

export interface Period {
  preset: Preset
  from: Date
  to: Date
}

/** Where the page is: the tab and the period, both from the hash. */
export interface StatsRoute {
  tab: Tab
  period: Period
}

const FIVE_MIN = 5 * 60_000

/** `now` rounded down to 5 minutes: every viewer of a preset asks the server for the same period, so one cache entry serves them all. */
export function roundedNow(now = new Date()): Date {
  return new Date(Math.floor(now.getTime() / FIVE_MIN) * FIVE_MIN)
}

export function presetPeriod(preset: Exclude<Preset, 'custom'>, now = new Date()): Period {
  const to = roundedNow(now)
  const hours = PRESETS.find((p) => p.id === preset)?.hours ?? 24
  return { preset, from: new Date(to.getTime() - hours * 3600_000), to }
}

function isTab(s: string | null): s is Tab {
  return TABS.some((t) => t.id === s)
}

function isPreset(s: string | null): s is Exclude<Preset, 'custom'> {
  return s === '24h' || s === '7d' || s === '30d' || s === '90d'
}

/**
 * The route lives in the hash so a view can be linked: `#/analytics` (targets, 24 h), `#/analytics?tab=alerts&p=7d`,
 * `#/stats?tab=sources&from=…&to=…`. Anything unreadable falls back to the targets tab and 24 h.
 */
export function parseStatsHash(hash: string, now = new Date()): StatsRoute {
  const q = hash.indexOf('?')
  const params = new URLSearchParams(q >= 0 ? hash.slice(q + 1) : '')
  const tabParam = params.get('metric') ?? params.get('tab')
  const tab: Tab = isTab(tabParam) ? tabParam : 'targets'
  const p = params.get('preset') ?? params.get('p')
  if (isPreset(p)) return { tab, period: presetPeriod(p, now) }
  const from = params.get('from')
  const to = params.get('to')
  if (from && to) {
    const f = new Date(from)
    const t = new Date(to)
    if (!Number.isNaN(f.getTime()) && !Number.isNaN(t.getTime()) && t > f) {
      return { tab, period: { preset: 'custom', from: f, to: t } }
    }
  }
  return { tab, period: presetPeriod('24h', now) }
}

export function statsHash(route: StatsRoute): string {
  const params = new URLSearchParams()
  params.set('metric', route.tab)
  const { period } = route
  if (period.preset === 'custom') {
    params.set('from', period.from.toISOString())
    params.set('to', period.to.toISOString())
  } else if (period.preset !== '24h') {
    params.set('preset', period.preset)
  }
  const q = params.toString()
  return q ? `#/analytics?${q}` : '#/analytics'
}

/** Value for an <input type="datetime-local"> in the viewer's local time. */
export function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export const WEEKDAYS = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'нд']
export const HOURS = Array.from({ length: 24 }, (_, h) => String(h).padStart(2, '0'))

/** Short axis label of a bucket start: the hour for hour buckets, the day for days, "day.month" for weeks. */
export function bucketLabel(iso: string, unit: StatsBucketUnit): string {
  const d = new Date(iso)
  if (unit === 'hour') return d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
  return d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' })
}

/** Full label for a tooltip: the bucket start and, for hours, the date too. */
export function bucketTitle(iso: string, unit: StatsBucketUnit): string {
  const d = new Date(iso)
  if (unit === 'hour') return d.toLocaleString('uk-UA', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
  if (unit === 'week') return `тиждень з ${d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' })}`
  return `${WEEKDAYS[(d.getDay() + 6) % 7]} ${d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' })}`
}

/** "за годину" / "за добу" / "за тиждень" for subtitles. */
export function perBucket(unit: StatsBucketUnit): string {
  return unit === 'hour' ? 'за годину' : unit === 'day' ? 'за добу' : 'за тиждень'
}

/** A Kyiv calendar day `YYYY-MM-DD` as "пн 14.09". */
export function dayTitle(day: string): string {
  const [y, m, d] = day.split('-').map(Number)
  const date = new Date(Date.UTC(y, m - 1, d))
  return `${WEEKDAYS[(date.getUTCDay() + 6) % 7]} ${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}.${y}`
}

export function rangeText(period: Period): string {
  const f = (d: Date) => d.toLocaleString('uk-UA', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })
  return `${f(period.from)} — ${f(period.to)}`
}

/** Compact figure: 1 284 / 12,9 тис. / 4,2 млн. */
export function compact(n: number): string {
  if (Math.abs(n) >= 1_000_000) return `${(n / 1_000_000).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} млн`
  if (Math.abs(n) >= 10_000) return `${(n / 1000).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} тис.`
  return n.toLocaleString('uk-UA')
}

export function hoursText(h: number): string {
  if (h < 1) return `${Math.round(h * 60)} хв`
  // Use the same rounding boundary as the day representation below. Without it,
  // 47.99 was shown as “48 год” instead of the unambiguous “2 д 0 год”.
  if (Math.round(h) < 48) return `${h.toLocaleString('uk-UA', { maximumFractionDigits: 1 })} год`
  let days = Math.floor(h / 24)
  let rest = Math.round(h % 24)
  if (rest === 24) {
    days += 1
    rest = 0
  }
  return `${days} д ${rest} год`
}

export function lagText(s?: number): string {
  if (s === undefined || s === null) return '—'
  return s < 90 ? `${Math.round(s)} с` : `${Math.round(s / 60)} хв`
}

export function pct(part: number, whole: number): string {
  return whole > 0 ? `${Math.round((part / whole) * 100)}%` : '—'
}

export function num(n: number): string {
  return n.toLocaleString('uk-UA')
}
