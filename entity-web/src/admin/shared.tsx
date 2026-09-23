import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'

export { ago, bucketLabel, fmtAge, fmtBytes, fmtDuration, fmtMs, fmtNum, fmtPercent, fmtTime, fmtUsd, secondsSince } from './format'

/**
 * Poll a loader every `intervalMs`; the error is shown instead of stale data. `deps` are the loader's parameters (a period,
 * a filter): when they change, the data of the previous parameters stays visible but `stale` is true until the new answer
 * arrives, and an answer that comes back for parameters no longer selected is dropped.
 */
export function usePolled<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[] = []) {
  const key = JSON.stringify(deps)
  // A new token for every transition, including A -> B -> A.
  const scope = useMemo(() => ({ key }), [key])
  const [state, setState] = useState<{ data: T | null; scope: object | null; at: Date | null }>({ data: null, scope: null, at: null })
  const [failure, setFailure] = useState<{ scope: object; message: string } | null>(null)
  const loader = useRef(load)
  const request = useRef(0)
  const inFlight = useRef<number | null>(null)
  const active = useRef<object | null>(null)
  useLayoutEffect(() => { loader.current = load })
  useLayoutEffect(() => {
    active.current = scope
    return () => { active.current = null; request.current++; inFlight.current = null }
  }, [scope])
  const run = useCallback(async (automatic = false) => {
    if (active.current !== scope || (automatic && inFlight.current !== null)) return
    const generation = ++request.current
    inFlight.current = generation
    const current = () => active.current === scope && request.current === generation
    setFailure(null)
    try {
      const data = await loader.current()
      if (!current()) return
      setState({ data, scope, at: new Date() })
      setFailure(null)
    } catch (error) {
      if (current()) setFailure({ scope, message: error instanceof Error ? error.message : String(error) })
    } finally {
      // An obsolete request must not unlock a newer manual reload.
      if (inFlight.current === generation) inFlight.current = null
    }
  }, [scope])
  const reload = useCallback(() => run(), [run])
  useEffect(() => {
    void run()
    const id = window.setInterval(() => void run(true), intervalMs)
    return () => { window.clearInterval(id); request.current++; inFlight.current = null }
  }, [run, intervalMs])
  return { data: state.data, error: failure?.scope === scope ? failure.message : null, reload, stale: state.data !== null && state.scope !== scope, loadedAt: state.at }
}

/** "Оновлюється…" while the shown data belong to other parameters, otherwise when the snapshot was taken. */
export function Freshness({ stale, loadedAt, label }: { stale: boolean; loadedAt: Date | null; label?: string }) {
  if (stale) return <span className="rounded bg-amber-100 px-1.5 py-0.5 text-[11px] text-amber-800 dark:bg-amber-900/40 dark:text-amber-200" role="status">Оновлюється… показано попередні дані</span>
  if (!loadedAt) return null
  return <span className="text-[11px] text-slate-500">{label ? `${label} · ` : ''}знімок {loadedAt.toLocaleTimeString('uk-UA')}</span>
}

/**
 * A server-paged list, newest first: `load(undefined)` is the first page, `more()` appends the page after the last row.
 * When `deps` (filters) change the list starts over, and pages that arrive for the old filters are dropped.
 */
export function usePaged<T, C>(load: (cursor?: C) => Promise<{ items: T[]; next?: C | null; total?: number }>, deps: unknown[]) {
  const key = JSON.stringify(deps)
  const scope = useMemo(() => ({ key }), [key])
  const [state, setState] = useState<{ scope: object | null; items: T[] | null; total?: number; next: C | null; error: string | null; busy: boolean }>({ scope: null, items: null, next: null, error: null, busy: true })
  const loader = useRef(load)
  const active = useRef<object | null>(null)
  const generation = useRef(0)
  const inFlight = useRef(false)
  const cursor = useRef<C | null>(null)
  useLayoutEffect(() => { loader.current = load })
  useLayoutEffect(() => {
    active.current = scope
    cursor.current = null
    inFlight.current = false
    return () => { active.current = null; generation.current++; inFlight.current = false; cursor.current = null }
  }, [scope])
  const fetchPage = useCallback(async (more = false) => {
    if (active.current !== scope || (more && (inFlight.current || cursor.current === null))) return
    const nextCursor = more ? cursor.current! : undefined
    const requested = ++generation.current
    const current = () => active.current === scope && generation.current === requested
    inFlight.current = true
    if (!more) {
      cursor.current = null
      setState({ scope, items: null, total: undefined, next: null, error: null, busy: true })
    } else setState((old) => ({ ...old, error: null, busy: true }))
    try {
      const page = await loader.current(nextCursor)
      if (!current()) return
      cursor.current = page.next ?? null
      setState((old) => ({ scope, items: more ? [...(old.items ?? []), ...page.items] : page.items, total: page.total, next: page.next ?? null, error: null, busy: false }))
    } catch (error) {
      if (current()) setState((old) => ({ ...old, error: error instanceof Error ? error.message : String(error), busy: false }))
    } finally {
      if (current()) inFlight.current = false
    }
  }, [scope])
  useEffect(() => { void fetchPage() }, [fetchPage])
  const current = state.scope === scope
  return {
    items: current ? state.items : null, total: current ? state.total : undefined,
    error: current ? state.error : null, busy: !current || state.busy, hasMore: current && state.next !== null,
    more: () => void fetchPage(true), refresh: () => void fetchPage(),
  }
}

/** "Показати ще" / "Оновити" under a paged list, with how much is shown. */
export function PagerButtons({ busy, hasMore, count, total, onMore, onRefresh }: { busy: boolean; hasMore: boolean; count?: number; total?: number; onMore: () => void; onRefresh: () => void }) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-xs">
      {count !== undefined && (
        <span className="text-slate-500">
          показано {count.toLocaleString('uk-UA')}
          {total !== undefined ? ` із ${total.toLocaleString('uk-UA')}` : hasMore ? ', є старіші' : ', це всі'}
        </span>
      )}
      {hasMore && (
        <button className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600" disabled={busy} onClick={onMore}>
          Показати ще
        </button>
      )}
      <button className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600" disabled={busy} onClick={onRefresh}>
        {busy ? 'Завантаження…' : 'Оновити'}
      </button>
    </div>
  )
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
      {/* Numbers wrap instead of being cut: on a narrow screen "1 234 / 56" must stay readable in full. */}
      <div className={`break-words font-mono ${color}`} title={hint ?? value}>
        {value}
      </div>
      {hint && <div className="break-words text-[10px] text-slate-400">{hint}</div>}
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
