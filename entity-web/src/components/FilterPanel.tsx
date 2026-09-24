import { defaultFilters, useStore, type Filters } from '../store/useStore'
import DataFilterControls from './DataFilterControls'
import FilterPanelShell from './FilterPanelShell'
import type { PublicRoute } from '../public/routes'

type BoolFilter = { [K in keyof Filters]: Filters[K] extends boolean ? K : never }[keyof Filters]
const toggles: { key: BoolFilter; label: string }[] = [
  { key: 'uav', label: 'БпЛА' }, { key: 'cruise', label: 'Крилаті ракети' },
  { key: 'ballistic', label: 'Балістика' }, { key: 'aircraft', label: 'Авіація' },
  { key: 'alerts', label: 'Тривоги' }, { key: 'events', label: 'Події' },
]
interface Props { route: PublicRoute; open: boolean; onClose: () => void; onReplay: () => void; count: number; loading: boolean; error?: string; truncated: boolean }
export default function FilterPanel({ route, open, onClose, onReplay, count, loading, error, truncated }: Props) {
  const filters = useStore((s) => s.filters)
  const setFilter = useStore((s) => s.setFilter)
  const lifetimeOptions = useStore((s) => s.mapConfig.lifetimeOptionsMinutes)
  const hidden = toggles.filter(({ key }) => !filters[key]).length
  return <FilterPanelShell title="Фільтри мапи" open={open} onClose={onClose} subtitle={<span role="status">{loading ? 'Оновлення даних…' : error ? 'Дані можуть бути застарілими' : `${count.toLocaleString('uk-UA')} записів після фільтрів`}</span>}>
    <DataFilterControls route={route} />
    <details className="mt-4 border-t border-slate-200 pt-3 dark:border-slate-700">
      <summary className="cursor-pointer rounded py-1 text-sm font-semibold focus-visible:outline-2 focus-visible:outline-blue-500">Відображення <span className="font-normal text-slate-500 dark:text-slate-400">· {filters.lifetimeMinutes} хв{hidden ? ` · приховано груп: ${hidden}` : ''}{filters.activeOnly ? ' · лише активні' : ''}</span></summary>
      <div className="mt-3 space-y-3 text-sm">
        <fieldset><legend className="mb-2 text-xs text-slate-500 dark:text-slate-400">Групи позначок</legend><div className="grid grid-cols-2 gap-1">{toggles.map(({ key, label }) => <label key={key} className="flex min-h-9 items-center gap-2 rounded-lg bg-slate-50 px-2 dark:bg-slate-800"><input type="checkbox" className="h-4 w-4 accent-blue-600" checked={filters[key]} onChange={(e) => setFilter(key, e.target.checked)} />{label}</label>)}</div></fieldset>
        <label className="flex min-h-9 items-center gap-2"><input type="checkbox" className="h-4 w-4 accent-blue-600" checked={filters.activeOnly} onChange={(e) => setFilter('activeOnly', e.target.checked)} />Лише активні записи зі статусом</label>
        <label className="flex flex-wrap items-center justify-between gap-2">Час життя позначки<select className="rounded-lg border border-slate-300 bg-white px-2 py-2 dark:border-slate-600 dark:bg-slate-800" value={filters.lifetimeMinutes} onChange={(e) => setFilter('lifetimeMinutes', Number(e.target.value))}>{lifetimeOptions.map((m) => <option key={m} value={m}>{m} хв</option>)}</select></label>
        <p className="text-xs leading-relaxed text-slate-500 dark:text-slate-400">Вік позначки рахується від часу події до поточного або історичного моменту. Ці налаштування зберігаються окремо від фільтрів даних.</p>
        <button type="button" className="text-xs font-medium text-blue-700 hover:underline dark:text-blue-300" onClick={() => { toggles.forEach(({ key }) => setFilter(key, defaultFilters[key])); setFilter('activeOnly', defaultFilters.activeOnly); setFilter('lifetimeMinutes', lifetimeOptions.includes(defaultFilters.lifetimeMinutes) ? defaultFilters.lifetimeMinutes : lifetimeOptions[0] ?? defaultFilters.lifetimeMinutes) }}>Скинути відображення</button>
      </div>
    </details>
    <div className="mt-4 space-y-3 border-t border-slate-200 pt-3 dark:border-slate-700">
      <p className="text-xs leading-relaxed text-slate-500 dark:text-slate-400">Мапа показує записи з координатами. У каталозі доступні також записи без координат.{truncated && ' Отримано обмежений зріз даних.'}</p>
      <button type="button" disabled={route.mapMode === 'history'} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium hover:bg-slate-50 disabled:opacity-60 dark:border-slate-600 dark:hover:bg-slate-800" onClick={onReplay}>{route.mapMode === 'history' ? 'Історичний режим увімкнено' : 'Відтворення історії'}</button>
    </div>
  </FilterPanelShell>
}
