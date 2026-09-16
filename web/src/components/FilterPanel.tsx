import { useStore, type Filters } from '../store/useStore'
import HomeLocationPicker from './HomeLocationPicker'
import Legend from './Legend'

type BoolFilter = { [K in keyof Filters]: Filters[K] extends boolean ? K : never }[keyof Filters]

const classItems: { key: BoolFilter; label: string }[] = [
  { key: 'uav', label: 'БпЛА' },
  { key: 'cruise', label: 'Крилаті ракети' },
  { key: 'ballistic', label: 'Балістика' },
  { key: 'aircraft', label: 'Авіація' },
  { key: 'alerts', label: 'Тривоги' },
  { key: 'events', label: 'Події' },
  { key: 'activeOnly', label: 'Лише активні' },
]

interface Props {
  open: boolean
  onClose: () => void
  picking: boolean
  onPickingChange: (v: boolean) => void
  onReplay: () => void
}

/** Spec §19 left panel: filters, vectors, sources, my location, history, legend. Folds away on every screen size;
 * on phones it is a bottom sheet. */
export default function FilterPanel({ open, onClose, picking, onPickingChange, onReplay }: Props) {
  const filters = useStore((s) => s.filters)
  const setFilter = useStore((s) => s.setFilter)
  const sources = useStore((s) => s.sources)
  const lifetimeOptions = useStore((s) => s.mapConfig.lifetimeOptionsMinutes)
  const home = useStore((s) => s.home)
  const trackCount = useStore((s) => Object.keys(s.tracks).length)
  const alertCount = useStore((s) => Object.keys(s.alerts).length)
  const eventCount = useStore((s) => Object.keys(s.events).length)

  const sourceOn = (id: number) => filters.sources === null || filters.sources.includes(id)
  const toggleSource = (id: number, on: boolean) => {
    const all = sources.map((s) => s.id)
    const current = filters.sources === null ? all : filters.sources
    const next = on ? [...new Set([...current, id])] : current.filter((x) => x !== id)
    // Every source selected is the same as no filter: stored as null so new sources show up by default.
    setFilter('sources', all.every((x) => next.includes(x)) ? null : next)
  }

  const box = 'rounded-md border border-slate-300 px-2 py-1 text-sm dark:border-slate-600'

  return (
    <aside
      data-section-panel={open ? 'open' : 'closed'}
      inert={!open}
      className={`pointer-events-auto absolute z-10 flex max-h-[60vh] w-full flex-col gap-4 overflow-y-auto rounded-t-xl bg-white/95 p-3 shadow-lg backdrop-blur transition-transform md:left-3 md:top-14 md:max-h-[calc(100vh-5rem)] md:w-72 md:rounded-xl dark:bg-slate-900/95 dark:text-slate-100 ${
        open ? 'bottom-0 translate-y-0' : 'pointer-events-none bottom-0 translate-y-full md:-translate-x-[120%] md:translate-y-0'
      }`}
      aria-hidden={!open}
    >
      <div>
        <div className="mb-1 flex items-baseline justify-between">
          <span className="font-medium">Фільтри</span>
          <span className="flex items-center gap-2 text-xs text-slate-500">
            {trackCount} об'єктів · {alertCount} тривог · {eventCount} подій
            <button className="rounded px-1.5 py-0.5 text-base leading-none text-slate-400 hover:bg-slate-200 hover:text-slate-700 dark:hover:bg-slate-700 dark:hover:text-slate-200" onClick={onClose} title="Згорнути панель" aria-label="Згорнути панель">
              ‹
            </button>
          </span>
        </div>
        <div className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
          {classItems.map((it) => (
            <label key={it.key} className="flex items-center gap-2">
              <input type="checkbox" checked={filters[it.key]} onChange={(e) => setFilter(it.key, e.target.checked)} />
              {it.label}
            </label>
          ))}
        </div>
      </div>

      <div>
        <div className="mb-1 font-medium">Вектори руху</div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={filters.forecast} onChange={(e) => setFilter('forecast', e.target.checked)} />
          <span title="Короткий пунктир зі штрихованим сектором попереду кожної цілі з курсом">Прогноз курсу</span>
        </label>
        <div className="mt-1 text-[11px] text-slate-500">Сліди й ймовірні попередники показуються лише для виділеної цілі (клік по маркеру).</div>
        <label className="mt-2 flex items-center justify-between gap-2 text-sm" title="Скільки часу після останнього повідомлення ціль лишається на карті">
          <span>Час життя позначки</span>
          <select className="rounded border border-slate-300 bg-white px-1 py-0.5 text-sm dark:border-slate-600 dark:bg-slate-800" value={filters.lifetimeMinutes} onChange={(e) => setFilter('lifetimeMinutes', Number(e.target.value))}>
            {lifetimeOptions.map((m) => (
              <option key={m} value={m}>
                {m} хв
              </option>
            ))}
          </select>
        </label>
      </div>

      <div>
        <div className="mb-1 flex items-baseline justify-between">
          <span className="font-medium">Джерела</span>
          {filters.sources !== null && (
            <button className="text-xs text-slate-500 underline" onClick={() => setFilter('sources', null)}>
              усі
            </button>
          )}
        </div>
        {sources.length === 0 && <div className="text-xs text-slate-500">Джерела ще не завантажені.</div>}
        <div className="flex flex-col gap-1 text-sm">
          {sources.map((s) => (
            <label key={s.id} className="flex items-center gap-2" title={s.url ?? s.code}>
              <input type="checkbox" checked={sourceOn(s.id)} onChange={(e) => toggleSource(s.id, e.target.checked)} />
              <span className="truncate">{s.name}</span>
              <span className="ml-auto shrink-0 text-[11px] text-slate-400">довіра {Math.round(s.trustLevel * 100)}%</span>
            </label>
          ))}
        </div>
        {filters.sources !== null && <div className="mt-1 text-[11px] text-slate-500">Ціль показується, якщо про неї повідомляє хоча б одне з вибраних джерел.</div>}
      </div>

      <HomeLocationPicker picking={picking} onPickingChange={onPickingChange} />
      {home && (
        <label className="-mt-2 flex items-center gap-2 text-sm">
          <input type="checkbox" checked={filters.highlightTargets} onChange={(e) => setFilter('highlightTargets', e.target.checked)} />
          <span>
            Підсвічувати цілі, небезпечні для моєї точки
            <span className="block text-[11px] text-slate-500">червоне кільце — поруч, помаранчеве — курс у ваш бік</span>
          </span>
        </label>
      )}
      <button className={`${box} border-indigo-300 text-indigo-700 hover:bg-indigo-50 dark:border-indigo-700 dark:text-indigo-300 dark:hover:bg-indigo-950`} onClick={onReplay}>
        ⏱ Відтворення історії
      </button>
      <Legend />
    </aside>
  )
}
