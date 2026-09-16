import { useMemo } from 'react'
import type { IncidentDto } from '../api/incidents'
import { isOnMap } from '../map/incidentLayer'
import { useIncidentStore } from '../store/useIncidentStore'
import { useStore } from '../store/useStore'
import { stateLabel } from '../lib/incidentLabels'

interface Props {
  /** Opens the popup for an incident (keyboard users reach every visible incident through this list, not the canvas). */
  onOpen: (incident: IncidentDto) => void
}

/**
 * DOM legend and list of the incidents on the map (plan §8.6, P11 accessibility minimum): text labels next to the
 * glyph colour (meaning is never colour alone), per-kind switches (the viewer's filter, separate from catalog
 * visibility), and a button per visible incident for keyboard and screen-reader access. Full keyboard navigation of
 * the map canvas itself is P12.
 */
export default function IncidentLegend({ onOpen }: Props) {
  const catalog = useIncidentStore((s) => s.catalog)
  const byId = useIncidentStore((s) => s.byId)
  const hidden = useIncidentStore((s) => s.hiddenKinds)
  const toggleKind = useIncidentStore((s) => s.toggleKind)
  const now = useStore((s) => s.now)
  const visible = useMemo(
    () =>
      Object.values(byId)
        .filter((i) => isOnMap(i, catalog, now) && !hidden.has(i.kind) && i.location && i.location.precision !== 'unknown')
        .sort((a, b) => b.lastReportedAt.localeCompare(a.lastReportedAt)),
    [byId, catalog, now, hidden],
  )
  const counts = useMemo(() => {
    const c = new Map<string, number>()
    for (const i of visible) c.set(i.kind, (c.get(i.kind) ?? 0) + 1)
    return c
  }, [visible])
  if (catalog.legend.length === 0) return null
  return (
    <details className="pointer-events-auto absolute bottom-8 left-2 z-10 max-w-[18rem] rounded-lg bg-white/95 text-xs shadow backdrop-blur dark:bg-slate-900/95 dark:text-slate-100" open={false}>
      <summary className="cursor-pointer select-none px-2.5 py-1.5 font-semibold" aria-label={`Інциденти на мапі: ${visible.length}`}>
        Інциденти <span className="font-normal text-slate-500">({visible.length})</span>
      </summary>
      <ul className="px-2.5 pb-1" aria-label="Легенда видів подій">
        {catalog.legend.map((k) => (
          <li key={k.code} className="flex items-center gap-2 py-0.5">
            <input id={`kind-${k.code}`} type="checkbox" checked={!hidden.has(k.code)} onChange={() => toggleKind(k.code)} aria-label={`Показувати: ${k.label}`} />
            <span aria-hidden="true" className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: k.color }} />
            <label htmlFor={`kind-${k.code}`} className="flex-1 truncate">
              {k.label} <span className="text-slate-400">· {k.shape}</span>
            </label>
            <span className="tabular-nums text-slate-500">{counts.get(k.code) ?? 0}</span>
          </li>
        ))}
      </ul>
      {visible.length > 0 && (
        <ul className="max-h-40 overflow-y-auto border-t border-slate-200 px-1 py-1 dark:border-slate-700" aria-label="Видимі інциденти">
          {visible.slice(0, 50).map((i) => (
            <li key={i.id}>
              <button type="button" className="w-full truncate rounded px-1.5 py-0.5 text-left hover:bg-slate-100 focus:bg-slate-100 focus:outline-none dark:hover:bg-slate-800 dark:focus:bg-slate-800" onClick={() => onOpen(i)}>
                {catalog.kindOf(i.kind).name} · {i.location?.placeName ?? '—'} · <span className="text-slate-500">{stateLabel[i.state] ?? i.state}</span>
              </button>
            </li>
          ))}
          {visible.length > 50 && <li className="px-1.5 text-slate-400">… ще {visible.length - 50}</li>}
        </ul>
      )}
    </details>
  )
}
