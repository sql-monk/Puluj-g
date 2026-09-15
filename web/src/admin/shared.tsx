import { useCallback, useEffect, useState } from 'react'

export { ago, bucketLabel, fmtBytes, fmtDuration, fmtMs, fmtNum, fmtPercent, fmtTime, fmtUsd, secondsSince } from './format'

/** Poll a loader every `intervalMs`; the error is shown instead of stale data. */
export function usePolled<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const run = useCallback(() => {
    load()
      .then((d) => {
        setData(d)
        setError(null)
      })
      .catch((e: Error) => setError(e.message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
  useEffect(() => {
    run()
    const id = window.setInterval(run, intervalMs)
    return () => window.clearInterval(id)
  }, [run, intervalMs])
  return { data, error, reload: run }
}

/** Tiny inline bar chart: one bar per bucket, oldest first. */
export function Bars({ values, color = 'bg-sky-500', height = 28, width = 'w-1.5', title }: { values: number[]; color?: string; height?: number; width?: string; title?: (i: number, v: number) => string }) {
  const max = Math.max(1, ...values)
  return (
    <div className="flex items-end gap-px" style={{ height }} aria-hidden>
      {values.map((v, i) => (
        <div key={i} className={`${width} rounded-sm ${v > 0 ? color : 'bg-slate-200 dark:bg-slate-700'}`} style={{ height: `${Math.max(2, (v / max) * 100)}%` }} title={title ? title(i, v) : String(v)} />
      ))}
    </div>
  )
}

export function Stat({ label, value, hint, tone }: { label: string; value: string; hint?: string; tone?: 'ok' | 'warn' | 'bad' }) {
  const color = tone === 'bad' ? 'text-red-600 dark:text-red-400' : tone === 'warn' ? 'text-amber-600 dark:text-amber-400' : tone === 'ok' ? 'text-emerald-600 dark:text-emerald-400' : ''
  return (
    <div className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/60">
      <div className="text-[10px] uppercase tracking-wide text-slate-500">{label}</div>
      <div className={`truncate font-mono ${color}`} title={hint ?? value}>
        {value}
      </div>
      {hint && <div className="truncate text-[10px] text-slate-400">{hint}</div>}
    </div>
  )
}

/** A confirmed action button: `window.confirm` with the consequence spelled out, then the async action. */
export function ConfirmButton({ label, confirm, onClick, danger, disabled, className = '' }: { label: string; confirm: string; onClick: () => Promise<void>; danger?: boolean; disabled?: boolean; className?: string }) {
  const [busy, setBusy] = useState(false)
  const run = async () => {
    if (!window.confirm(confirm)) return
    setBusy(true)
    try {
      await onClick()
    } finally {
      setBusy(false)
    }
  }
  const tone = danger ? 'border-red-300 text-red-700 hover:bg-red-50 dark:border-red-800 dark:text-red-300 dark:hover:bg-red-900/30' : 'border-slate-300 hover:bg-slate-100 dark:border-slate-600 dark:hover:bg-slate-800'
  return (
    <button type="button" className={`rounded border px-2 py-0.5 text-xs disabled:opacity-50 ${tone} ${className}`} onClick={() => void run()} disabled={disabled || busy}>
      {busy ? '…' : label}
    </button>
  )
}

export function Loading({ error, empty }: { error: string | null; empty?: boolean }) {
  if (error) return <div className="text-xs text-red-600">{error}</div>
  if (empty) return <div className="text-xs text-slate-500">Завантаження…</div>
  return null
}
