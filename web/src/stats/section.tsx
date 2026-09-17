import { useEffect, useRef, useState, type ReactNode } from 'react'
import { serializeDataQuery, type DataQuery } from '../public/query'
import type { Period } from './period'

/**
 * One tab's payload for one exact period/filter key. A response that arrives
 * after a newer request was sent is dropped, and an old payload is never
 * rendered under the new URL header. `load` must be stable (api.stats.x).
 */
export function useSection<T>(load: (from: Date, to: Date, filter?: DataQuery, signal?: AbortSignal) => Promise<T>, period: Period, filter?: DataQuery) {
  const [data, setData] = useState<T | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const seq = useRef(0)
  const fromMs = period.from.getTime()
  const toMs = period.to.getTime()
  const filterKey = filter ? serializeDataQuery(new URLSearchParams(), filter).toString() : ''
  const requestKey = `${fromMs}|${toMs}|${filterKey}`
  const [dataKey, setDataKey] = useState<string | null>(null)
  const [errorKey, setErrorKey] = useState<string | null>(null)
  useEffect(() => {
    const id = ++seq.current
    const controller = new AbortController()
    setLoading(true)
    load(new Date(fromMs), new Date(toMs), filter, controller.signal)
      .then((d) => {
        if (id !== seq.current) return
        setData(d)
        setDataKey(requestKey)
        setError(null)
        setErrorKey(null)
      })
      .catch((e: Error) => {
        if (e.name === 'AbortError') return
        if (id === seq.current) {
          setError(e.message)
          setErrorKey(requestKey)
        }
      })
      .finally(() => {
        if (id === seq.current) setLoading(false)
      })
    return () => controller.abort()
  }, [attempt, load, requestKey, fromMs, toMs, filter])
  const retry = () => setAttempt((value) => value + 1)
  return { data: dataKey === requestKey ? data : null, loading, error: errorKey === requestKey ? error : null, retry }
}

/** The frame every tab shares: an exact-request skeleton, error and retry state. */
export function SectionShell<T>({ data, loading, error, retry, children }: { data: T | null; loading: boolean; error: string | null; retry: () => void; children: (data: T) => ReactNode }) {
  return (
    <>
      {error && <div role="alert" className="flex flex-wrap items-center gap-2 rounded bg-red-600 px-3 py-2 text-xs text-white">Не вдалося завантажити статистику: {error}<button type="button" className="rounded bg-white/20 px-2 py-1 font-medium hover:bg-white/30" onClick={retry}>Повторити</button></div>}
      {!data && !error && <div className="py-10 text-center text-sm text-slate-500" aria-live="polite">Завантаження статистики…</div>}
      {data && <div className={`flex flex-col gap-3 transition-opacity ${loading ? 'opacity-60' : ''}`}>{children(data)}</div>}
    </>
  )
}
