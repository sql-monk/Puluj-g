import type * as maplibregl from 'maplibre-gl'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api/client'
import type { AlertDto } from '../api/types'
import { alertsFor, ancestorsOf, effectiveLevel, levelTone } from '../lib/alerts'
import { clock, dateTime } from '../lib/format'
import { placeWindow, useDraggable } from '../lib/useDraggable'
import { useStore } from '../store/useStore'

interface Props {
  map: maplibregl.Map
  placeId: number
  /** Where the viewer clicked (lon, lat). */
  anchor: [number, number]
  onClose: () => void
}

const WIDTH = 290
const GAP = 14
const DAY_MS = 24 * 3600_000

const typeLabel: Record<string, string> = { AirRaid: 'повітряна тривога', air_raid: 'повітряна тривога', Artillery: 'артобстріл', UrbanFights: 'вуличні бої', Chemical: 'хімічна', Nuclear: 'радіаційна' }

function duration(ms: number): string {
  const min = Math.max(0, Math.round(ms / 60000))
  if (min < 60) return `${min} хв`
  const h = Math.floor(min / 60)
  return `${h} год ${min % 60} хв`
}

/**
 * The region window: opens at the click on an oblast, raion or city district. With an alert on: its type, level, when
 * it started and how long it has lasted. Without: when the last one ended. Either way: how many alerts and how much
 * alert time in the last 24 h. "An alert on" is hierarchical: on the place, on a place covering it (a city-wide alert
 * over a Kyiv district, an oblast-wide one over a raion) or on a place inside it (a raion or hromada of the oblast).
 */
