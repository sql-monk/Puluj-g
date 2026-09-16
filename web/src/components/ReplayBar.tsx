import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { TimelineBucketDto } from '../api/types'
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
  return d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
}
function fmtDate(d: Date) {
  return d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' })
}
function toLocalInput(d: Date) {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}

/**
 * Replay mode (spec §20): a window of history, a scrubber over it and a transport. The window's tracks with all their
 * reported positions load once (`/api/replay`); the replay engine then runs the clock on animation frames and the map
 * moves the markers between reports — a time-lapse, not a slideshow of snapshots. The store's `at` follows the clock
 * once a second (alerts, feed cutoff). Works on both map pages.
 */
export default function ReplayBar({ initialWindow, onClose }: { initialWindow: HistoryWindow; onClose: () => void }) {
  const at = useStore((s) => s.at)
  const setMode = useStore((s) => s.setMode)
  const loading = useStore((s) => s.loading)
  const [hours, setHours] = useState(() => (initialWindow.to.getTime() - initialWindow.from.getTime()) / 3600_000)
  const [to, setTo] = useState(() => initialWindow.to)
  const from = useMemo(() => new Date(to.getTime() - hours * 3600_000), [to, hours])
  const [speed, setSpeed] = useState(5)
  const [playing, setPlaying] = useState(false)
  const [buckets, setBuckets] = useState<TimelineBucketDto[]>([])
  const [windowLoaded, setWindowLoaded] = useState(false)
  // The clock readout: the replay engine's instant, throttled; the store's `at` is behind it while playing.
  const [readout, setReadout] = useState<number | null>(null)

  const totalMin = hours * 60
  const cursor = readout !== null ? new Date(readout) : (at ?? to)
  const posMin = Math.min(totalMin, Math.max(0, (cursor.getTime() - from.getTime()) / 60000))
  const isLiveEdge = to.getTime() >= Date.now() - 60_000

  const seek = useCallback(
    (d: Date) => {
      const clamped = new Date(Math.min(to.getTime(), Math.max(from.getTime(), d.getTime())))
      replay.seek(clamped.getTime())
      setMode('history', clamped)
    },
    [from, to, setMode],
  )

  // Entering replay freezes the map at the end of the window; the window's data (tracks with their positions,
  // histogram, feed) loads once per range.
  useEffect(() => {
    setPlaying(false)
    replay.pause()
    setWindowLoaded(false)
    const current = useStore.getState().at
    seek(current && current >= from && current < to ? current : new Date(to.getTime() - 1))
    let cancelled = false
    api
      .replay(from, to)
      .then((d) => {
        if (cancelled) return
        replay.load(d)
        setWindowLoaded(true)
      })
      .catch(() => {
        if (!cancelled) useStore.getState().setError('Не вдалося завантажити вікно відтворення')
      })
    const bucketMin = Math.max(1, Math.round(totalMin / 72))
    api.timeline(from, to, bucketMin).then(setBuckets).catch(() => setBuckets([]))
    api
      .targetsBetween(from, to)
      .then((list) => useStore.getState().setTargets(list))
      .catch(() => useStore.getState().setTargets([]))
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [from, to])

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
      setPlaying(false)
      setMode('history', new Date(replay.t))
    })
    return () => {
      off()
      offEnd()
    }
  }, [setMode])

  // Keyboard transport: space = play/pause, arrows = step, Home/End = edges. Ignored while typing in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName
      if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return
      const current = useStore.getState().at ?? to
      if (e.key === ' ') {
        e.preventDefault()
        setPlaying((p) => !p)
      } else if (e.key === 'ArrowLeft') seek(new Date(current.getTime() - (e.shiftKey ? 10 : STEP_MIN) * 60_000))
      else if (e.key === 'ArrowRight') seek(new Date(current.getTime() + (e.shiftKey ? 10 : STEP_MIN) * 60_000))
      else if (e.key === 'Home') seek(from)
      else if (e.key === 'End') seek(to)
      else if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [seek, from, to, onClose])

  const maxObs = Math.max(1, ...buckets.map((b) => b.targets))
  const btn = 'rounded px-2 py-1 text-sm hover:bg-slate-200 disabled:opacity-40 dark:hover:bg-slate-700'
  const chip = (active: boolean) => `rounded px-2 py-0.5 text-xs ${active ? 'bg-blue-600 text-white' : 'bg-slate-200 hover:bg-slate-300 dark:bg-slate-700 dark:hover:bg-slate-600'}`

  return (
    <div className="pointer-events-auto absolute bottom-3 left-1/2 z-20 w-[min(100%-1.5rem,42rem)] -translate-x-1/2 rounded-xl bg-white/95 p-3 text-slate-800 shadow-lg backdrop-blur dark:bg-slate-900/95 dark:text-slate-100">
      <div className="flex flex-wrap items-center gap-2">
        <span className="rounded bg-indigo-600 px-2 py-0.5 text-xs font-medium text-white">ВІДТВОРЕННЯ</span>
        <span className="text-xs text-slate-500">вікно:</span>
        {PRESETS_H.map((h) => (
          <button key={h} className={chip(hours === h)} onClick={() => setHours(h)}>
            {h} год
          </button>
        ))}
        <label className="ml-1 flex items-center gap-1 text-xs text-slate-500">
          до
          <input
            type="datetime-local"
            className="rounded border border-slate-300 bg-white px-1 py-0.5 text-xs text-slate-800 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
            value={toLocalInput(to)}
            max={toLocalInput(new Date())}
            onChange={(e) => {
              const d = new Date(e.target.value)
              if (!Number.isNaN(d.getTime())) setTo(d > new Date() ? new Date() : d)
            }}
          />
          {!isLiveEdge && (
            <button className="underline" onClick={() => setTo(new Date())}>
              зараз
            </button>
          )}
        </label>
        <span className="ml-auto font-mono text-lg tabular-nums">
          {fmtTime(cursor)} <span className="text-xs text-slate-500">{fmtDate(cursor)}</span>
        </span>
        <button className={btn} title="Вийти з відтворення (Esc)" onClick={onClose}>
          ✕
        </button>
      </div>

      <div className="relative mt-2 h-10" title="повідомлень / тривог у проміжку">
        <div className="absolute inset-0 flex items-end gap-px">
          {buckets.map((b) => (
            <div key={b.from} className="relative flex-1">
              <div className="bg-slate-400/60 dark:bg-slate-500/60" style={{ height: `${Math.max(2, (b.targets / maxObs) * 40)}px` }} />
              {b.alerts > 0 && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-red-500" />}
            </div>
          ))}
        </div>
        <div className="pointer-events-none absolute bottom-0 top-0 w-0.5 bg-indigo-600" style={{ left: `${(posMin / totalMin) * 100}%` }} />
      </div>
      <input
        type="range"
        className="w-full"
        min={0}
        max={totalMin}
        step={STEP_MIN}
        value={Math.round(posMin)}
        onChange={(e) => {
          setPlaying(false)
          seek(new Date(from.getTime() + Number(e.target.value) * 60_000))
        }}
      />
      <div className="flex items-center gap-1">
        <span className="w-24 text-[11px] text-slate-500">
          {fmtTime(from)} {fmtDate(from)}
        </span>
        <span className="mx-auto flex items-center gap-1">
          <button className={btn} title="На початок (Home)" onClick={() => seek(from)}>
            ⏮
          </button>
          <button className={btn} title="−1 хв (←, Shift: −10)" onClick={() => seek(new Date(cursor.getTime() - STEP_MIN * 60_000))}>
            ◀
          </button>
          <button className={`${btn} min-w-24 bg-indigo-600 text-white hover:bg-indigo-700`} title="Пробіл" onClick={() => setPlaying((p) => !p)} disabled={(posMin >= totalMin && !playing) || !windowLoaded}>
            {playing ? '⏸ пауза' : '▶ відтворити'}
          </button>
          <button className={btn} title="+1 хв (→, Shift: +10)" onClick={() => seek(new Date(cursor.getTime() + STEP_MIN * 60_000))}>
            ▶
          </button>
          <button className={btn} title="В кінець (End)" onClick={() => seek(to)}>
            ⏭
          </button>
          <select className="ml-2 rounded border border-slate-300 bg-white px-1 py-0.5 text-xs dark:border-slate-600 dark:bg-slate-800" value={speed} onChange={(e) => setSpeed(Number(e.target.value))} title="Швидкість">
            {SPEEDS.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </select>
          {(loading || !windowLoaded) && <span className="ml-1 text-[11px] text-slate-400">…</span>}
        </span>
        <span className="w-24 text-right text-[11px] text-slate-500">
          {fmtTime(to)} {fmtDate(to)}
        </span>
      </div>
    </div>
  )
}
