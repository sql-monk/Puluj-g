/* oxlint-disable react/set-state-in-effect -- route/poll changes synchronize this view with the EE HTTP read model. */
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { entityApi, type EntityItem, type EntityPage } from '../api/entityExtractor'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

export interface CatalogueLoadState { loading: boolean; error?: string; lastSuccess?: string }
type OnLoadState = (state: CatalogueLoadState) => void
interface Props { route: PublicRoute; query: DataQuery; refreshKey?: string; onLoadState?: OnLoadState }
interface CachedPage extends EntityPage { scroll: number; lastSuccess: string }
const cache = new Map<string, CachedPage>()
const buttonClass = 'rounded bg-slate-200 px-3 py-1 disabled:opacity-50 dark:bg-slate-700'
function display(value: unknown): string { return value == null ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value) }
function title(item: EntityItem): string { const preferred = ['label', 'name', 'title', 'place', 'status']; for (const key of preferred) if (item.values[key]) return String(item.values[key]); return `${item.entity} #${item.id}` }
function when(item: EntityItem): string { return item.occurredAt ? new Date(item.occurredAt).toLocaleString('uk-UA', { dateStyle: 'short', timeStyle: 'short', timeZone: 'Europe/Kyiv' }) : 'час не задано' }

/** One synchronous request slot covers manual refresh, polling and pagination. Identity
 * checks also protect against transports that settle after ignoring cancellation. */
function useRequest(refreshKey: string | undefined, initial: string | undefined, onLoadState: OnLoadState | undefined, fetchData: (signal: AbortSignal, more: boolean, current: () => boolean) => Promise<void>) {
  const [state, setState] = useState<CatalogueLoadState>({ loading: !initial, lastSuccess: initial })
  const active = useRef<AbortController | null>(null)
  const callback = useRef(onLoadState)
  const fetchRef = useRef(fetchData)
  useLayoutEffect(() => { callback.current = onLoadState; fetchRef.current = fetchData })
  useEffect(() => { callback.current?.(state) }, [state])
  const load = useCallback(async (more = false) => {
    if (active.current) return
    const controller = new AbortController()
    active.current = controller
    const current = () => active.current === controller && !controller.signal.aborted
    setState((old) => ({ ...old, loading: true, error: undefined }))
    try {
      await fetchRef.current(controller.signal, more, current)
      if (current()) setState({ loading: false, lastSuccess: new Date().toISOString() })
    } catch (reason) {
      if (current()) setState((old) => ({ ...old, loading: false, error: reason instanceof Error ? reason.message : String(reason) }))
    } finally {
      controller.abort()
      if (active.current === controller) active.current = null
    }
  }, [])
  const previousRefresh = useRef(refreshKey)
  useEffect(() => {
    // Cached navigation is not a new success; only a changed refresh token fetches again.
    if (!initial || previousRefresh.current !== refreshKey) void load()
    previousRefresh.current = refreshKey
  }, [initial, refreshKey, load])
  useEffect(() => () => { active.current?.abort(); active.current = null }, [])
  return { ...state, load }
}

export default function EntityCatalogue({ route, query, refreshKey, onLoadState }: Props) {
  // Remount immediately on identity changes so an old record/error is never painted
  // under a new URL. Primitive keys avoid refetches from recreated query arrays.
  const key = JSON.stringify([route.query.toString(), query.entityKinds, query.q, query.sourceIds, query.from, query.to, query.pageSize])
  return route.detail?.kind
    ? <Detail key={`${route.detail.kind}:${route.detail.id}`} route={route} kind={route.detail.kind} id={route.detail.id} refreshKey={refreshKey} onLoadState={onLoadState} />
    : <List key={key} cacheKey={key} route={route} query={query} refreshKey={refreshKey} onLoadState={onLoadState} />
}

