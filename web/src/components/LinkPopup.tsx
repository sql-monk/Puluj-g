import type * as maplibregl from 'maplibre-gl'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { MapId, TargetDto } from '../api/types'
import { clock, confidenceLabel } from '../lib/format'
import { placeWindow, useDraggable } from '../lib/useDraggable'
import { usePalette, type SelectedLink } from '../store/useStore'
import Highlight from './Highlight'

interface Props {
  map: maplibregl.Map
  link: SelectedLink
  /** Where the viewer clicked (lon, lat). */
  anchor: [number, number]
  onClose: () => void
}

const WIDTH = 330
const GAP = 14

const kindLabel: Record<string, string> = { Continuation: 'продовження', Split: 'розділення', Merge: 'злиття', Possible: 'можливо', Duplicate: 'дубль' }

/**
 * The link window: opens at a click on a leg between two reports of the selected target's family. Says how probable
 * it is that the object reported earlier (A) is the one reported later (B), the kinematics behind that number, and
 * shows both reports with their messages.
 */
export default function LinkPopup({ map, link, anchor, onClose }: Props) {
  const palette = usePalette()
  const el = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const [pair, setPair] = useState<{ from: TargetDto; to: TargetDto } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<MapId | null>(null)
  const lon = anchor[0]
  const lat = anchor[1]
  // Dragged by its header; the offset is forgotten when another leg is picked.
  const drag = useDraggable(`${link.fromTargetId}>${link.toTargetId}`)

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

  useEffect(() => {
    let cancelled = false
    setPair(null)
    setError(null)
    Promise.all([api.target(link.fromTargetId), api.target(link.toTargetId)])
      .then(([from, to]) => {
        if (!cancelled) setPair({ from, to })
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
    return () => {
      cancelled = true
    }
  }, [link.fromTargetId, link.toTargetId])

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

  // The kinematics of this very link, as the later report keeps them.
  const kin = pair?.to.links?.find((l) => l.targetId === link.fromTargetId && l.direction === 'from')
  const pct = (v: number) => `${Math.round(v * 100)}%`

  const card = (o: TargetDto, title: string) => {
    const color = o.type ? (palette.marker[o.type.displayMode] ?? palette.marker.uav) : '#64748b'
    const text = o.rawMessage.text ?? o.segmentText ?? ''
    const isOpen = open === o.id
    return (
      <div className="rounded border border-slate-200 px-2 py-1.5 dark:border-slate-700">
        <div className="flex items-baseline gap-1.5">
          <span className="text-[10px] font-semibold uppercase tracking-wide text-slate-500">{title}</span>
          <span className="font-mono">{clock(o.observedAt)}</span>
          <span className="inline-block h-2 w-2 shrink-0 rounded-full" style={{ background: color }} />
          <span className="truncate font-medium">{o.type?.label ?? o.eventType}</span>
          <span className="ml-auto shrink-0 text-slate-400">{o.source.name}</span>
        </div>
        <div className="text-slate-500">
          {o.location?.placeName ?? '—'}
          {o.destination && ` → ${o.destination.placeName}`}
          {o.direction && ` · ${Math.round(o.direction.degrees)}°`}
          {o.objectCount && ` · ${o.objectCountIsApproximate ? '~' : ''}${o.objectCount}`}
          {o.type && ` · ${confidenceLabel[o.modelConfidence]}`}
        </div>
        <button className={`mt-0.5 block w-full text-left text-[11px] leading-snug text-slate-800 dark:text-slate-200 ${isOpen ? 'whitespace-pre-wrap' : 'line-clamp-2'}`} onClick={() => setOpen(isOpen ? null : o.id)} title={isOpen ? 'Згорнути' : 'Розгорнути'}>
          {text ? <Highlight text={text} part={o.segmentText ?? ''} folded={!isOpen} /> : '(без тексту)'}
        </button>
        {isOpen && o.rawMessage.url && (
          <a className="text-[11px] text-blue-600 underline dark:text-blue-300" href={o.rawMessage.url} target="_blank" rel="noreferrer">
            оригінал ↗
          </a>
        )}
      </div>
    )
  }

  return (
    <div ref={el} className="pointer-events-auto absolute z-20 flex max-h-[min(70vh,30rem)] flex-col rounded-lg bg-white/95 text-xs shadow-xl backdrop-blur dark:bg-slate-900/95 dark:text-slate-100" style={{ width: WIDTH, left: -9999, top: -9999 }} onClick={(e) => e.stopPropagation()}>
      <div className={`flex items-start justify-between gap-2 px-2.5 pt-2 ${drag.handleProps.className}`} onPointerDown={drag.handleProps.onPointerDown} title={drag.handleProps.title}>
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-sm font-semibold">
            Зв'язок між цілями
            <span className="rounded-full px-1.5 text-[11px] font-bold text-white" style={{ background: palette.selected.uav }} title="Ймовірність, що це той самий об'єкт">
              {pct(link.probability)}
            </span>
          </div>
          <div className="text-[11px] text-slate-500 dark:text-slate-400">
            що об'єкт з А перелетів у Б · {kindLabel[link.kind] ?? link.kind}
            {link.pathProbability < link.probability - 0.005 && ` · шлях від виділеної цілі ${pct(link.pathProbability)}`}
          </div>
        </div>
        <button className="shrink-0 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <div className="min-h-0 flex-1 space-y-1.5 overflow-y-auto px-2.5 pb-2.5 pt-1.5">
        {kin && (
          <div className="text-slate-600 dark:text-slate-300">
            {kin.distanceKm !== undefined && `${Math.round(kin.distanceKm)} км`}
            {kin.minutesApart !== undefined && ` за ${Math.round(kin.minutesApart)} хв`}
            {kin.requiredMinutes !== undefined && ` (потрібно ≈ ${Math.round(kin.requiredMinutes)} хв)`}
            {kin.headingDiffDeg !== undefined && ` · відхилення від курсу ${Math.round(kin.headingDiffDeg)}°`}
          </div>
        )}
        {error && <div className="text-red-600">{error}</div>}
        {!pair && !error && <div className="text-slate-500">Завантаження…</div>}
        {pair && card(pair.from, 'А · раніше')}
        {pair && <div className="text-center text-slate-400">↓</div>}
        {pair && card(pair.to, 'Б · пізніше')}
      </div>
    </div>
  )
}
