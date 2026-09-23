/* oxlint-disable react/set-state-in-effect -- activating the polling resource intentionally starts its first HTTP synchronization. */
import { useCallback, useEffect, useRef, useState } from 'react'
import { entityApi, type EntityDefinition, type EntityItem } from '../api/entityExtractor'

const STORAGE_KEY = 'puluj.ee.pollSeconds'
export const POLL_OPTIONS = [15, 30, 60, 120] as const
export type PollSeconds = (typeof POLL_OPTIONS)[number]

function initialInterval(): PollSeconds {
  const value = Number(localStorage.getItem(STORAGE_KEY))
  return normalizePollSeconds(value)
}

export function normalizePollSeconds(value: number): PollSeconds { return POLL_OPTIONS.includes(value as PollSeconds) ? value as PollSeconds : 30 }
export function refreshIsDue(active: boolean, visible: boolean, inFlight: boolean, lastRequestAt: number, now: number, seconds: PollSeconds): boolean {
  return active && visible && !inFlight && now - lastRequestAt >= seconds * 1000
}

export function useEntityPolling(active: boolean, at?: Date, loadSnapshot = true) {
  const [seconds, setSecondsState] = useState<PollSeconds>(initialInterval)
  const [definitions, setDefinitions] = useState<EntityDefinition[]>([])
  const [items, setItems] = useState<EntityItem[]>([])
  const [lastSuccess, setLastSuccess] = useState<string>()
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(false)
  const [truncated, setTruncated] = useState(false)
  const [catalogueTick, setCatalogueTick] = useState<string>()
  const inFlight = useRef<AbortController | undefined>(undefined)
  const lastRequest = useRef(0)
  const historical = at !== undefined
  const atMs = at?.getTime()
  const desiredAt = useRef(atMs)
  desiredAt.current = atMs
  const [snapshotAt, setSnapshotAt] = useState<string>()
  const refreshLatest = useRef<() => Promise<void>>(async () => {})

  const setSeconds = useCallback((value: PollSeconds) => {
    localStorage.setItem(STORAGE_KEY, String(value))
    setSecondsState(value)
  }, [])

  const refresh = useCallback(async () => {
    if (!refreshIsDue(active, document.visibilityState === 'visible', !!inFlight.current, 0, Date.now(), seconds)) return
    const requestedAt = desiredAt.current
    const controller = new AbortController()
    inFlight.current = controller
    lastRequest.current = Date.now()
    if (loadSnapshot) setLoading(true)
    try {
      if (loadSnapshot) {
        const [nextDefinitions, snapshot] = await Promise.all([entityApi.definitions(controller.signal), entityApi.snapshot(requestedAt === undefined ? undefined : new Date(requestedAt), controller.signal)])
        if (controller.signal.aborted || inFlight.current !== controller) return
        setDefinitions(nextDefinitions)
        setItems(snapshot.items)
        setTruncated(snapshot.truncated)
        setLastSuccess(snapshot.generatedAt)
        setSnapshotAt(snapshot.at ?? (requestedAt === undefined ? snapshot.generatedAt : new Date(requestedAt).toISOString()))
      } else {
        const refreshedAt = new Date().toISOString()
        setCatalogueTick(refreshedAt)
      }
      setError(undefined)
    } catch (reason) {
      if (!controller.signal.aborted && inFlight.current === controller && (reason as Error).name !== 'AbortError') setError((reason as Error).message)
    } finally {
      if (inFlight.current === controller) {
        inFlight.current = undefined
        setLoading(false)
        // Playback may advance while a slow frame is in flight. Finish it, label
        // its real timestamp, then request the newest desired frame (no starvation).
        if (!controller.signal.aborted && loadSnapshot && requestedAt !== desiredAt.current) void refreshLatest.current()
      }
    }
  }, [active, loadSnapshot, seconds, historical])
  refreshLatest.current = refresh
  useEffect(() => { if (active && loadSnapshot) void refresh() }, [atMs, active, loadSnapshot, refresh])

  useEffect(() => {
    if (!active) { inFlight.current?.abort(); inFlight.current = undefined; return }
    if (loadSnapshot) void refresh()
    const timer = window.setInterval(() => void refresh(), seconds * 1000)
    const visible = () => {
      if (document.visibilityState === 'visible' && Date.now() - lastRequest.current >= seconds * 1000) void refresh()
    }
    document.addEventListener('visibilitychange', visible)
    return () => { window.clearInterval(timer); document.removeEventListener('visibilitychange', visible); inFlight.current?.abort(); inFlight.current = undefined }
  }, [active, loadSnapshot, refresh, seconds])

  return { seconds, setSeconds, definitions, items, lastSuccess, error, loading, truncated, catalogueTick, snapshotAt, refresh }
}
