import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { kyivLabel, parseKyivInput, toKyivInput } from '../public/kyivTime'
import { replay } from '../replay/engine'
import { useStore } from '../store/useStore'
import type { HistoryWindow } from '../public/routes'

const PRESETS_H = [1, 3, 6, 12, 24]
/** Minutes of history per real second. */
const SPEEDS = [
  { label: '1 хв/с', value: 1 },
  { label: '5 хв/с', value: 5 },
  { label: '15 хв/с', value: 15 },
  { label: '60 хв/с', value: 60 },
]
const STEP_MIN = 1
/** The store's `at` (alerts snapshot, feed cutoff) follows the replay clock at most this often while playing. */
const STORE_SYNC_MS = 1000
/** The clock readout follows the replay clock at most this often. */
const READOUT_MS = 100

function fmtTime(d: Date) {
  return d.toLocaleTimeString('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit' })
}
function fmtDate(d: Date) {
  return d.toLocaleDateString('uk-UA', { timeZone: 'Europe/Kyiv', day: '2-digit', month: '2-digit' })
}

/**
 * EE snapshot history: the engine supplies only the clock. Snapshot polling follows
 * the store once per second; explicit seeks, pause and end persist through the route.
 */
export default function ReplayBar({ initialWindow, onHistoryChange, onClose, keyboardEnabled = true }: { initialWindow: HistoryWindow; onHistoryChange: (window: HistoryWindow) => void; onClose: () => void; keyboardEnabled?: boolean }) {
  const at = useStore((s) => s.at)
  const setMode = useStore((s) => s.setMode)
  const loading = useStore((s) => s.loading)
  const [hours, setHours] = useState(() => (initialWindow.to.getTime() - initialWindow.from.getTime()) / 3600_000)
  const [to, setTo] = useState(() => initialWindow.to)
  const from = useMemo(() => new Date(to.getTime() - hours * 3600_000), [to, hours])
  const [speed, setSpeed] = useState(5)
  const [playing, setPlaying] = useState(false)
  const onHistoryChangeRef = useRef(onHistoryChange)
  const routeAtRef = useRef(initialWindow.at.getTime())
  const initializedRef = useRef(false)
  useEffect(() => { onHistoryChangeRef.current = onHistoryChange }, [onHistoryChange])
  const [windowLoaded, setWindowLoaded] = useState(false)
  // The clock readout: the replay engine's instant, throttled; the store's `at` is behind it while playing.
  const [readout, setReadout] = useState<number | null>(null)

  const totalMin = hours * 60
  const end = to.getTime() - 1
  const cursor = new Date(Math.min(end, Math.max(from.getTime(), readout ?? (at ?? initialWindow.at).getTime())))
  const posMin = Math.min(totalMin, Math.max(0, (cursor.getTime() - from.getTime()) / 60000))
  const isLiveEdge = to.getTime() >= Date.now() - 60_000

  const seek = useCallback(
    (d: Date) => {
      const clamped = new Date(Math.min(end, Math.max(from.getTime(), d.getTime())))
      replay.pause()
      setPlaying(false)
      replay.seek(clamped.getTime())
      setReadout(clamped.getTime())
      setMode('history', clamped)
      onHistoryChangeRef.current({ from, to, at: clamped })
    },
    [from, to, end, setMode],
  )

  // EE history is a sequence of database snapshots. The old processor replay payload must never be mixed into this fork.
  useEffect(() => {
    setPlaying(false)
    replay.pause()
    const current = initializedRef.current ? useStore.getState().at : initialWindow.at
    initializedRef.current = true
    seek(current && current >= from && current < to ? current : new Date(to.getTime() - 1))
    // The clock's inclusive endpoint must remain inside the route's half-open range.
    replay.load({ from: from.toISOString(), to: new Date(end).toISOString(), tracks: [] })
    setWindowLoaded(true)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [from, to])

  // Back/forward navigation within the same range must move the clock too.
  useEffect(() => {
    if (routeAtRef.current === initialWindow.at.getTime()) return
    routeAtRef.current = initialWindow.at.getTime()
    const next = Math.min(end, Math.max(from.getTime(), initialWindow.at.getTime()))
    if (next === replay.t) return
    replay.pause()
    setPlaying(false)
    replay.seek(next)
    setReadout(next)
    setMode('history', new Date(next))
  }, [initialWindow.at.getTime(), from, end, setMode])

  // Leaving replay stops the clock and drops the window's tracks.
  useEffect(() => () => replay.clear(), [])

  useEffect(() => {
    replay.speed = speed
  }, [speed])

  useEffect(() => {
    if (playing) replay.play()
    else replay.pause()
  }, [playing])

  // The engine's clock drives the readout (often) and the store's `at` (rarely); the end of the window stops playback.
  useEffect(() => {
    let lastStore = 0
    let lastReadout = 0
    const off = replay.subscribe((t) => {
      const now = performance.now()
      if (now - lastReadout >= READOUT_MS || !replay.playing) {
        lastReadout = now
        setReadout(t)
      }
      if (replay.playing && now - lastStore >= STORE_SYNC_MS) {
        lastStore = now
        setMode('history', new Date(t))
      }
    })
    const offEnd = replay.onEnd(() => {
      seek(new Date(end))
    })
    return () => {
      off()
      offEnd()
    }
  }, [setMode, seek, end])

  const togglePlaying = useCallback(() => {
    if (replay.playing) seek(new Date(replay.t))
    else if (windowLoaded && replay.t < end) setPlaying(true)
  }, [seek, end, windowLoaded])

  // Keyboard transport: space = play/pause, arrows = step, Home/End = edges. Ignored while typing in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!keyboardEnabled || e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.isComposing) return
      // Also guard the existing panel marker, so listener registration order cannot
      // turn the panel's Escape into an exit from history.
      if (document.querySelector('[data-section-panel="open"], [aria-modal="true"]')) return
      const target = e.target instanceof Element ? e.target : null
      if (target?.closest('input, select, textarea, button, a[href], [contenteditable]:not([contenteditable="false"]), [role="button"], [role="slider"]')) return
      if (![' ', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Escape'].includes(e.key)) return
      e.preventDefault()
      if (e.repeat && (e.key === ' ' || e.key === 'Escape')) return
      const current = new Date(replay.t)
      if (e.key === ' ') {
        togglePlaying()
      } else if (e.key === 'ArrowLeft') seek(new Date(current.getTime() - (e.shiftKey ? 10 : STEP_MIN) * 60_000))
      else if (e.key === 'ArrowRight') seek(new Date(current.getTime() + (e.shiftKey ? 10 : STEP_MIN) * 60_000))
      else if (e.key === 'Home') seek(from)
      else if (e.key === 'End') seek(to)
      else if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [seek, from, to, onClose, keyboardEnabled, togglePlaying])

  const btn = 'rounded px-2 py-1 text-sm hover:bg-slate-200 disabled:opacity-40 dark:hover:bg-slate-700'
  const chip = (active: boolean) => `rounded px-2 py-0.5 text-xs ${active ? 'bg-blue-600 text-white' : 'bg-slate-200 hover:bg-slate-300 dark:bg-slate-700 dark:hover:bg-slate-600'}`

  return (
    <div role="region" aria-label="Відтворення історії" data-map-occlusion="true" className="pointer-events-auto absolute bottom-3 left-1/2 z-20 w-[calc(100%-1.5rem)] max-w-2xl min-w-0 max-h-[70dvh] overflow-y-auto -translate-x-1/2 rounded-xl bg-white/95 p-3 text-slate-800 shadow-lg backdrop-blur dark:bg-slate-900/95 dark:text-slate-100">
      <div className="flex flex-wrap items-center gap-2">
        <span className="rounded bg-indigo-600 px-2 py-0.5 text-xs font-medium text-white">ВІДТВОРЕННЯ</span>
        <span className="text-xs text-slate-500">вікно:</span>
        {PRESETS_H.map((h) => (
          <button key={h} className={chip(hours === h)} onClick={() => setHours(h)}>
            {h} год
          </button>
        ))}
        <label className="flex min-w-0 flex-wrap items-center gap-1 text-xs text-slate-500">
          до (Київ)
          <input
            type="datetime-local"
            aria-label="Кінець вікна (час Києва)"
            className="min-w-0 max-w-full rounded border border-slate-300 bg-white px-1 py-0.5 text-xs text-slate-800 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
            value={toKyivInput(to)}
            max={toKyivInput(new Date())}
            onChange={(e) => {
              const d = parseKyivInput(e.target.value)
              e.target.setCustomValidity(d ? '' : 'Вкажіть дійсний час Києва (враховуючи перехід на літній час).')
              if (d) setTo(d > new Date() ? new Date() : d)
            }}
          />
          {!isLiveEdge && (
            <button className="underline" onClick={() => setTo(new Date())}>
              зараз
            </button>
          )}
        </label>
        <span className="ml-auto font-mono text-lg tabular-nums" title={kyivLabel(cursor)}>
          {fmtTime(cursor)} <span className="text-xs text-slate-500">{fmtDate(cursor)}</span>
        </span>
        <button className={btn} aria-label="Вийти з відтворення" title="Вийти з відтворення (Esc)" onClick={onClose}>
          ✕
        </button>
      </div>

      <input
        type="range"
        aria-label="Час відтворення (Київ)"
        aria-valuetext={kyivLabel(cursor)}
        className="mt-2 w-full"
        min={0}
        max={totalMin}
        step="any"
        value={posMin}
        onKeyDown={(e) => {
          const delta = (e.shiftKey ? 10 : STEP_MIN) * 60_000
          const next = e.key === 'Home' ? from : e.key === 'End' ? to
            : e.key === 'ArrowLeft' || e.key === 'ArrowDown' ? new Date(replay.t - delta)
            : e.key === 'ArrowRight' || e.key === 'ArrowUp' ? new Date(replay.t + delta) : null
          if (next) { e.preventDefault(); seek(next) }
        }}
        onChange={(e) => {
          setPlaying(false)
          seek(new Date(from.getTime() + Number(e.target.value) * 60_000))
        }}
      />
      <div className="flex flex-wrap items-center justify-between gap-1">
        <span className="text-[11px] text-slate-500" title={kyivLabel(from)}>
          {fmtTime(from)} {fmtDate(from)}
        </span>
        <span className="order-last flex w-full flex-wrap items-center justify-center gap-1">
          <button className={btn} aria-label="На початок" title="На початок (Home)" onClick={() => seek(from)}>
            ⏮
          </button>
          <button className={btn} aria-label="Назад на 1 хвилину" title="−1 хв (←, Shift: −10)" onClick={() => seek(new Date(replay.t - STEP_MIN * 60_000))}>
            ◀
          </button>
          <button className={`${btn} min-w-24 bg-indigo-600 text-white hover:bg-indigo-700`} title="Пробіл" onClick={togglePlaying} disabled={(cursor.getTime() >= end && !playing) || !windowLoaded}>
            {playing ? '⏸ пауза' : '▶ відтворити'}
          </button>
          <button className={btn} aria-label="Вперед на 1 хвилину" title="+1 хв (→, Shift: +10)" onClick={() => seek(new Date(replay.t + STEP_MIN * 60_000))}>
            ▶
          </button>
          <button className={btn} aria-label="В кінець" title="В кінець (End)" onClick={() => seek(to)}>
            ⏭
          </button>
          <select aria-label="Швидкість відтворення" className="max-w-full rounded border border-slate-300 bg-white px-1 py-0.5 text-xs dark:border-slate-600 dark:bg-slate-800" value={speed} onChange={(e) => setSpeed(Number(e.target.value))} title="Швидкість">
            {SPEEDS.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </select>
          {(loading || !windowLoaded) && <span className="ml-1 text-[11px] text-slate-400">…</span>}
        </span>
        <span className="text-right text-[11px] text-slate-500" title={kyivLabel(to)}>
          {fmtTime(to)} {fmtDate(to)}
        </span>
      </div>
    </div>
  )
}
