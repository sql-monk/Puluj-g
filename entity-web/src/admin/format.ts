// Pure formatting helpers of the admin panel (no React): numbers, bytes, times, durations. Tested in format.test.ts.

export function fmtNum(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return '—'
  return n.toLocaleString('uk-UA')
}

/** USD cost. More precision is useful below one cent, which is normal for individual LLM calls. */
export function fmtUsd(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return '—'
  return `$${n.toLocaleString('en-US', { minimumFractionDigits: n > 0 && n < 0.01 ? 4 : 2, maximumFractionDigits: 6 })}`
}

export function fmtBytes(b: number | null | undefined): string {
  if (b === null || b === undefined) return '—'
  if (b < 1024) return `${b} Б`
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(0)} КБ`
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(1)} МБ`
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} ГБ`
}

/** Milliseconds as the panel shows them: whole under a second, seconds with one decimal above. */
export function fmtMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return '—'
  if (ms < 1000) return `${Math.round(ms).toLocaleString('uk-UA')} мс`
  return `${(ms / 1000).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} с`
}

/** Seconds as a compact Ukrainian age/duration. */
export function fmtAge(seconds?: number | null): string {
  if (seconds === null || seconds === undefined || Number.isNaN(seconds)) return '—'
  if (seconds < 90) return `${Math.round(seconds)} с`
  if (seconds < 5400) return `${Math.round(seconds / 60)} хв`
  if (seconds < 172800) return `${(seconds / 3600).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} год`
  return `${(seconds / 86400).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} д`
}

export function fmtPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—'
  return `${value.toLocaleString('uk-UA', { maximumFractionDigits: digits })} %`
}

export function fmtTime(iso?: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return d.toLocaleString('uk-UA', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

/** "5 с тому", "12 хв тому", "1.5 год тому", "3 д тому"; `now` is injectable for tests. */
export function ago(iso?: string | null, now: number = Date.now()): string {
  if (!iso) return '—'
  const s = Math.max(0, (now - new Date(iso).getTime()) / 1000)
  if (s < 90) return `${Math.round(s)} с тому`
  if (s < 5400) return `${Math.round(s / 60)} хв тому`
  if (s < 172800) return `${(s / 3600).toFixed(1)} год тому`
  return `${Math.round(s / 86400)} д тому`
}

/** Duration between two instants as "3 д 4 год", "4 год 12 хв", "12 хв", "45 с". */
export function fmtDuration(fromIso: string | null | undefined, now: number = Date.now()): string {
  if (!fromIso) return '—'
  const s = Math.max(0, Math.round((now - new Date(fromIso).getTime()) / 1000))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0) return `${d} д ${h} год`
  if (h > 0) return `${h} год ${m} хв`
  if (m > 0) return `${m} хв`
  return `${s} с`
}

/** Seconds since an instant, whole. */
export function secondsSince(iso: string, now: number = Date.now()): number {
  return Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
}

/**
 * Where PostgreSQL's 1-based error position falls in the query: line and column (both 1-based) and that line's text.
 * A position just past the end (an incomplete query) points after the last character.
 */
export function sqlErrorLocation(sql: string, position: number): { line: number; column: number; text: string } | null {
  if (!Number.isInteger(position) || position < 1 || position > sql.length + 1) return null
  const before = sql.slice(0, position - 1)
  const lines = before.split('\n')
  const line = lines.length
  const start = before.length - lines[lines.length - 1].length
  const end = sql.indexOf('\n', start)
  return { line, column: position - start, text: sql.slice(start, end < 0 ? undefined : end) }
}

/** Bucket label for the pipeline chart: hour of day for hour buckets, day.month for day buckets. */
export function bucketLabel(iso: string, unit: 'hour' | 'day'): string {
  const d = new Date(iso)
  return unit === 'hour' ? d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' }) : d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' })
}
