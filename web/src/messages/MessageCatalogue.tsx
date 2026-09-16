import { useEffect, useRef, useState, type ReactNode, type RefObject } from 'react'
import { api } from '../api/client'
import type { PublicCollectionPageDto, PublicMessageDetailsDto, PublicMessageResultDto, PublicMessageRevisionDto, PublicMessageSummaryDto } from '../api/types'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

/** The public API accepts only this allow-listed projection of the shared U03 query. */
export function messageParams(q: DataQuery) {
  // U05 rejects result-derived filters with hasResults=false. Keep a legacy or
  // hand-written URL usable by dropping that contradictory part at the boundary.
  const noResults = q.hasResults === false
  return {
    sourceIds: q.sourceIds.join(',') || undefined, from: q.from?.toISOString(), to: q.to?.toISOString(), q: q.q,
    hasResults: q.hasResults, outcome: q.outcome, eventKinds: noResults ? undefined : q.eventKinds.join(',') || undefined,
    categoryIds: noResults ? undefined : q.categoryIds.join(',') || undefined, classIds: noResults ? undefined : q.classIds.join(',') || undefined,
    familyIds: noResults ? undefined : q.familyIds.join(',') || undefined, modelIds: noResults ? undefined : q.modelIds.join(',') || undefined,
    regionId: noResults ? undefined : q.regionId, location: noResults ? undefined : q.location, confidence: noResults ? undefined : q.confidence, cursor: q.cursor,
    pageSize: q.pageSize, dataset: 'live',
  }
}

export function mergeMessagePage(old: PublicMessageSummaryDto[], page: PublicMessageSummaryDto[]) {
  return [...old, ...page.filter((item) => !old.some((known) => known.id === item.id))]
}

