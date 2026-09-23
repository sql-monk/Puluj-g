import { useStore, type Filters } from '../store/useStore'
import DataFilterControls from './DataFilterControls'
import type { PublicRoute } from '../public/routes'

type BoolFilter = { [K in keyof Filters]: Filters[K] extends boolean ? K : never }[keyof Filters]
const toggles: { key: BoolFilter; label: string }[] = [
  { key: 'uav', label: 'БпЛА' }, { key: 'cruise', label: 'Крилаті ракети' },
  { key: 'ballistic', label: 'Балістика' }, { key: 'aircraft', label: 'Авіація' },
  { key: 'alerts', label: 'Тривоги' }, { key: 'events', label: 'Події' }, { key: 'activeOnly', label: 'Лише активні' },
]
interface Props { route: PublicRoute; open: boolean; onClose: () => void; onReplay: () => void; count: number; loading: boolean; error?: string; truncated: boolean }
export default function FilterPanel({ route, open, onClose, onReplay, count, loading, error, truncated }: Props) {
  const filters = useStore((s) => s.filters)
  const setFilter = useStore((s) => s.setFilter)
  const lifetimeOptions = useStore((s) => s.mapConfig.lifetimeOptionsMinutes)
  return <aside data-section-panel={open ? 'open' : 'closed'} data-map-occlusion="true" inert={!open} aria-hidden={!open}
    className={`pointer-events-auto absolute bottom-0 z-40 flex max-h-[60vh] w-full flex-col gap-4 overflow-y-auto rounded-t-xl bg-white/95 p-3 shadow-lg backdrop-blur md:bottom-auto md:left-3 md:top-14 md:max-h-[calc(100vh-5rem)] md:w-72 md:rounded-xl dark:bg-slate-900/95 dark:text-slate-100 ${open ? 'translate-y-0' : 'pointer-events-none translate-y-full md:-translate-x-[120%] md:translate-y-0'}`}>
    <div className="flex items-start justify-between gap-2"><div><h2 className="font-medium">Відображення на мапі</h2><p className="text-xs text-slate-500" role="status">{loading ? 'Оновлення даних…' : error ? 'Дані можуть бути застарілими' : `${count.toLocaleString('uk-UA')} записів після фільтрів`}</p></div><button aria-label="Згорнути панель" className="rounded px-2 py-1" onClick={onClose}>‹</button></div>
    <p className="text-xs text-slate-500">На мапі показано записи з геометрією. Повний список, зокрема без координат, доступний у каталозі.{truncated && ' Отримано обмежений зріз даних.'}</p>
    <div className="grid grid-cols-2 gap-2 text-sm">{toggles.map(({key,label}) => <label key={key} className="flex items-center gap-2"><input type="checkbox" checked={filters[key]} onChange={(e) => setFilter(key,e.target.checked)} />{label}</label>)}</div>
    <DataFilterControls route={route} />
    <label className="flex flex-wrap items-center justify-between gap-2 text-sm">Час життя позначки<select className="rounded border bg-white px-1 py-0.5 dark:bg-slate-800" value={filters.lifetimeMinutes} onChange={(e) => setFilter('lifetimeMinutes',Number(e.target.value))}>{lifetimeOptions.map((m) => <option key={m} value={m}>{m} хв</option>)}</select></label>
    <p className="text-xs text-slate-500">Вік запису рахується від часу події до поточного або вибраного історичного моменту. «Лише активні» застосовується до записів, які мають статус.</p>
    <button className="rounded border px-2 py-1 text-sm" onClick={onReplay}>Відтворення історії</button>
  </aside>
}
