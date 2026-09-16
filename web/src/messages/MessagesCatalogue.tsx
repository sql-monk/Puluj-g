import { useEffect, useRef, useState, type ReactNode } from 'react'
import { api } from '../api/client'
import type { PublicCollectionPageDto, PublicEntityRefDto, PublicMessageDetailsDto, PublicMessageResultDto, PublicMessageRevisionDto, PublicMessageSummaryDto, PublicMessageTextChunkDto } from '../api/types'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

type ListCache = { items: PublicMessageSummaryDto[]; next?: string; scroll: number }
const listCache = new Map<string, ListCache>()
const CACHE_LIMIT = 8

/** Translates the shared URL state to the narrower U05 public-message contract. */
export function messageParams(query: DataQuery, cursor?: string): Record<string, string | number | boolean | undefined> {
  return {
    q: query.q,
    sourceIds: query.sourceIds.join(',') || undefined,
    hasResults: query.hasResults,
    outcome: query.status,
    eventKinds: query.eventKinds.join(',') || undefined,
    categoryIds: query.categoryIds.join(',') || undefined,
    classIds: query.classIds.join(',') || undefined,
    familyIds: query.familyIds.join(',') || undefined,
    modelIds: query.modelIds.join(',') || undefined,
    regionId: query.regionId,
    confidence: query.confidence,
    location: query.location === 'known' ? 'located' : query.location === 'missing' ? 'unlocated' : undefined,
    from: query.from?.toISOString(),
    to: query.to?.toISOString(),
    cursor,
    pageSize: query.pageSize,
    dataset: undefined,
  }
}

export function mergeMessagePage(old: PublicMessageSummaryDto[], page: PublicMessageSummaryDto[]) {
  return [...old, ...page.filter((item) => !old.some((existing) => existing.id === item.id))]
}

function saveListCache(key: string, value: ListCache) {
  listCache.delete(key)
  listCache.set(key, value)
  while (listCache.size > CACHE_LIMIT) listCache.delete(listCache.keys().next().value!)
}

function time(value: string) {
  return new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', dateStyle: 'short', timeStyle: 'short', timeZoneName: 'shortOffset' }).format(new Date(value))
}

function safeHref(value?: string) {
  try {
    const url = value ? new URL(value) : null
    return url && (url.protocol === 'https:' || url.protocol === 'http:') ? url.href : null
  } catch { return null }
}

function outcomeLabel(outcome: string) {
  return ({ pending: 'Очікує обробки', awaiting_llm: 'Очікує аналізу', parsed_projection_pending: 'Розбір завершено, результат готується', completed: 'Оброблено', no_facts: 'Результатів не знайдено', failed: 'Помилка обробки', skipped: 'Пропущено', legacy_processed: 'Оброблено (legacy)' } as Record<string, string>)[outcome] ?? outcome
}

function messageHref(route: PublicRoute, id?: string) {
  return publicHash({ section: 'messages', ...(id ? { detail: { id } } : {}), query: route.query })
}

function entityHref(route: PublicRoute, relation: PublicEntityRefDto) {
  return publicHash({ section: 'entities', detail: { kind: relation.kind, id: relation.id }, query: route.query })
}