const listCache = new Map<string, { items: PublicMessageSummaryDto[]; next?: string; scroll: number }>()
const CACHE_LIMIT = 8
function saveCache(key: string, value: { items: PublicMessageSummaryDto[]; next?: string; scroll: number }) {
  listCache.delete(key); listCache.set(key, value)
  while (listCache.size > CACHE_LIMIT) listCache.delete(listCache.keys().next().value!)
}
function kyiv(value: string) { return new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)) }
function safeHref(value?: string) { return value && /^https?:\/\//i.test(value) ? value : undefined }
function outcomeLabel(value: string) {
  return ({ pending: 'очікує/обробляється', awaiting_llm: 'очікує розпізнавання', parsed_projection_pending: 'проєкція ще формується', completed: 'завершено', no_facts: 'результатів не знайдено', failed: 'помилка обробки', skipped: 'пропущено', legacy_processed: 'історично оброблено' } as Record<string, string>)[value] ?? `недоступний стан: ${value}`
}
function entityHref(route: PublicRoute, kind: string, id: string) { return publicHash({ section: 'entities', detail: { kind, id }, query: route.query }) }

export default function MessageCatalogue({ route, query }: { route: PublicRoute; query: DataQuery }) {
  return route.detail ? <MessageDetail route={route} id={route.detail.id} /> : <MessageList route={route} query={query} />
}

function MessageList({ route, query }: { route: PublicRoute; query: DataQuery }) {
  const key = route.query.toString()
  const cached = listCache.get(key)
  const [items, setItems] = useState<PublicMessageSummaryDto[]>(cached?.items ?? [])
  const [next, setNext] = useState<string | undefined>(cached?.next)
  const [loading, setLoading] = useState(!cached)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const sequence = useRef(0)
  const abort = useRef<AbortController | null>(null)
  const scrollRef = useRef<HTMLElement>(null)
  const load = () => {
    const current = ++sequence.current
    abort.current?.abort()
    const controller = new AbortController(); abort.current = controller
    setLoading(true); setError(null)
    api.publicMessagesIndex(messageParams(query), controller.signal)
      .then((page) => {
        if (current !== sequence.current) return
        // The cursor is in the URL; a page replaces the previous one instead
        // of retaining an unbounded raw-message history in React state.
        saveCache(key, { items: page.items, next: page.nextCursor, scroll: scrollRef.current?.scrollTop ?? 0 })
        setItems(page.items)
        setNext(page.nextCursor)
        setNotice(page.refreshRecommended ? 'Дані могли оновитися. Оновіть список для нового узгодженого зрізу.' : null)
      })
      .catch((reason: Error) => { if (current === sequence.current && reason.name !== 'AbortError') setError(reason.message) })
      .finally(() => { if (current === sequence.current) setLoading(false) })
  }
  useEffect(() => {
    const entry = listCache.get(key)
    if (entry) {
      setItems(entry.items); setNext(entry.next); setLoading(false)
      requestAnimationFrame(() => { if (scrollRef.current) scrollRef.current.scrollTop = entry.scroll })
    } else { setItems([]); setNext(undefined); load() }
    return () => { abort.current?.abort() }
  // The canonical URL is the list identity; load is intentionally recreated from it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])
  useEffect(() => () => { const entry = listCache.get(key); if (entry) saveCache(key, { ...entry, scroll: scrollRef.current?.scrollTop ?? 0 }) }, [key])
  if (error && items.length === 0) return <Page scrollRef={scrollRef}><h1>Повідомлення</h1><p role="alert">Не вдалося завантажити каталог: {error}</p><button className="underline" onClick={() => load()}>Повторити</button></Page>
  return <Page scrollRef={scrollRef}><h1>Повідомлення</h1><p className="text-sm text-slate-600 dark:text-slate-300">Кожен рядок — окрема збережена редакція повідомлення. Тип, область і класифікація фільтрують його результати розбору.</p>
    {notice && <p role="status" className="mt-2 rounded bg-amber-100 p-2 text-sm dark:bg-amber-950">{notice} <button className="underline" onClick={() => load()}>Оновити</button></p>}
    {loading && items.length === 0 ? <p className="mt-3">Завантаження…</p> : items.length === 0 ? <p className="mt-3">За цими фільтрами повідомлень немає.</p> : <ol className="mt-3 space-y-2">{items.map((item) => <li key={item.id}><a className="block rounded border border-slate-200 p-3 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800" href={publicHash({ section: 'messages', detail: { id: item.id }, query: route.query })}>
      <div className="flex flex-wrap gap-x-3 gap-y-1"><b>{item.sourceCode ?? `Джерело #${item.sourceId}`}</b><time>{kyiv(item.publishedAt)} (Europe/Kyiv)</time><span className="text-sm">редакція {item.sourceRevision || 'без позначки'}</span></div>
      <p className="mt-1 whitespace-pre-wrap text-sm">{item.excerpt ?? (item.hasText ? 'Текст доступний у деталях.' : 'Структуроване повідомлення без тексту.')}</p>
      <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{outcomeLabel(item.outcome)} · результатів: {item.resultCount}{item.matchedResultCount !== item.resultCount ? `, відповідають filters: ${item.matchedResultCount}` : ''} · на мапі: {item.locatedResultCount}, без локації: {item.unlocatedResultCount}</p>
    </a></li>)}</ol>}
    {error && <p role="alert" className="mt-2">Не вдалося завантажити сторінку: {error} <button className="underline" onClick={load}>Повторити</button></p>}
    {next && <button disabled={loading} className="mt-3 rounded bg-slate-200 px-3 py-1 disabled:opacity-50 dark:bg-slate-700" onClick={() => { const nextQuery = new URLSearchParams(route.query); nextQuery.set('cursor', next); window.location.hash = publicHash({ ...route, query: nextQuery }) }}>Наступна сторінка</button>}
  </Page>
}

function MessageDetail({ route, id }: { route: PublicRoute; id: string }) {
  const [data, setData] = useState<PublicMessageDetailsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController(); setData(null); setError(null)
    api.publicMessage(id, route.query.get('dataset') ?? 'live', controller.signal).then(setData).catch((reason: Error) => { if (reason.name !== 'AbortError') setError(reason.message) })
    return () => controller.abort()
  }, [id, route.query])
  if (error) return <Page><a className="underline" href={publicHash({ section: 'messages', query: route.query })}>← До каталогу</a><p role="alert">Повідомлення недоступне: {error}</p></Page>
  if (!data) return <Page>Завантаження деталей…</Page>
  const message = data.message
  const href = safeHref(message.url)
  return <Page><a className="underline" href={publicHash({ section: 'messages', query: route.query })}>← До каталогу</a><h1 className="mt-3">Повідомлення {message.id}</h1>
    <p>{message.sourceCode ?? `Джерело #${message.sourceId}`} · опубліковано {kyiv(message.publishedAt)} · отримано {kyiv(message.receivedAt)} · редакція {message.sourceRevision || 'без позначки'}</p>
    <p className="mt-1 text-sm">{outcomeLabel(message.outcome)}. Це статус публічного outcome, а не висновок із кількості results.</p>
    {href ? <a className="mt-2 inline-block underline" href={href} target="_blank" rel="noreferrer">Оригінальне джерело ↗</a> : message.urlText ? <p className="mt-2 break-all text-sm">Оригінальне посилання недоступне; значення: {message.urlText}</p> : null}
    <MessageText id={id} state={data.textState} initial={data.text} dataset={route.query.get('dataset') ?? 'live'} />
    <Paged title={`Результати (${data.results.totalCount})`} initial={data.results} load={(cursor) => api.publicMessageResults(id, cursor, route.query.get('dataset') ?? 'live')} keyOf={(item) => item.targetId} render={(item: PublicMessageResultDto) => <Result route={route} item={item} />} />
    <Paged title={`Інші редакції (${data.revisions.totalCount})`} initial={data.revisions} load={(cursor) => api.publicMessageRevisions(id, cursor, route.query.get('dataset') ?? 'live')} keyOf={(item) => item.id} render={(item: PublicMessageRevisionDto) => item.isCurrent ? <span>{kyiv(item.publishedAt)} · ця редакція</span> : <a className="underline" href={publicHash({ section: 'messages', detail: { id: item.id }, query: route.query })}>{kyiv(item.publishedAt)} · редакція {item.sourceRevision || 'без позначки'}</a>} />
    {data.directRelations.length > 0 && <section><h2 className="mt-4 font-semibold">Прямі зв’язки</h2><ul>{data.directRelations.map((item) => <li key={`${item.kind}:${item.id}`}><a className="underline" href={entityHref(route, item.kind, item.id)}>{item.kind}: {item.title ?? item.id}</a> · {item.relation}</li>)}</ul></section>}
  </Page>
}

function MessageText({ id, state, initial, dataset }: { id: string; state: string; initial?: string; dataset: string }) {
  const [chunks, setChunks] = useState<string[]>(initial ? [initial] : [])
  const [next, setNext] = useState<string | undefined>()
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(!initial && state === 'chunked')
  const load = (cursor?: string) => {
    setLoading(true); setError(null)
    api.publicMessageText(id, cursor, dataset).then((page) => { if (page.text !== undefined) setChunks([page.text]); setNext(page.nextCursor) }).catch((reason: Error) => setError(reason.message)).finally(() => setLoading(false))
  }
  useEffect(() => { setChunks(initial ? [initial] : []); setNext(undefined); setError(null); if (!initial && state === 'chunked') load() // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, state, initial])
  if (state === 'structured_no_text') return <section><h2 className="mt-4 font-semibold">Вміст</h2><p>Структуроване повідомлення без тексту; metadata та результати доступні нижче.</p></section>
  return <section><h2 className="mt-4 font-semibold">Текст</h2>{loading && chunks.length === 0 ? <p>Завантаження тексту…</p> : <pre className="mt-1 whitespace-pre-wrap break-words rounded bg-slate-100 p-3 font-sans text-sm dark:bg-slate-800">{chunks.join('')}</pre>}{error && <p role="alert">Не вдалося завантажити текст: {error} <button className="underline" onClick={() => load(next)}>Повторити</button></p>}{next && <button className="mt-2 underline" disabled={loading} onClick={() => load(next)}>Показати наступну частину тексту</button>}</section>
}

function Result({ route, item }: { route: PublicRoute; item: PublicMessageResultDto }) {
  return <div className="border-b border-slate-200 py-2 text-sm dark:border-slate-700"><p><a className="underline" href={entityHref(route, 'observation', item.observationId ?? item.targetId)}>{item.catalogKind ?? 'результат'}{item.classification ? ` · ${item.classification}` : ''}</a> · {kyiv(item.at)} · confidence: {item.confidence}</p>{item.segmentText && <p className="whitespace-pre-wrap">{item.segmentText}</p>}<p>Місце: {item.map.placeName ?? 'невідоме'} · точність: {item.map.precision ?? 'невідома'} · {item.mapAvailable ? 'мапа доступна після U10' : item.map.unavailableReason ?? 'мапа недоступна'} <button disabled title={item.mapAvailable ? 'Наскрізний перехід буде додано в U10' : 'Для цього результату немає мапи'} className="ml-1 rounded bg-slate-200 px-2 py-0.5 disabled:opacity-50 dark:bg-slate-700">Показати на мапі</button></p>{item.relations.length > 0 && <p>Зв’язки: {item.relations.map((relation) => <a key={`${relation.kind}:${relation.id}`} className="mr-1 underline" href={entityHref(route, relation.kind, relation.id)}>{relation.kind} {relation.id}</a>)}</p>}</div>
}

function Paged<T>({ title, initial, load, render, keyOf }: { title: string; initial: PublicCollectionPageDto<T>; load: (cursor: string) => Promise<PublicCollectionPageDto<T>>; render: (item: T) => ReactNode; keyOf: (item: T) => string }) {
  const [items, setItems] = useState(initial.items); const [next, setNext] = useState(initial.nextCursor); const [loading, setLoading] = useState(false); const [error, setError] = useState<string | null>(null)
  useEffect(() => { setItems(initial.items); setNext(initial.nextCursor); setError(null) }, [initial])
  const more = () => { if (!next) return; setLoading(true); setError(null); load(next).then((page) => { setItems(page.items); setNext(page.nextCursor) }).catch((reason: Error) => setError(reason.message)).finally(() => setLoading(false)) }
  return <section><h2 className="mt-4 font-semibold">{title}</h2><div>{items.map((item) => <div key={keyOf(item)}>{render(item)}</div>)}</div>{error && <p role="alert">Не вдалося завантажити сторінку: {error} <button className="underline" onClick={more}>Повторити</button></p>}{next && <button className="mt-1 underline" disabled={loading} onClick={more}>Наступна сторінка</button>}</section>
}

function Page({ children, scrollRef }: { children: ReactNode; scrollRef?: RefObject<HTMLElement | null> }) { return <main ref={scrollRef} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-8 pt-16 text-slate-900 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main> }
