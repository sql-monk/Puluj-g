import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { DataQuery } from '../public/query'
import type { Period } from './period'

/**
 * One tab's payload for the period. While a new period loads the previous payload stays on screen (the tab dims it);
 * a response that arrives after a newer request was sent is dropped. `load` must be a stable reference (api.stats.x).
 */
export function useSection<T>(load: (from: Date, to: Date, filter?: DataQuery) => Promise<T>, period: Period, filter?: DataQuery) {
  const [data, setData] = useState<T | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const seq = useRef(0)
  const fromMs = period.from.getTime()
  const toMs = period.to.getTime()
  useEffect(() => {
    const id = ++seq.current
    setLoading(true)
    load(new Date(fromMs), new Date(toMs), filter)
      .then((d) => {
        if (id !== seq.current) return
        setData(d)
        setError(null)
      })
      .catch((e: Error) => {
        if (id === seq.current) setError(e.message)
      })
      .finally(() => {
        if (id === seq.current) setLoading(false)
      })
  }, [load, fromMs, toMs, filter])
  return { data, loading, error }
}

/** The frame every tab shares: the error line, the first-load placeholder, the dimmed previous payload while reloading. */
export function SectionShell<T>({ data, loading, error, children }: { data: T | null; loading: boolean; error: string | null; children: (data: T) => ReactNode }) {
  return (
    <>
      {error && <div className="rounded bg-red-600 px-3 py-1.5 text-xs text-white">Не вдалося завантажити статистику: {error}</div>}
      {!data && !error && <div className="py-10 text-center text-sm text-slate-500">Завантаження…</div>}
      {data && <div className={`flex flex-col gap-3 transition-opacity ${loading ? 'opacity-60' : ''}`}>{children(data)}</div>}
    </>
  )
}