export default function MessagesCatalogue({ route, query }: { route: PublicRoute; query: DataQuery }) {
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

  const load = (more = false) => {
    const current = ++sequence.current
    abort.current?.abort()
    const controller = new AbortController()
    abort.current = controller
    setLoading(true)
    setError(null)
    api.publicMessagesIndex({ ...messageParams(query, more ? next : undefined), dataset: route.query.get('dataset') ?? undefined }, controller.signal).then((page) => {
      if (current !== sequence.current) return
      const merged = more ? mergeMessagePage(items, page.items) : page.items
      setItems(merged)
      setNext(page.nextCursor)
      saveListCache(key, { items: merged, next: page.nextCursor, scroll: scrollRef.current?.scrollTop ?? 0 })
      setNotice(page.refreshRecommended ? 'Поки ви читали, з’явилися нові дані. Натисніть «Оновити», щоб отримати новий узгоджений зріз.' : null)
    }).catch((e: Error) => {
      if (e.name !== 'AbortError' && current === sequence.current) setError(e.message)
    }).finally(() => { if (current === sequence.current) setLoading(false) })
  }

  useEffect(() => {
    const entry = listCache.get(key)
    if (!entry) {
      setItems([])
      setNext(undefined)
      load()
      return () => abort.current?.abort()
    }
    setItems(entry.items)
    setNext(entry.next)
    setLoading(false)
    requestAnimationFrame(() => { if (scrollRef.current) scrollRef.current.scrollTop = entry.scroll })
    return () => abort.current?.abort()
  // Query state is represented by key; load intentionally reads the matching render closure.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])
  useEffect(() => () => {
    const entry = listCache.get(key)
    if (entry) saveListCache(key, { ...entry, scroll: scrollRef.current?.scrollTop ?? 0 })
  }, [key])

  return <Page scrollRef={scrollRef}>
    <h1>Повідомлення</h1>
    <p className="text-sm text-slate-600 dark:text-slate-300">Початкові повідомлення: один рядок — одна збережена редакція. Тип, область і впевненість відбирають результати розбору, а не сам текст.</p>
    {notice && <p className="mt-2" role="status">{notice} <button className="underline" onClick={() => load()}>Оновити</button></p>}
    {error ? <p role="alert" className="mt-3">Не вдалося завантажити повідомлення: {error} <button className="underline" onClick={() => load()}>Повторити</button></p> : loading && items.length === 0 ? <p className="mt-3">Завантаження…</p> : items.length === 0 ? <p className="mt-3">За цими фільтрами повідомлень не знайдено.</p> : <div className="mt-3 space-y-2">{items.map((item) => <MessageCard key={item.id} route={route} item={item} />)}</div>}
    {next && <button disabled={loading} className="mt-3 rounded bg-slate-200 px-3 py-1 disabled:opacity-50 dark:bg-slate-700" onClick={() => load(true)}>Показати ще</button>}
  </Page>
}

function MessageCard({ route, item }: { route: PublicRoute; item: PublicMessageSummaryDto }) {
  const href = safeHref(item.url)
  const outcome = outcomeLabel(item.outcome)
  return <article className="rounded border border-slate-200 p-3 dark:border-slate-700">
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1"><a className="font-semibold underline" href={messageHref(route, item.id)}>Повідомлення {item.id}</a><span>{item.sourceCode ?? `Джерело #${item.sourceId}`}</span><time className="text-xs text-slate-600 dark:text-slate-300">опубліковано {time(item.publishedAt)}</time></div>
    <p className="mt-1 text-sm">Редакція {item.sourceRevision} · {outcome}{item.outcomeSource === 'legacy' ? ' (legacy)' : ''}</p>
    <p className="mt-1 whitespace-pre-wrap break-words text-sm">{item.excerpt ?? (item.hasText ? 'Текст доступний у деталях.' : 'Структуроване повідомлення без тексту.')}</p>
    <p className="mt-1 text-sm">Результатів: {item.resultCount}{item.matchedResultCount !== item.resultCount ? `, відповідають фільтрам: ${item.matchedResultCount}` : ''} · на мапі: {item.locatedResultCount} доступно, {item.unlocatedResultCount} без локації</p>
    {href ? <a className="mt-1 block break-all text-xs text-blue-700 underline dark:text-blue-300" href={href} target="_blank" rel="noreferrer">Відкрити джерело</a> : item.url ? <p className="mt-1 break-all text-xs text-slate-500">Оригінальне посилання недоступне: {item.url}</p> : null}
  </article>
}

function MessageDetail({ route, id }: { route: PublicRoute; id: string }) {
  const [data, setData] = useState<PublicMessageDetailsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  useEffect(() => {
    let active = true
    setData(null)
    setError(null)
    api.publicMessage(id, route.query.get('dataset') ?? 'live').then((value) => active && setData(value)).catch((e: Error) => active && setError(e.message))
    return () => { active = false }
  }, [id, route.query, reload])
  if (error) return <Page><a className="underline" href={messageHref(route)}>← До повідомлень</a><p role="alert" className="mt-3">Повідомлення недоступне: {error}</p><button className="mt-2 underline" onClick={() => setReload((n) => n + 1)}>Повторити</button></Page>
  if (!data) return <Page>Завантаження деталей повідомлення…</Page>
  const message = data.message
  const href = safeHref(message.url)
  const newest = data.revisions.items.find((revision) => revision.isCurrent)
  return <Page>
    <a className="underline" href={messageHref(route)}>← До повідомлень</a>
    <h1 className="mt-3 break-all">Повідомлення {message.id}</h1>
    <p>{message.sourceCode ?? `Джерело #${message.sourceId}`} · редакція {message.sourceRevision} · {outcomeLabel(message.outcome)}</p>
    <p className="text-sm">Опубліковано: {time(message.publishedAt)}. Отримано: {time(message.receivedAt)}.</p>
    {newest && newest.id !== message.id && <a className="block text-sm underline" href={messageHref(route, newest.id)}>Відкрити найновішу редакцію</a>}
    <MessageMapAction message={message} />
    {href ? <a className="block break-all text-sm text-blue-700 underline dark:text-blue-300" href={href} target="_blank" rel="noreferrer">Джерельний URL</a> : message.url ? <p className="break-all text-sm text-slate-500">Джерельний URL недоступний: {message.url}</p> : null}
    <MessageText key={id} id={id} data={data} />
    <ResultPage route={route} id={id} initial={data.results} />
    {data.directRelations.length > 0 && <section><h2 className="mt-4 font-semibold">Прямі зв’язки</h2><ul>{data.directRelations.map((relation) => <Relation key={`${relation.kind}:${relation.id}:${relation.relation}`} route={route} relation={relation} />)}</ul></section>}
    <RevisionPage route={route} id={id} initial={data.revisions} />
  </Page>
}

function MessageText({ id, data }: { id: string; data: PublicMessageDetailsDto }) {
  const [chunks, setChunks] = useState(data.text ? [data.text] : [])
  const [next, setNext] = useState<string | undefined>(data.textState === 'chunked' ? undefined : undefined)
  const [loading, setLoading] = useState(data.textState === 'chunked')
  const [error, setError] = useState<string | null>(null)
  const loaded = useRef(false)
  const load = (cursor?: string) => {
    setLoading(true)
    setError(null)
    api.publicMessageText(id, cursor).then((chunk: PublicMessageTextChunkDto) => { setChunks((old) => cursor ? [...old, chunk.text ?? ''] : [chunk.text ?? '']); setNext(chunk.nextCursor) }).catch((e: Error) => setError(e.message)).finally(() => setLoading(false))
  }
  useEffect(() => { if (data.textState === 'chunked' && !loaded.current) { loaded.current = true; load() } }, [data.textState])
  if (data.textState === 'structured_no_text') return <section><h2 className="mt-4 font-semibold">Вміст</h2><p>Структуроване повідомлення без тексту. Доступні його метадані та результати розбору.</p></section>
  return <section><h2 className="mt-4 font-semibold">Текст повідомлення</h2>{loading && chunks.length === 0 ? <p>Завантаження тексту…</p> : <pre className="mt-1 whitespace-pre-wrap break-words rounded bg-slate-100 p-3 text-sm dark:bg-slate-800">{chunks.join('')}</pre>}{error && <p role="alert">Не вдалося завантажити текст: {error} <button className="underline" onClick={() => load(next)}>Повторити</button></p>}{next && <button disabled={loading} className="mt-2 underline" onClick={() => load(next)}>Показати продовження тексту</button>}</section>
}

function ResultPage({ route, id, initial }: { route: PublicRoute; id: string; initial: PublicCollectionPageDto<PublicMessageResultDto> }) {
  return <Paged title={`Результати розбору (${initial.totalCount})`} initial={initial} load={(cursor) => api.publicMessageResults(id, cursor, route.query.get('dataset') ?? 'live')} keyOf={(item) => `${item.segmentIndex}:${item.observationId ?? ''}:${item.targetId}`} render={(item) => <Result route={route} item={item} />} />
}

function Result({ route, item }: { route: PublicRoute; item: PublicMessageResultDto }) {
  const observation = item.observationId ? publicHash({ section: 'entities', detail: { kind: 'observation', id: item.observationId }, query: route.query }) : null
  const place = item.map.placeName ?? (item.map.locationKind ? `Локація: ${item.map.locationKind}` : null)
  return <div className="rounded border border-slate-200 p-2 text-sm dark:border-slate-700"><p>Сегмент {item.segmentIndex} · {item.catalogKind ?? item.classification ?? 'Без класифікації'} · {time(item.at)} · впевненість: {item.confidence}</p>{item.segmentText && <p className="mt-1 whitespace-pre-wrap break-words">{item.segmentText}</p>}<p className="mt-1">{place}{place && item.map.precision ? ' · ' : ''}{item.map.precision ? `точність: ${item.map.precision}` : ''}</p>{observation && <a className="mt-1 inline-block underline" href={observation}>Відкрити observation</a>}<p className="mt-1">{item.mapAvailable ? 'Локація доступна для мапи.' : item.map.unavailableReason ?? 'Локація недоступна.'} <button disabled={!item.mapAvailable} title={item.mapAvailable ? 'Перехід до мапи буде підключено в U10' : item.map.unavailableReason ?? 'Немає локації'} className="underline disabled:opacity-50">Показати на мапі</button></p>{item.relations.length > 0 && <ul className="mt-1">{item.relations.map((relation) => <Relation key={`${relation.kind}:${relation.id}:${relation.relation}`} route={route} relation={relation} />)}</ul>}</div>
}

function MessageMapAction({ message }: { message: PublicMessageSummaryDto }) {
  const count = message.locatedResultCount
  const label = count === 1 ? 'Показати на мапі' : `Показати всі доступні (${count})`
  return <p className="mt-2 text-sm">{count > 0 ? <><button disabled title="Наскрізний перехід буде підключено в U10" className="rounded bg-slate-200 px-2 py-1 disabled:opacity-50 dark:bg-slate-700">{label}</button> <span>Керування вибором результатів буде підключено в U10.</span></> : `На мапі немає доступних результатів${message.unlocatedResultCount ? ` (${message.unlocatedResultCount} без локації)` : ''}.`}</p>
}

function Relation({ route, relation }: { route: PublicRoute; relation: PublicEntityRefDto }) {
  return <li><a className="underline" href={entityHref(route, relation)}>{relation.title ?? `${relation.kind} ${relation.id}`}</a> · {relation.relation}{relation.probability !== undefined ? ` · ${Math.round(relation.probability * 100)}%` : ''}</li>
}

function RevisionPage({ route, id, initial }: { route: PublicRoute; id: string; initial: PublicCollectionPageDto<PublicMessageRevisionDto> }) {
  return <Paged title={`Редакції (${initial.totalCount})`} initial={initial} load={(cursor) => api.publicMessageRevisions(id, cursor, route.query.get('dataset') ?? 'live')} keyOf={(item) => item.id} render={(item) => <a className="underline" href={messageHref(route, item.id)}>Редакція {item.sourceRevision} · {time(item.publishedAt)}{item.isCurrent ? ' · найновіша' : ''}</a>} />
}

function Paged<T>({ title, initial, load, keyOf, render }: { title: string; initial: PublicCollectionPageDto<T>; load: (cursor: string) => Promise<PublicCollectionPageDto<T>>; keyOf: (item: T) => string; render: (item: T) => ReactNode }) {
  const [items, setItems] = useState(initial.items)
  const [next, setNext] = useState(initial.nextCursor)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const sequence = useRef(0)
  useEffect(() => { sequence.current += 1; setItems(initial.items); setNext(initial.nextCursor); setError(null) }, [initial])
  const more = () => { if (!next) return; const current = ++sequence.current; setLoading(true); setError(null); load(next).then((page) => { if (current !== sequence.current) return; setItems((old) => [...old, ...page.items.filter((item) => !old.some((existing) => keyOf(existing) === keyOf(item)))]); setNext(page.nextCursor) }).catch((e: Error) => current === sequence.current && setError(e.message)).finally(() => current === sequence.current && setLoading(false)) }
  return <section><h2 className="mt-4 font-semibold">{title}</h2><ul className="mt-1 space-y-2">{items.map((item) => <li key={keyOf(item)}>{render(item)}</li>)}</ul>{error && <p role="alert">Не вдалося дозавантажити: {error} <button className="underline" onClick={more}>Повторити</button></p>}{next && <button disabled={loading} className="mt-2 underline disabled:opacity-50" onClick={more}>Показати ще</button>}</section>
}

function Page({ children, scrollRef }: { children: ReactNode; scrollRef?: React.RefObject<HTMLElement | null> }) {
  return <main ref={scrollRef} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-8 pt-16 text-slate-900 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main>
}
