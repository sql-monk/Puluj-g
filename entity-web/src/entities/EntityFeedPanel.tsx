import type { EntityItem } from '../api/entityExtractor'
import type { PublicRoute } from '../public/routes'
import { publicHash } from '../public/routes'

export default function EntityFeedPanel({ open, onToggle, items, route, loading, error, history = false }: {open:boolean; onToggle:()=>void; items:EntityItem[]; route:PublicRoute; loading:boolean; error?:string; history?:boolean}) {
  if (!open) return <button className="absolute right-3 top-24 z-10 rounded-lg bg-white/95 px-3 py-1.5 text-sm shadow sm:top-14 dark:bg-slate-900/95" onClick={onToggle}>Записи карти ({loading && !items.length ? "…" : items.length})</button>
  return <aside aria-label="Записи карти" data-map-occlusion="true" className={`absolute ${history ? "bottom-[calc(var(--replay-height,16rem)+1.5rem)]" : "bottom-24"} right-3 top-56 z-20 flex w-[calc(100%-1.5rem)] flex-col rounded-xl bg-white/95 shadow-lg sm:top-48 md:w-96 dark:bg-slate-900/95`}>
    <div className="flex items-center justify-between border-b p-3"><h2 className="font-medium">Записи карти ({loading && !items.length ? "…" : items.length})</h2><button onClick={onToggle} aria-label="Згорнути записи карти">Згорнути</button></div>
    <div className="overflow-y-auto p-3 text-sm"><p className="mb-2 text-xs text-slate-500">Сутності після фільтрів поточного знімка, включно із записами без координат.</p>{loading && <p role="status">Оновлення…</p>}{error && <p role="alert" className="text-red-600">Не вдалося оновити дані. Показано попередній знімок.</p>}{!items.length && !loading && !error && <p>За цими фільтрами записів немає.</p>}
      <ul className="space-y-2">{items.map((item) => <li key={`${item.entity}:${item.id}`}><a className="block break-words rounded border p-2 hover:bg-slate-100 dark:hover:bg-slate-800" href={publicHash({section:'entities',detail:{kind:item.entity,id:item.id},query:route.query})}><b>{String(item.values.label ?? item.values.name ?? item.values.title ?? item.entity)}</b><span className="block text-xs text-slate-500">{item.entity} · #{item.id}{item.occurredAt && ` · ${new Date(item.occurredAt).toLocaleString('uk-UA', {timeZone:'Europe/Kyiv'})}`}</span></a></li>)}</ul>
    </div>
  </aside>
}
