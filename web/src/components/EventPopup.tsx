import type * as maplibregl from 'maplibre-gl'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { TargetDto } from '../api/types'
import { dateTime, locationKindLabel, timeAgo } from '../lib/format'
import { placeWindow, useDraggable } from '../lib/useDraggable'
import { useStore } from '../store/useStore'

interface Props {
  map: maplibregl.Map
  event: TargetDto
  anchor: [number, number]
  onClose: () => void
}

const WIDTH = 300
const GAP = 14

const eventLabel: Record<string, string> = {
  ExplosionReport: 'Повідомлення про вибух',
  AirDefenseActivity: 'Повідомлення про роботу ППО',
  TargetCancelled: 'Скасування повідомлення про ціль',
}

/** A source report is an observation, not a track: it deliberately has no direction, ETA or forecast. */
export default function EventPopup({ map, event, anchor, onClose }: Props) {
  const now = useStore((s) => s.now)
  const el = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const drag = useDraggable(event.id)
  const [lon, lat] = anchor
  const text = event.rawMessage.text ?? event.segmentText ?? ''

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

  return (
    <div ref={el} className="pointer-events-auto absolute z-20 rounded-lg bg-white/95 text-xs shadow-xl backdrop-blur dark:bg-slate-900/95 dark:text-slate-100" style={{ width: WIDTH, left: -9999, top: -9999 }} onClick={(e) => e.stopPropagation()}>
      <div className={`flex items-start justify-between gap-2 px-2.5 pt-2 ${drag.handleProps.className}`} onPointerDown={drag.handleProps.onPointerDown} title={drag.handleProps.title}>
        <div className="text-sm font-semibold">{eventLabel[event.eventType] ?? 'Повідомлення про подію'}</div>
        <button className="shrink-0 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <div className="px-2.5 pb-2.5 pt-1">
        <dl className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-0">
          <dt className="text-slate-500">Час</dt>
          <dd title={dateTime(event.observedAt)}>{timeAgo(event.observedAt, now)}</dd>
          <dt className="text-slate-500">Локація</dt>
          <dd className="truncate">{event.location?.placeName ?? '—'} {event.location && <span className="text-slate-400">({locationKindLabel[event.location.kind]})</span>}</dd>
          <dt className="text-slate-500">Джерело</dt>
          <dd className="truncate">{event.source.name}</dd>
        </dl>
        <div className="mt-2 border-t border-slate-200 pt-1.5 text-[11px] leading-snug text-slate-700 dark:border-slate-700 dark:text-slate-300">
          {text || '(текст повідомлення недоступний)'}
        </div>
      </div>
    </div>
  )
}