function List({ route, query, refreshKey, onLoadState, cacheKey }: Props & { cacheKey: string }) {
  const [saved] = useState(() => cache.get(cacheKey))
  const [page, setPage] = useState<EntityPage | undefined>(saved)
  const scroll = useRef<HTMLElement>(null)
  const failedMore = useRef(false)
  const request = useRequest(refreshKey, saved?.lastSuccess, onLoadState, async (signal, more, current) => {
    failedMore.current = more
    const result = await entityApi.catalogueMany({ kinds: query.entityKinds, q: query.q, sourceIds: query.sourceIds, from: query.from, to: query.to, limit: query.pageSize ?? 100, cursor: more ? page?.nextCursor : undefined }, signal)
    if (!current()) return
    const seen = new Set((more ? page?.items ?? [] : []).map((item) => `${item.entity}:${item.id}`))
    const items = [...(more ? page?.items ?? [] : []), ...result.items.filter((item) => { const id = `${item.entity}:${item.id}`; if (seen.has(id)) return false; seen.add(id); return true })]
    const next = { ...result, items, totalCountExact: result.totalCountExact !== false && (!more || page?.totalCountExact !== false) }
    setPage(next)
    cache.delete(cacheKey)
    cache.set(cacheKey, { ...next, scroll: scroll.current?.scrollTop ?? 0, lastSuccess: new Date().toISOString() })
    if (cache.size > 20) cache.delete(cache.keys().next().value!)
  })
  useLayoutEffect(() => {
    const element = scroll.current
    if (!element) return
    element.scrollTop = saved?.scroll ?? 0
    return () => {
      // Navigation can unmount the list before its queued scroll event fires.
      // Read the captured node during layout cleanup, before DOM removal; a
      // passive cleanup or ref lookup can instead see a detached node or null.
      const entry = cache.get(cacheKey)
      if (entry) entry.scroll = element.scrollTop
    }
  }, [cacheKey, saved])
  const rememberScroll = () => { const entry = cache.get(cacheKey); if (entry) entry.scroll = scroll.current?.scrollTop ?? 0 }
  return <Page scroll={scroll} onScroll={rememberScroll}>
    <div className="flex flex-wrap items-start gap-3"><div className="min-w-0 flex-1"><h1 className="text-xl font-semibold">Сутності Entity Extractor</h1><p className="text-sm text-slate-500">Конкретні цілі, тривоги, влучання, вибухи, робота ППО та інші налаштовані типи.</p></div><button disabled={request.loading} className={buttonClass} onClick={() => void request.load()}>Оновити</button></div>
    <RequestStatus state={request} hasData={!!page} retry={() => void request.load(failedMore.current)} />
    {page && <p className="mt-3 text-sm">{page.totalCountExact === false ? `Показано ${page.items.length} записів. Сервер ще не підтверджує загальну кількість без вимкнених треків.` : `Показано ${page.items.length} із ${page.totalCount}`}</p>}
    {page?.items.length === 0 && !request.loading && !request.error && <p className="mt-3">Сутностей не знайдено.</p>}
    {!!page?.items.length && <div className="mt-3 space-y-2">{page.items.map((item) => <a key={`${item.entity}:${item.id}`} href={publicHash({ section: 'entities', detail: { kind: item.entity, id: item.id }, query: route.query })} className="block min-w-0 rounded border border-slate-200 p-3 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"><div className="flex min-w-0 flex-wrap gap-2"><b className="break-all">{item.entity}</b><span className="min-w-0 flex-1 break-words [overflow-wrap:anywhere]">{title(item)}</span><time className="ml-auto text-xs">{when(item)}</time></div><div className="break-all text-xs text-slate-500">ID {item.id}{item.rawMessageId ? ` · raw_message ${item.rawMessageId}` : ''}{item.geometry ? ' · є геометрія' : ' · без геометрії'}</div></a>)}</div>}
    {page?.nextCursor && <button disabled={request.loading} className={`mt-3 ${buttonClass}`} onClick={() => void request.load(true)}>Показати ще</button>}
  </Page>
}

function Detail({ route, kind, id, refreshKey, onLoadState }: { route: PublicRoute; kind: string; id: string; refreshKey?: string; onLoadState?: OnLoadState }) {
  const [data, setData] = useState<{ item: EntityItem; history: EntityItem[] }>()
  const request = useRequest(refreshKey, undefined, onLoadState, async (signal, _more, current) => {
    const [item, history] = await Promise.all([entityApi.detail(kind, id, signal), entityApi.history(kind, id, signal)])
    if (current()) setData({ item, history: history.filter((row) => row.entity !== item.entity || row.id !== item.id) })
  })
  return <Page><div className="flex flex-wrap items-center gap-3"><a className="underline" href={publicHash({ section: 'entities', query: route.query })}>← До каталогу</a><button disabled={request.loading} className={`ml-auto ${buttonClass}`} onClick={() => void request.load()}>Оновити</button></div>
    <RequestStatus state={request} hasData={!!data} retry={() => void request.load()} />
    {data && <><h1 className="mt-3 break-words text-xl font-semibold [overflow-wrap:anywhere]">{title(data.item)}</h1><p className="break-all text-sm text-slate-500">{data.item.entity} · ID {data.item.id} · {when(data.item)}</p><dl className="mt-4 grid grid-cols-[minmax(0,1fr)_minmax(0,2fr)] gap-x-4 gap-y-2">{Object.entries(data.item.values).map(([field, value]) => <div className="contents" key={field}><dt className="break-all font-medium">{field}</dt><dd className="break-all font-mono text-sm">{display(value)}</dd></div>)}</dl>{data.history.length > 0 && <section className="mt-5"><h2 className="font-semibold">Пов’язані записи цього повідомлення</h2><ul className="mt-2 space-y-1">{data.history.map((row) => <li className="break-all" key={`${row.entity}:${row.id}`}><a className="underline" href={publicHash({ section: 'entities', detail: { kind: row.entity, id: row.id }, query: route.query })}>{row.entity} #{row.id} · {when(row)}</a></li>)}</ul></section>}</>}
  </Page>
}

function RequestStatus({ state, hasData, retry }: { state: CatalogueLoadState; hasData: boolean; retry: () => void }) {
  return <>{state.loading && <p className="mt-3" role="status">{hasData ? 'Оновлення… Показано попередні дані.' : 'Завантаження…'}</p>}
    {state.error && <div className="mt-3"><p className="break-words text-red-600" role="alert">Не вдалося завантажити дані: {state.error}</p>{hasData && <p>Показано попередні дані; вони можуть бути застарілими.</p>}<button className={`mt-2 ${buttonClass}`} onClick={retry}>Спробувати ще раз</button></div>}</>
}
function Page({ children, scroll, onScroll }: { children: React.ReactNode; scroll?: React.RefObject<HTMLElement | null>; onScroll?: () => void }) { return <main ref={scroll} onScroll={onScroll} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-28 pt-24 text-slate-900 sm:pt-16 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto min-w-0 max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main> }
