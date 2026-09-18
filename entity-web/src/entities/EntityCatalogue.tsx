/* oxlint-disable react/set-state-in-effect -- route/poll changes synchronize this view with the EE HTTP read model. */
import { useCallback, useEffect, useRef, useState } from 'react'
import { entityApi, type EntityItem } from '../api/entityExtractor'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

const cache = new Map<string, { items: EntityItem[]; next?: string; scroll: number }>()
function display(value: unknown): string { return value == null ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value) }
function title(item: EntityItem): string { const preferred = ['label', 'name', 'title', 'place', 'status']; for (const key of preferred) if (item.values[key]) return String(item.values[key]); return `${item.entity} #${item.id}` }
function when(item: EntityItem): string { return item.occurredAt ? new Date(item.occurredAt).toLocaleString('uk-UA', { dateStyle: 'short', timeStyle: 'short' }) : 'час не задано' }

export default function EntityCatalogue({ route, query, refreshKey }: { route: PublicRoute; query: DataQuery; refreshKey?: string }) {
  return route.detail?.kind ? <Detail route={route} kind={route.detail.kind} id={route.detail.id} refreshKey={refreshKey} /> : <List route={route} query={query} refreshKey={refreshKey} />
}

function List({ route, query, refreshKey }: { route: PublicRoute; query: DataQuery; refreshKey?: string }) {
  const key = route.query.toString(); const saved = cache.get(key)
  const [items, setItems] = useState(saved?.items ?? []); const [next, setNext] = useState(saved?.next); const [loading, setLoading] = useState(!saved); const [error, setError] = useState<string>(); const scroll = useRef<HTMLElement>(null)
  const itemsRef = useRef(items); const nextRef = useRef(next)
  useEffect(() => { itemsRef.current = items }, [items])
  useEffect(() => { nextRef.current = next }, [next])
  const load = useCallback(async (more = false) => {
    setLoading(true); setError(undefined)
    try {
      const current = itemsRef.current
      const page = await entityApi.catalogueMany({ kinds: query.entityKinds, q: query.q, sourceIds: query.sourceIds, from: query.from, to: query.to, limit: query.pageSize ?? 100, cursor: more ? nextRef.current : undefined })
      const merged = more ? [...current, ...page.items.filter((item) => !current.some((old) => old.entity === item.entity && old.id === item.id))] : page.items
      setItems(merged); setNext(page.nextCursor); cache.set(key, { items: merged, next: page.nextCursor, scroll: scroll.current?.scrollTop ?? 0 })
    } catch (reason) { setError((reason as Error).message) } finally { setLoading(false) }
  }, [key, query.entityKinds, query.from, query.pageSize, query.q, query.sourceIds, query.to])
  useEffect(() => { const entry = cache.get(key); if (entry) { setItems(entry.items); setNext(entry.next); requestAnimationFrame(() => { if (scroll.current) scroll.current.scrollTop = entry.scroll }) } else void load() }, [key, load])
  useEffect(() => { if (refreshKey) void load() }, [refreshKey, load])
  return <Page scroll={scroll}><div className="flex items-center"><div><h1 className="text-xl font-semibold">Сутності Entity Extractor</h1><p className="text-sm text-slate-500">Конкретні цілі, тривоги, влучання, вибухи, робота ППО та інші налаштовані типи.</p></div><button className="ml-auto rounded bg-slate-200 px-3 py-1 dark:bg-slate-700" onClick={() => void load()}>Оновити</button></div>
    {error && <p className="mt-3 text-red-600" role="alert">Не вдалося завантажити каталог: {error}</p>}
    {loading && items.length === 0 ? <p className="mt-3">Завантаження…</p> : items.length === 0 ? <p className="mt-3">Сутностей не знайдено.</p> : <div className="mt-3 space-y-2">{items.map((item) => <a key={`${item.entity}:${item.id}`} href={publicHash({ section: 'entities', detail: { kind: item.entity, id: item.id }, query: route.query })} className="block rounded border border-slate-200 p-3 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"><div className="flex gap-2"><b>{item.entity}</b><span>{title(item)}</span><time className="ml-auto text-xs">{when(item)}</time></div><div className="text-xs text-slate-500">ID {item.id}{item.rawMessageId ? ` · raw_message ${item.rawMessageId}` : ''}{item.geometry ? ' · є геометрія' : ' · без геометрії'}</div></a>)}</div>}
    {next && <button disabled={loading} className="mt-3 rounded bg-slate-200 px-3 py-1 dark:bg-slate-700" onClick={() => void load(true)}>Показати ще</button>}
  </Page>
}

function Detail({ route, kind, id, refreshKey }: { route: PublicRoute; kind: string; id: string; refreshKey?: string }) {
  const [item, setItem] = useState<EntityItem>(); const [history, setHistory] = useState<EntityItem[]>([]); const [error, setError] = useState<string>()
  useEffect(() => { const controller = new AbortController(); Promise.all([entityApi.detail(kind, id, controller.signal), entityApi.history(kind, id, controller.signal)]).then(([current, related]) => { setItem(current); setHistory(related) }).catch((reason: Error) => reason.name !== 'AbortError' && setError(reason.message)); return () => controller.abort() }, [kind, id, refreshKey])
  return <Page><a className="underline" href={publicHash({ section: 'entities', query: route.query })}>← До каталогу</a>{error ? <p className="mt-3 text-red-600" role="alert">{error}</p> : !item ? <p className="mt-3">Завантаження…</p> : <><h1 className="mt-3 text-xl font-semibold">{title(item)}</h1><p className="text-sm text-slate-500">{item.entity} · ID {item.id} · {when(item)}</p><dl className="mt-4 grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-2">{Object.entries(item.values).map(([field, value]) => <div className="contents" key={field}><dt className="font-medium">{field}</dt><dd className="break-all font-mono text-sm">{display(value)}</dd></div>)}</dl>{history.length > 0 && <section className="mt-5"><h2 className="font-semibold">Пов’язані записи цього повідомлення</h2><ul className="mt-2 space-y-1">{history.map((row) => <li key={`${row.entity}:${row.id}`}>{row.entity} #{row.id} · {when(row)}</li>)}</ul></section>}</>}</Page>
}

function Page({ children, scroll }: { children: React.ReactNode; scroll?: React.RefObject<HTMLElement | null> }) { return <main ref={scroll} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-8 pt-16 text-slate-900 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main> }
