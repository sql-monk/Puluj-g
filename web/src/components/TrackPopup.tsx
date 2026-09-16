import type * as maplibregl from 'maplibre-gl'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api/client'
import type { MapId, TargetDto, RegionDto, TrackDto } from '../api/types'
import { useEta } from '../eta/useEta'
import { clock, confidenceLabel, directionText, etaConfidence, etaText, fixChain, locationKindLabel, timeAgo } from '../lib/format'
import { placeWindow, useDraggable } from '../lib/useDraggable'
import { hazardKind } from '../map/geojson'
import { effectiveNow, usePalette, useStore } from '../store/useStore'
import Highlight from './Highlight'

interface Props {
  map: maplibregl.Map
  track: TrackDto
  /** Where the viewer clicked (lon, lat): the popup opens there and follows that spot while the map moves. */
  anchor: [number, number] | null
  onDetails: () => void
  onClose: () => void
}

const WIDTH = 300
const GAP = 14
const PREVIEW = 3

/**
 * Compact card at the click point: what it is, how old, where, course, ETA, the badges and the last few messages.
 * "Деталі" opens the left panel with the full provenance, which then follows the selection.
 */
export default function TrackPopup({ map, track, anchor, onDetails, onClose }: Props) {
  const eta = useEta(track)
  const home = useStore((s) => s.home)
  const filters = useStore((s) => s.filters)
  const regions = useStore((s) => s.regions)
  const palette = usePalette()
  const regionsById = useMemo(() => new Map<number, RegionDto>(regions.map((r) => [r.id, r])), [regions])
  const clockNow = effectiveNow(useStore.getState())
  const loc = track.lastLocation
  const el = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const [sheet, setSheet] = useState(() => window.innerWidth < 640)
  // Dragged by its header; the offset is forgotten when another track is selected. Not on the phone sheet.
  const drag = useDraggable(track.id, !sheet)
  const point = anchor ?? loc?.point?.coordinates ?? null
  const lon = point?.[0]
  const lat = point?.[1]

  // Screen position of the anchor, refreshed on every map move / resize.
  useEffect(() => {
    if (lon === undefined || lat === undefined) {
      setPos(null)
      return
    }
    const update = () => {
      const p = map.project([lon, lat])
      setPos({ x: p.x, y: p.y })
      setSheet(window.innerWidth < 640)
    }
    update()
    map.on('move', update)
    map.on('resize', update)
    return () => {
      map.off('move', update)
      map.off('resize', update)
    }
  }, [map, lon, lat])

  // Right of and slightly below the click by default; flipped / clamped so it stays inside the map and off the feed
  // column. A drag moves it from there, still clamped to the map.
  useLayoutEffect(() => {
    const node = el.current
    if (!node || !pos || sheet) return
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

  const hazard = home && filters.highlightTargets ? hazardKind(track, home, clockNow, regionsById) : ''
  const sources = Math.max(track.distinctSourceCount, track.sourceIds.length)

  return (
    <div
      ref={el}
      className={`pointer-events-auto absolute z-20 flex max-h-[min(60vh,26rem)] flex-col rounded-lg bg-white/95 text-xs shadow-xl backdrop-blur dark:bg-slate-900/95 dark:text-slate-100 ${
        sheet ? 'inset-x-2 bottom-2' : ''
      }`}
      style={sheet ? undefined : { width: WIDTH, left: -9999, top: -9999 }}
      onClick={(e) => e.stopPropagation()}
    >
      <div className={`flex items-start justify-between gap-2 px-2.5 pt-2 ${drag.handleProps.className}`} onPointerDown={drag.handleProps.onPointerDown} title={drag.handleProps.title}>
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-1 text-sm font-semibold">
            <span className="inline-block h-2.5 w-2.5 shrink-0 rounded-full" style={{ background: palette.marker[track.type.displayMode] }} />
            <span className="truncate">{track.type.label}</span>
            {track.objectCount && track.objectCount > 1 && (
              <span className="rounded-full border border-slate-700 bg-white px-1 text-[10px] font-bold text-slate-900" title="Кількість цілей у групі">
                ×{track.objectCount}
              </span>
            )}
            <span className="rounded-full bg-blue-800 px-1 text-[10px] font-bold text-white" title="Скільки джерел повідомляють про цю ціль">
              {sources} {plural(sources, 'джерело', 'джерела', 'джерел')}
            </span>
            {track.status !== 'Active' && <span className="rounded bg-slate-200 px-1 text-[10px] font-normal dark:bg-slate-700">{track.status === 'Cancelled' ? 'відбій' : 'закрито'}</span>}
            {hazard && (
              <span className={`rounded px-1 text-[10px] font-semibold text-white ${hazard === 'near' ? 'bg-red-600' : 'bg-orange-500'}`}>
                {hazard === 'near' ? 'поруч з вами' : 'у ваш бік'}
              </span>
            )}
          </div>
          <div className="text-[11px] text-slate-500 dark:text-slate-400">
            {track.type.categoryName}
            {track.type.className ? ` · ${track.type.className}` : ''} · модель: {confidenceLabel[track.modelConfidence]}
          </div>
        </div>
        <button className="shrink-0 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-0 px-2.5 pt-1">
        <dt className="text-slate-500">Останнє</dt>
        <dd>{timeAgo(track.lastSeenAt, clockNow)}</dd>
        <dt className="text-slate-500">Район</dt>
        <dd className="truncate">
          {loc?.placeName ?? '—'} <span className="text-slate-400">({locationKindLabel[loc?.kind ?? 'Unknown']})</span>
        </dd>
        <dt className="text-slate-500">Курс</dt>
        <dd>{directionText(track.direction)}</dd>
        {track.fixes.length >= 2 && (
          <>
            <dt className="text-slate-500">Був</dt>
            <dd className="leading-snug">{fixChain(track)}</dd>
          </>
        )}
        {home && (
          <>
            <dt className="text-slate-500">ETA до вас</dt>
            <dd className={eta?.kind === 'imminent' ? 'font-semibold text-red-600' : ''}>
              {etaText(eta)}
              {etaConfidence(eta) && <span className="text-slate-400"> · {etaConfidence(eta)}</span>}
            </dd>
          </>
        )}
        <dt className="text-slate-500">Трек</dt>
        <dd>
          {confidenceLabel[track.trackConfidence]} · {track.targetCount} повід.
        </dd>
      </dl>
      <Messages trackId={track.id} version={track.targetCount} />
      <div className="px-2.5 pb-2 pt-1">
        <button className="w-full rounded bg-slate-800 px-2 py-1 text-[11px] font-medium text-white hover:bg-slate-700 dark:bg-slate-100 dark:text-slate-900" onClick={onDetails}>
          Деталі та всі повідомлення →
        </button>
      </div>
    </div>
  )
}

/** The newest few messages behind the track; duplicates fold into a counter, the rest is in the details panel. */
function Messages({ trackId, version }: { trackId: MapId; version: number }) {
  const [items, setItems] = useState<TargetDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const sourceFilter = useStore((s) => s.filters.sources)

  // Re-fetched whenever the track gains an target (version = targetCount), debounced because a busy
  // channel can bump it several times a second. An older response still lands if no newer one has.
  const seq = useRef(0)
  const applied = useRef(0)
  useEffect(() => {
    const id = ++seq.current
    const handle = window.setTimeout(() => {
      api
        .track(trackId)
        .then((d) => {
          if (id > applied.current) {
            applied.current = id
            setItems(d.targets)
            setError(null)
          }
        })
        .catch((e: Error) => {
          if (id > applied.current) setError(e.message)
        })
    }, applied.current === 0 ? 0 : 1500)
    return () => window.clearTimeout(handle)
  }, [trackId, version])
  useEffect(() => {
    applied.current = 0
    setItems(null)
  }, [trackId])

  const list = useMemo(() => {
    if (!items) return []
    const dupes = new Map<MapId, number>()
    for (const o of items) if (o.duplicateOfTargetId) dupes.set(o.duplicateOfTargetId, (dupes.get(o.duplicateOfTargetId) ?? 0) + 1)
    return items
      .filter((o) => !o.duplicateOfTargetId)
      .filter((o) => sourceFilter === null || sourceFilter.includes(o.source.id))
      .sort((a, b) => new Date(b.observedAt).getTime() - new Date(a.observedAt).getTime())
      .map((o) => ({ o, dupes: dupes.get(o.id) ?? 0 }))
  }, [items, sourceFilter])
  const more = Math.max(0, list.length - PREVIEW)

  return (
    <div className="mt-1.5 min-h-0 flex-1 overflow-y-auto border-t border-slate-200 dark:border-slate-700">
      {error && <div className="px-2.5 py-1 text-red-600">{error}</div>}
      {!items && !error && <div className="px-2.5 py-1 text-slate-500">Завантаження…</div>}
      <ol>
        {list.slice(0, PREVIEW).map(({ o, dupes }) => {
          const text = o.rawMessage.text ?? o.segmentText ?? ''
          return (
            <li key={o.id} className="border-b border-slate-100 px-2.5 py-1 last:border-b-0 dark:border-slate-800">
              <div className="flex items-baseline gap-1.5">
                <span className="font-mono">{clock(o.observedAt)}</span>
                <span className="truncate font-medium">{o.source.name}</span>
                {dupes > 0 && <span className="shrink-0 text-slate-400">+{dupes}</span>}
                <span className="ml-auto shrink-0 truncate text-slate-400">{o.location?.placeName ?? (o.destination ? `→ ${o.destination.placeName}` : '')}</span>
              </div>
              <div className="line-clamp-2 text-[11px] leading-snug text-slate-700 dark:text-slate-300">{text ? <Highlight text={text} part={o.segmentText ?? ''} folded /> : '(без тексту)'}</div>
            </li>
          )
        })}
        {items && list.length === 0 && <li className="px-2.5 py-1 text-slate-500">Немає повідомлень від вибраних джерел.</li>}
        {more > 0 && <li className="px-2.5 py-1 text-[11px] text-slate-400">ще {more} — у панелі деталей</li>}
      </ol>
    </div>
  )
}

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10
  const m100 = n % 100
  if (m10 === 1 && m100 !== 11) return one
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few
  return many
}
