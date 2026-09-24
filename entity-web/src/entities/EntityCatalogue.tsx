/* oxlint-disable react/set-state-in-effect -- route/poll changes synchronize this view with the EE HTTP read model. */
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { entityApi, type EntityItem, type EntityPage } from '../api/entityExtractor'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'
import ActiveFilterSummary from '../components/ActiveFilterSummary'
import { detailRows, entityTitle, externalUrl, geometrySummary } from './detailsPresentation'
import { entityLabel } from './presentation'

export interface CatalogueLoadState { loading: boolean; error?: string; lastSuccess?: string }
type OnLoadState = (state: CatalogueLoadState) => void
interface Props { route: PublicRoute; query: DataQuery; refreshKey?: string; onLoadState?: OnLoadState }
interface CachedPage extends EntityPage { scroll: number; lastSuccess: string }
const cache = new Map<string, CachedPage>()
const buttonClass = 'rounded bg-slate-200 px-3 py-1 disabled:opacity-50 dark:bg-slate-700'
function when(item: EntityItem): string { return item.occurredAt ? new Date(item.occurredAt).toLocaleString('uk-UA', { dateStyle: 'short', timeStyle: 'short', timeZone: 'Europe/Kyiv' }) : 'час не задано' }
function time(value: string): string { return new Date(value).toLocaleString('uk-UA', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Europe/Kyiv' }) }

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
    const limit = more ? query.pageSize ?? 100 : Math.max(query.pageSize ?? 100, page?.items.length ?? 0)
    const result = await entityApi.catalogueMany({ kinds: query.entityKinds, q: query.q, sourceIds: query.sourceIds, from: query.from, to: query.to, limit, cursor: more ? page?.nextCursor : undefined }, signal)
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
    <div className="flex flex-wrap items-start gap-3"><div className="min-w-0 flex-1"><h1 className="text-xl font-semibold">Цілі та події</h1><p className="text-sm text-slate-500">Повідомлення про цілі, тривоги, влучання, вибухи, роботу ППО та інші події.</p></div><button disabled={request.loading} className={buttonClass} onClick={() => void request.load()}>Оновити</button></div>
    <ActiveFilterSummary route={route} filters={query} entitySection />
    <RequestStatus state={request} hasData={!!page} retry={() => void request.load(failedMore.current)} />
    {page && <p className="mt-3 text-sm">{page.totalCountExact === false ? `Показано ${page.items.length} записів. Сервер ще не підтверджує точну загальну кількість.` : `Показано ${page.items.length} із ${page.totalCount}`}</p>}
    {page?.searchCandidateLimit && query.q && <p className="mt-1 text-xs text-slate-500">Швидкий пошук перевіряє до {page.searchCandidateLimit.toLocaleString('uk-UA')} найновіших записів кожного типу.</p>}
    {page?.items.length === 0 && !request.loading && !request.error && <p className="mt-3">Сутностей не знайдено.</p>}
    {!!page?.items.length && <div className="mt-3 space-y-2">{page.items.map((item) => <a key={`${item.entity}:${item.id}`} href={publicHash({ section: 'entities', detail: { kind: item.entity, id: item.id }, query: route.query })} className="block min-w-0 rounded border border-slate-200 p-3 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"><div className="flex min-w-0 flex-wrap gap-2"><b>{entityLabel(item.entity)}</b><span className="min-w-0 flex-1 break-words [overflow-wrap:anywhere]">{entityTitle(item)}</span><time className="ml-auto text-xs">{when(item)}</time></div><div className="text-xs text-slate-500">Запис №{item.id} · {item.geometry ? 'місце визначено' : 'без координат'}</div></a>)}</div>}
    {page?.nextCursor && <button disabled={request.loading} className={`mt-3 ${buttonClass}`} onClick={() => void request.load(true)}>Показати ще</button>}
  </Page>
}

function Detail({ route, kind, id, refreshKey, onLoadState }: { route: PublicRoute; kind: string; id: string; refreshKey?: string; onLoadState?: OnLoadState }) {
  const [data, setData] = useState<{ item: EntityItem; history: EntityItem[] }>()
  const request = useRequest(refreshKey, undefined, onLoadState, async (signal, _more, current) => {
    const [item, history] = await Promise.all([entityApi.detail(kind, id, signal), entityApi.history(kind, id, signal)])
    if (current()) setData({ item, history: history.filter((row) => row.entity !== item.entity || row.id !== item.id) })
  })
  const rows = data ? detailRows(data.item) : []
  const originalUrl = externalUrl(data?.item.message?.url)
  const sourceUrl = externalUrl(data?.item.message?.sourceUrl)
  return <Page><div className="flex flex-wrap items-center gap-3"><a className="underline" href={publicHash({ section: 'entities', query: route.query })}>← До каталогу</a><button disabled={request.loading} className={`ml-auto ${buttonClass}`} onClick={() => void request.load()}>Оновити</button></div>
    <RequestStatus state={request} hasData={!!data} retry={() => void request.load()} />
    {data && <><h1 className="mt-3 break-words text-xl font-semibold [overflow-wrap:anywhere]">{entityTitle(data.item)}</h1><p className="text-sm text-slate-500">{entityLabel(data.item.entity)} · запис №{data.item.id} · {when(data.item)}</p>
      {data.item.message && <section className="mt-4 rounded-lg border border-slate-200 p-3 dark:border-slate-700"><h2 className="font-semibold">Джерело повідомлення</h2><p className="mt-1 text-sm"><b>{data.item.message.sourceName}</b> · опубліковано {time(data.item.message.publishedAt)}</p>{data.item.message.text && <p className="mt-2 max-h-80 overflow-y-auto whitespace-pre-wrap break-words">{data.item.message.text}</p>}<div className="mt-2 flex flex-wrap gap-3 text-sm">{originalUrl && <a className="underline" href={originalUrl} target="_blank" rel="noreferrer">Відкрити оригінал ↗</a>}{sourceUrl && <a className="underline" href={sourceUrl} target="_blank" rel="noreferrer">Перейти до джерела ↗</a>}</div></section>}
      <section className="mt-4"><h2 className="font-semibold">Відомості</h2><p className="mt-1 text-sm">{geometrySummary(data.item.geometry)}</p>{rows.length > 0 && <dl className="mt-3 grid grid-cols-[minmax(0,1fr)_minmax(0,2fr)] gap-x-4 gap-y-2">{rows.map((row) => <div className="contents" key={row.key}><dt className="break-words font-medium">{row.label}</dt><dd className="break-words text-sm [overflow-wrap:anywhere]">{row.value}</dd></div>)}</dl>}</section>
      {data.history.length > 0 && <section className="mt-5"><h2 className="font-semibold">Інші події з цього повідомлення</h2><ul className="mt-2 space-y-1">{data.history.map((row) => <li className="break-words" key={`${row.entity}:${row.id}`}><a className="underline" href={publicHash({ section: 'entities', detail: { kind: row.entity, id: row.id }, query: route.query })}>{entityLabel(row.entity)} №{row.id} · {when(row)}</a></li>)}</ul></section>}
      <details className="mt-5 rounded-lg border border-slate-200 p-3 text-sm dark:border-slate-700"><summary className="cursor-pointer font-medium">Технічні дані</summary><pre className="mt-2 max-h-96 overflow-auto whitespace-pre-wrap break-words text-xs">{JSON.stringify({ entity: data.item.entity, id: data.item.id, rawMessageId: data.item.rawMessageId, values: data.item.values, geometry: data.item.geometry }, null, 2)}</pre></details></>}
  </Page>
}

function RequestStatus({ state, hasData, retry }: { state: CatalogueLoadState; hasData: boolean; retry: () => void }) {
  return <>{state.loading && <p className="mt-3" role="status">{hasData ? 'Оновлення… Показано попередні дані.' : 'Завантаження…'}</p>}
    {state.error && <div className="mt-3"><p className="break-words text-red-600" role="alert">Не вдалося завантажити дані: {state.error}</p>{hasData && <p>Показано попередні дані; вони можуть бути застарілими.</p>}<button className={`mt-2 ${buttonClass}`} onClick={retry}>Спробувати ще раз</button></div>}</>
}
function Page({ children, scroll, onScroll }: { children: React.ReactNode; scroll?: React.RefObject<HTMLElement | null>; onScroll?: () => void }) { return <main ref={scroll} onScroll={onScroll} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-28 pt-20 text-slate-900 sm:pt-16 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto min-w-0 max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main> }
