import type * as maplibregl from 'maplibre-gl'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { incidentsApi, type IncidentDetailsDto, type IncidentDto } from '../api/incidents'
import { dateTime, timeAgo } from '../lib/format'
import { placeWindow, useDraggable } from '../lib/useDraggable'
import { useIncidentStore } from '../store/useIncidentStore'
import { useStore } from '../store/useStore'
import { confidenceLabel, precisionLabel, stateLabel } from '../lib/incidentLabels'

interface Props {
  map: maplibregl.Map
  incident: IncidentDto
  anchor: [number, number]
  onClose: () => void
}

const WIDTH = 320
const GAP = 14

/** A static event (§8.5): state, times on both scales, precision, sources, evidence and provenance; no course, ETA or forecast. */
export default function IncidentPopup({ map, incident, anchor, onClose }: Props) {
  const now = useStore((s) => s.now)
  const catalog = useIncidentStore((s) => s.catalog)
  const el = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const [details, setDetails] = useState<IncidentDetailsDto | null>(null)
  const drag = useDraggable(1_000_000 + incident.id)
  const [lon, lat] = anchor
  const kind = catalog.kindOf(incident.kind)

  useEffect(() => {
    let cancelled = false
    incidentsApi
      .details(incident.id)
      .then((d) => {
        if (!cancelled) setDetails(d)
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [incident.id, incident.revision])

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
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  useLayoutEffect(() => {
    const node = el.current
    if (!node || !pos) return
    const box = map.getContainer().getBoundingClientRect()
    const h = node.offsetHeight
    let left = pos.x + GAP
    if (left + WIDTH > box.width - 8) left = Math.max(8, pos.x - GAP - WIDTH)
    let top = pos.y + GAP
    if (top + h > box.height - 8) top = Math.max(52, pos.y - GAP - h)
    placeWindow(node, left, top, drag.offset.current, box, WIDTH)
  })
  void drag.tick

  // Details of the incident on screen only: a stale fetch of the previous selection never shows under the new one.
  const current = details?.incident.id === incident.id ? details : null
  const canonical = current?.observations.find((o) => o.observationId === incident.provenance.canonicalObservationId) ?? current?.observations[0]
  const text = canonical?.rawMessage?.text ?? canonical?.segmentText ?? ''

  return (
    <div
      ref={el}
      role="dialog"
      aria-label={`${kind.name}: ${incident.location?.placeName ?? ''}`}
      className="pointer-events-auto absolute z-20 rounded-lg bg-white/95 text-xs shadow-xl backdrop-blur dark:bg-slate-900/95 dark:text-slate-100"
      style={{ width: WIDTH, left: -9999, top: -9999 }}
      onClick={(e) => e.stopPropagation()}
    >
      <div className={`flex items-start justify-between gap-2 px-2.5 pt-2 ${drag.handleProps.className}`} onPointerDown={drag.handleProps.onPointerDown} title={drag.handleProps.title}>
        <div className="text-sm font-semibold">
          <span aria-hidden="true" className="mr-1 inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ background: kind.color }} />
          {kind.name} · <span className="font-normal text-slate-500">{stateLabel[incident.state] ?? incident.state}</span>
        </div>
        <button className="shrink-0 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <div className="px-2.5 pb-2.5 pt-1">
        <dl className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-0">
          <dt className="text-slate-500">Подія</dt>
          <dd title={dateTime(incident.eventAt)}>{timeAgo(incident.eventAt, now)}</dd>
          <dt className="text-slate-500">Останнє</dt>
          <dd title={dateTime(incident.lastReportedAt)}>{timeAgo(incident.lastReportedAt, now)}</dd>
          <dt className="text-slate-500">Локація</dt>
          <dd className="truncate">
            {incident.location?.placeName ?? '—'}
            {incident.location?.regionName && <span className="text-slate-400"> · {incident.location.regionName}</span>}
          </dd>
          <dt className="text-slate-500">Точність</dt>
          <dd>
            {precisionLabel[incident.location?.precision ?? 'unknown']}
            {incident.location?.accuracyKm !== undefined && incident.location.precision !== 'point' && <span className="text-slate-400"> (±{Math.round(incident.location.accuracyKm)} км)</span>}
          </dd>
          <dt className="text-slate-500">Джерела</dt>
          <dd>
            {incident.sourceCount} · {incident.provenance.observationCount} повідомл. · впевненість {confidenceLabel[incident.confidence] ?? incident.confidence}
          </dd>
          <dt className="text-slate-500">Ревізія</dt>
          <dd>
            {incident.revision}
            {incident.provenance.policyVersion && <span className="text-slate-400"> · {incident.provenance.policyVersion}</span>}
            {incident.closureReason && <span className="text-slate-400"> · {incident.closureReason}</span>}
          </dd>
        </dl>
        <div className="mt-2 border-t border-slate-200 pt-1.5 text-[11px] leading-snug text-slate-700 dark:border-slate-700 dark:text-slate-300">
          {current === null ? 'завантаження…' : text || '(текст повідомлення недоступний)'}
          {canonical?.rawMessage?.url && /^https?:\/\//i.test(canonical.rawMessage.url) && (
            <a className="ml-1 text-blue-600 underline dark:text-blue-400" href={canonical.rawMessage.url} target="_blank" rel="noreferrer">
              джерело
            </a>
          )}
        </div>
        {current && current.revisions.length > 1 && (
          <ol className="mt-1.5 max-h-24 overflow-y-auto border-t border-slate-200 pt-1 text-[11px] text-slate-500 dark:border-slate-700" aria-label="Ревізії">
            {current.revisions.map((r) => (
              <li key={r.revision}>
                #{r.revision} {r.change} · {timeAgo(r.recordedAt, now)} · {r.actor}
                {r.reason && ` · ${r.reason}`}
              </li>
            ))}
          </ol>
        )}
      </div>
    </div>
  )
}
