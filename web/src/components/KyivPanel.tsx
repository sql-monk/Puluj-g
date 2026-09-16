import { useMemo } from 'react'
import { alertsFor, effectiveLevel, levelTone } from '../lib/alerts'
import { useStore, type Filters } from '../store/useStore'
import Legend from './Legend'
import DataFilterControls from './DataFilterControls'
import type { PublicRoute } from '../public/routes'

type BoolFilter = { [K in keyof Filters]: Filters[K] extends boolean ? K : never }[keyof Filters]

const items: { key: BoolFilter; label: string }[] = [
  { key: 'uav', label: 'БпЛА' },
  { key: 'cruise', label: 'Крилаті ракети' },
  { key: 'ballistic', label: 'Балістика' },
  { key: 'aircraft', label: 'Авіація' },
  { key: 'alerts', label: 'Тривоги' },
  { key: 'activeOnly', label: 'Лише активні' },
]

const RECENT_MS = 60 * 60_000

/** Kyiv page left panel: the ten districts with their alert state and recent message count, plus the class filters. */
export default function KyivPanel({ route, open, onClose }: { route: PublicRoute; open: boolean; onClose: () => void }) {
  const regions = useStore((s) => s.regions)
  const alerts = useStore((s) => s.alerts)
  const targets = useStore((s) => s.targets)
  const filters = useStore((s) => s.filters)
  const setFilter = useStore((s) => s.setFilter)
  const selectedRegionId = useStore((s) => s.selectedRegionId)
  const selectRegion = useStore((s) => s.selectRegion)
  const now = useStore((s) => s.now)

  const kyiv = useMemo(() => regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ'), [regions])
  const districts = useMemo(
    () => regions.filter((r) => r.level === 'District' && r.parentId === kyiv?.id).sort((a, b) => a.name.localeCompare(b.name, 'uk')),
    [regions, kyiv],
  )
  const active = useMemo(() => Object.values(alerts).filter((a) => !a.endedAt), [alerts])
  // The city-wide alert (the level of Kyiv itself); districts are under it too.
  const cityLevel = kyiv ? levelTone(effectiveLevel(active.filter((a) => a.placeId === kyiv.id))) : null
  const since = now.getTime() - RECENT_MS
  const recent = (placeId: number) => targets.filter((o) => new Date(o.observedAt).getTime() >= since && (o.location?.placeId === placeId || o.destination?.placeId === placeId)).length

  const rows = districts.map((d) => {
    // The district's own alerts and the city-wide one, at their effective level (a published level beats none).
    const level = levelTone(effectiveLevel(alertsFor(active, d.id, kyiv ? [kyiv.id] : [])))
    return { id: d.id, name: d.name.replace(' район', ''), level, recent: recent(d.id) }
  })

  const chip = (level: 'red' | 'yellow' | null) =>
    level === 'red' ? (
      <span className="rounded bg-red-600 px-1.5 py-0.5 text-[10px] font-medium text-white">тривога</span>
    ) : level === 'yellow' ? (
      <span className="rounded bg-yellow-400 px-1.5 py-0.5 text-[10px] font-medium text-slate-900">жовтий</span>
    ) : (
      <span className="text-[10px] text-slate-400">—</span>
    )

  return (
    <aside
      data-section-panel={open ? 'open' : 'closed'}
      inert={!open}
      className={`pointer-events-auto absolute z-10 flex max-h-[60vh] w-full flex-col gap-3 overflow-y-auto rounded-t-xl bg-white/95 p-3 shadow-lg backdrop-blur transition-transform md:left-3 md:top-14 md:max-h-[calc(100vh-5rem)] md:w-72 md:rounded-xl dark:bg-slate-900/95 dark:text-slate-100 ${
        open ? 'bottom-0 translate-y-0' : 'pointer-events-none bottom-0 translate-y-full md:-translate-x-[120%] md:translate-y-0'
      }`}
      aria-hidden={!open}
    >
      <div>
        <div className="mb-1 flex items-baseline justify-between">
          <span className="font-medium">Райони Києва</span>
          {kyiv && (
            <button
              className={`text-xs ${selectedRegionId === kyiv.id ? 'font-medium text-blue-700 dark:text-blue-300' : 'text-slate-500 hover:underline'}`}
              onClick={() => selectRegion(kyiv.id)}
            >
              усе місто {cityLevel && chip(cityLevel)}
            </button>
          )}
          <button className="ml-2 rounded px-1.5 py-0.5 text-base leading-none text-slate-400 hover:bg-slate-200 hover:text-slate-700 dark:hover:bg-slate-700 dark:hover:text-slate-200" onClick={onClose} title="Згорнути панель" aria-label="Згорнути панель">
            ‹
          </button>
        </div>
        {rows.length === 0 && <div className="text-xs text-slate-500">Полігони районів ще не завантажені.</div>}
        <ul className="text-sm">
          {rows.map((r) => (
            <li key={r.id}>
              <button
                className={`flex w-full items-center gap-2 rounded px-1.5 py-1 text-left hover:bg-slate-100 dark:hover:bg-slate-800 ${selectedRegionId === r.id ? 'bg-blue-50 font-medium dark:bg-blue-900/40' : ''}`}
                onClick={() => selectRegion(selectedRegionId === r.id ? (kyiv?.id ?? null) : r.id)}
              >
                <span className="flex-1 truncate">{r.name}</span>
                {r.recent > 0 && <span className="rounded-full bg-slate-200 px-1.5 text-[10px] text-slate-700 dark:bg-slate-700 dark:text-slate-200" title="повідомлень за годину">{r.recent}</span>}
                {chip(r.level)}
              </button>
            </li>
          ))}
        </ul>
      </div>
      <DataFilterControls route={route} />
      <div>
        <div className="mb-1 font-medium">Фільтри</div>
        <div className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
          {items.map((it) => (
            <label key={it.key} className="flex items-center gap-2">
              <input type="checkbox" checked={filters[it.key]} onChange={(e) => setFilter(it.key, e.target.checked)} />
              {it.label}
            </label>
          ))}
        </div>
      </div>
      <Legend />
    </aside>
  )
}