export default function RegionPopup({ map, placeId, anchor, onClose }: Props) {
  const regions = useStore((s) => s.regions)
  const region = useMemo(() => regions.find((r) => r.id === placeId), [regions, placeId])
  const alerts = useStore((s) => s.alerts)
  // Derived in a memo: a selector returning a fresh array every call would re-render the popup on every store tick.
  const live = useMemo(() => {
    const open = Object.values(alerts).filter((a) => !a.endedAt)
    const regionsById = new Map(regions.map((r) => [r.id, r]))
    return alertsFor(open, placeId, ancestorsOf(placeId, regionsById, open))
  }, [alerts, regions, placeId])
  const now = useStore((s) => s.now)
  const el = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const [history, setHistory] = useState<AlertDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const lon = anchor[0]
  const lat = anchor[1]
  // Dragged by its header; the offset is forgotten when another region is clicked.
  const drag = useDraggable(placeId)

  useEffect(() => {
    const update = () => {
      const p = map.project([lon, lat])
      setPos({ x: p.x, y: p.y })
    }
    update()
    map.on('move', update)
    return () => {
      map.off('move', update)
    }
  }, [map, lon, lat])

  // Reloaded when an alert concerning this place starts or ends (live list changes).
  const liveKey = live.map((a) => a.id).join(',')
  useEffect(() => {
    let cancelled = false
    setError(null)
    api
      .alertsHistory(placeId, 24)
      .then((h) => {
        if (!cancelled) setHistory(h)
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
    return () => {
      cancelled = true
    }
  }, [placeId, liveKey])

  useLayoutEffect(() => {
    const node = el.current
    if (!node || !pos) return
    const box = map.getContainer().getBoundingClientRect()
    const h = node.offsetHeight
    const feedOpen = box.width >= 768 && document.querySelector('[data-feed="open"]') !== null
    const rightLimit = box.width - 8 - (feedOpen ? 24 * 16 + 12 : 0)
    let left = pos.x + GAP
    if (left + WIDTH > rightLimit) left = Math.max(8, pos.x - GAP - WIDTH)
    let top = pos.y + GAP
    if (top + h > box.height - 8) top = Math.max(52, pos.y - GAP - h)
    placeWindow(node, left, top, drag.offset.current, box, WIDTH)
  })
  void drag.tick

  const stats = useMemo(() => {
    if (!history) return null
    const since = now.getTime() - DAY_MS
    // Alerts on the place, covering it and inside it overlap in time: the total is the union of their intervals, not the sum.
    const spans = history
      .map((a) => [Math.max(new Date(a.startedAt).getTime(), since), a.endedAt ? new Date(a.endedAt).getTime() : now.getTime()] as [number, number])
      .filter(([s, e]) => e >= since && e > s)
      .sort((x, y) => x[0] - y[0])
    const count = spans.length
    let total = 0
    let cur: [number, number] | null = null
    for (const [s, e] of spans) {
      if (cur && s <= cur[1]) cur[1] = Math.max(cur[1], e)
      else {
        if (cur) total += cur[1] - cur[0]
        cur = [s, e]
      }
    }
    if (cur) total += cur[1] - cur[0]
    const last = history.filter((a) => a.endedAt).sort((a, b) => new Date(b.endedAt!).getTime() - new Date(a.endedAt!).getTime())[0]
    return { count, total, last }
  }, [history, now])

  // The place's own alert first, then the one covering it, then those inside it (alertsFor's order); the level shown
  // is the effective one over all of them, the colour the map paints this place with.
  const current = live[0] as AlertDto | undefined
  const level = effectiveLevel(live)
  const tone = levelTone(level)
  // Other places among the live alerts (the covering oblast, raions inside): named, "без рівня" for an unlevelled one.
  const others = useMemo(() => {
    const names: string[] = []
    for (const a of live) {
      if (a.placeId === placeId) continue
      const name = a.level === 'Unknown' ? `${a.placeName} (без рівня)` : a.placeName
      if (!names.includes(name)) names.push(name)
    }
    return names
  }, [live, placeId])
  const title = region?.name ?? live.find((a) => a.placeId === placeId)?.placeName ?? history?.find((a) => a.placeId === placeId)?.placeName ?? `Регіон #${placeId}`

  return (
    <div ref={el} className="pointer-events-auto absolute z-20 rounded-lg bg-white/95 text-xs shadow-xl backdrop-blur dark:bg-slate-900/95 dark:text-slate-100" style={{ width: WIDTH, left: -9999, top: -9999 }} onClick={(e) => e.stopPropagation()}>
      <div className={`flex items-start justify-between gap-2 px-2.5 pt-2 ${drag.handleProps.className}`} onPointerDown={drag.handleProps.onPointerDown} title={drag.handleProps.title}>
        <div className="text-sm font-semibold">{title}</div>
        <button className="shrink-0 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <div className="px-2.5 pb-2.5 pt-1">
        {current ? (
          <div className={`mb-2 rounded px-2 py-1.5 ${tone === 'yellow' ? 'bg-yellow-100 text-yellow-900 dark:bg-yellow-900/40 dark:text-yellow-100' : 'bg-red-100 text-red-900 dark:bg-red-900/40 dark:text-red-100'}`}>
            <div className="font-semibold">
              {level === 'Yellow' ? 'Жовтий рівень' : level === 'Red' ? 'Червоний рівень' : 'Тривога'} · {typeLabel[current.alertType] ?? current.alertType}
              {current.placeId !== placeId && <span className="font-normal"> · {current.placeName}</span>}
            </div>
            <div>
              з {clock(current.startedAt)} · триває {duration(now.getTime() - new Date(current.startedAt).getTime())}
            </div>
            {others.length > 0 && (
              <div className="text-[11px] opacity-80">
                також: {others.slice(0, 3).join(', ')}
                {others.length > 3 && ` і ще ${others.length - 3}`}
              </div>
            )}
          </div>
        ) : (
          <div className="mb-2 rounded bg-emerald-50 px-2 py-1.5 text-emerald-900 dark:bg-emerald-900/30 dark:text-emerald-100">
            <div className="font-semibold">Тривоги немає</div>
            {stats?.last ? (
              <div>
                остання: {dateTime(stats.last.startedAt)} – {clock(stats.last.endedAt!)} ({duration(new Date(stats.last.endedAt!).getTime() - new Date(stats.last.startedAt).getTime())})
              </div>
            ) : (
              history && <div>за останню добу не було</div>
            )}
          </div>
        )}
        {error && <div className="text-red-600">{error}</div>}
        {!history && !error && <div className="text-slate-500">Завантаження…</div>}
        {stats && (
          <div className="text-slate-600 dark:text-slate-300">
            За добу: {stats.count} {stats.count === 1 ? 'тривога' : stats.count >= 2 && stats.count <= 4 ? 'тривоги' : 'тривог'} (разом з тими, що накривають, і районними) · під тривогою {duration(stats.total)}
          </div>
        )}
      </div>
    </div>
  )
}
