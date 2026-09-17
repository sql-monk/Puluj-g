import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { PublicCollectionPageDto, PublicEntityDetailsDto, PublicEntityKind, PublicEntitySummaryDto, PublicEvidenceDto, PublicMessageRefDto, PublicEntityRefDto } from '../api/types'
import type { DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'
import { mapHref } from '../public/mapLink'

export function entityParams(q: DataQuery) {
  return {
    entityKinds: q.entityKinds.join(',') || undefined, eventKinds: q.eventKinds.join(',') || undefined, eventCategories: q.eventCategories.join(',') || undefined,
    categoryIds: q.categoryIds.join(',') || undefined, classIds: q.classIds.join(',') || undefined, familyIds: q.familyIds.join(',') || undefined, modelIds: q.modelIds.join(',') || undefined,
    sourceIds: q.sourceIds.join(',') || undefined, regionId: q.regionId, from: q.from?.toISOString(), to: q.to?.toISOString(), q: q.q, status: q.status, confidence: q.confidence, location: q.location, hasResults: q.hasResults, sort: q.sort, cursor: q.cursor, pageSize: q.pageSize,
  }
}
export function mergeEntityPage(old: PublicEntitySummaryDto[], page: PublicEntitySummaryDto[]) { return [...old, ...page.filter((x) => !old.some((y) => y.kind === x.kind && y.id === x.id))] }
const label: Record<PublicEntityKind, string> = { track: 'Трек', incident: 'Подія', alert: 'Тривога', observation: 'Спостереження' }
const listCache = new Map<string, { items: PublicEntitySummaryDto[]; next?: string; scroll: number }>()
const CACHE_LIMIT = 8
function saveListCache(key: string, value: { items: PublicEntitySummaryDto[]; next?: string; scroll: number }) { listCache.delete(key); listCache.set(key, value); while (listCache.size > CACHE_LIMIT) listCache.delete(listCache.keys().next().value!) }
function href(route: PublicRoute, kind: PublicEntityKind, id: string) { return publicHash({ section: 'entities', detail: { kind, id }, query: route.query }) }
function time(value: string) { return new Date(value).toLocaleString('uk-UA', { dateStyle: 'short', timeStyle: 'short' }) }

export default function EntityCatalogue({ route, query }: { route: PublicRoute; query: DataQuery }) {
  return route.detail ? <EntityDetail route={route} kind={route.detail.kind as PublicEntityKind} id={route.detail.id} /> : <EntityList route={route} query={query} />
}

function EntityList({ route, query }: { route: PublicRoute; query: DataQuery }) {
  const key = route.query.toString(); const cached = listCache.get(key)
  const [items, setItems] = useState<PublicEntitySummaryDto[]>(cached?.items ?? []); const [next, setNext] = useState<string | undefined>(cached?.next); const [loading, setLoading] = useState(!cached); const [error, setError] = useState<string | null>(null); const [notice, setNotice] = useState<string | null>(null)
  const seq = useRef(0)
  const scrollRef = useRef<HTMLElement>(null)
  const load = (more = false) => {
    const current = ++seq.current; setLoading(true); setError(null)
    api.publicEntities(entityParams({ ...query, cursor: more ? next : undefined })).then((page) => {
      if (current !== seq.current) return
      const merged = more ? mergeEntityPage(items, page.items) : page.items; setItems(merged); setNext(page.nextCursor); saveListCache(key, { items: merged, next: page.nextCursor, scroll: scrollRef.current?.scrollTop ?? 0 }); if (page.refreshRecommended) setNotice('Дані каталогу оновилися. Оновіть список, щоб побачити узгоджений зріз.')
    }).catch((e: Error) => current === seq.current && setError(e.message)).finally(() => current === seq.current && setLoading(false))
  }
  useEffect(() => { const entry = listCache.get(key); if (!entry) { setItems([]); setNext(undefined); load(); return }; setItems(entry.items); setNext(entry.next); setLoading(false); requestAnimationFrame(() => { if (scrollRef.current) scrollRef.current.scrollTop = entry.scroll }) }, [key]) // URL is the filter and paging source of truth.
  useEffect(() => () => { const current = listCache.get(key); if (current) saveListCache(key, { ...current, scroll: scrollRef.current?.scrollTop ?? 0 }) }, [key])
  if (error) return <Page scrollRef={scrollRef}><p role="alert">Не вдалося завантажити каталог: {error}</p><button onClick={() => load()}>Повторити</button></Page>
  return <Page scrollRef={scrollRef}><h1>Цілі і події</h1><p className="text-sm text-slate-600 dark:text-slate-300">Каталог за вибраний період. Зв’язки та докази доступні в деталях.</p>{notice && <p role="status">{notice} <button className="underline" onClick={() => load()}>Оновити</button></p>}
    {loading && items.length === 0 ? <p>Завантаження…</p> : items.length === 0 ? <p>За цими фільтрами нічого не знайдено.</p> : <div className="mt-3 space-y-2">{items.map((item) => <a key={`${item.kind}:${item.id}`} href={href(route, item.kind, item.id)} className="block rounded border border-slate-200 p-3 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"><div className="flex gap-2"><b>{label[item.kind]}</b><span>{item.catalogKindName ?? item.catalogKind ?? item.classification ?? 'Без класифікації'}</span><time className="ml-auto text-xs">{time(item.at)}</time></div><div className="mt-1 text-sm">{item.title} · {item.placeName ?? (item.locationKind === 'Unknown' ? 'Локація невідома' : 'Без локації')} · джерел: {item.sourceIds.length} · доказів: {item.totalEvidenceCount}{item.mapAvailable ? ' · мапа доступна' : ''}</div></a>)}</div>}
    {next && <button disabled={loading} className="mt-3 rounded bg-slate-200 px-3 py-1 dark:bg-slate-700" onClick={() => load(true)}>Показати ще</button>}</Page>
}

function EntityDetail({ route, kind, id }: { route: PublicRoute; kind: PublicEntityKind; id: string }) {
  const [data, setData] = useState<PublicEntityDetailsDto | null>(null); const [error, setError] = useState<string | null>(null)
  useEffect(() => { let alive = true; setData(null); setError(null); api.publicEntity(kind, id, route.query.get('dataset') ?? 'live').then((d) => alive && setData(d)).catch((e: Error) => alive && setError(e.message)); return () => { alive = false } }, [kind, id, route.query])
  if (error) return <Page><a href={publicHash({ section: 'entities', query: route.query })}>← До каталогу</a><p role="alert">Сутність недоступна: {error}</p></Page>
  if (!data) return <Page>Завантаження деталей…</Page>
  const entity = data.entity
  return <Page><a href={publicHash({ section: 'entities', query: route.query })}>← До каталогу</a><h1 className="mt-3">{label[entity.kind]}: {entity.title}</h1><p>{entity.catalogKindName ?? entity.classification ?? 'Без класифікації'} · {time(entity.at)} · {entity.placeName ?? 'Без локації'}</p>
    {entity.mapAvailable ? <a href={mapHref(route, { kind: entity.kind, id: entity.id }, entity.map, entity.state === 'active')} className="mt-2 inline-block rounded bg-slate-200 px-3 py-1 dark:bg-slate-700">Показати на мапі</a> : <button disabled title={entity.map.unavailableReason ?? 'Немає підтвердженої геометрії для мапи'} className="mt-2 rounded bg-slate-200 px-3 py-1 disabled:opacity-50 dark:bg-slate-700">Показати на мапі</button>}
    <Paged title={`Докази (${data.evidence.totalCount})`} initial={data.evidence} load={(cursor) => api.publicEvidence(kind, id, cursor, route.query.get('dataset') ?? 'live')} render={(e: PublicEvidenceDto) => `${e.catalogKind ?? 'факт'} · ${time(e.at)} · ${e.relation}${e.score !== undefined ? ` · ${Math.round(e.score * 100)}%` : ''}`} keyOf={(e: PublicEvidenceDto) => `${e.entityId}:${e.observationId ?? e.targetId ?? e.at}`} />
    <Paged title={`Пов’язані повідомлення (${data.messages.totalCount})`} initial={data.messages} load={(cursor) => api.publicMessages(kind, id, cursor, route.query.get('dataset') ?? 'live')} render={(m: PublicMessageRefDto) => <><a className="underline" href={publicHash({ section: 'messages', detail: { id: m.id }, query: route.query })}>Повідомлення {m.id}</a> · {time(m.publishedAt)}</>} keyOf={(m: PublicMessageRefDto) => m.id} />
    <Paged title={`Пов’язані сутності (${data.relations.totalCount})`} initial={data.relations} load={(cursor) => api.publicRelations(kind, id, cursor, route.query.get('dataset') ?? 'live')} render={(r: PublicEntityRefDto) => <><a className="underline" href={href(route, r.kind, r.id)}>{label[r.kind]}: {r.title ?? r.id}</a> · {r.relation}{r.probability !== undefined ? ` · ${Math.round(r.probability * 100)}%` : ''}</>} keyOf={(r: PublicEntityRefDto) => `${r.kind}:${r.id}`} />
  </Page>
}
function Paged<T>({ title, initial, load, render, keyOf }: { title: string; initial: PublicCollectionPageDto<T>; load: (cursor: string) => Promise<PublicCollectionPageDto<T>>; render: (item: T) => React.ReactNode; keyOf: (item: T) => string }) {
  const [items, setItems] = useState(initial.items); const [next, setNext] = useState(initial.nextCursor); const [loading, setLoading] = useState(false); const [error, setError] = useState<string | null>(null); const sequence = useRef(0)
  useEffect(() => { sequence.current += 1; setItems(initial.items); setNext(initial.nextCursor); setError(null) }, [initial])
  const more = () => { if (!next) return; const current = ++sequence.current; setLoading(true); setError(null); load(next).then((page) => { if (current !== sequence.current) return; setItems((old) => [...old, ...page.items.filter((x) => !old.some((y) => keyOf(y) === keyOf(x)))]); setNext(page.nextCursor) }).catch((e: Error) => current === sequence.current && setError(e.message)).finally(() => current === sequence.current && setLoading(false)) }
  return <section><h2 className="mt-4 font-semibold">{title}</h2><ul>{items.map((item) => <li key={keyOf(item)}>{render(item)}</li>)}</ul>{error && <p role="alert">Не вдалося догрузити: {error} <button className="underline" onClick={more}>Повторити</button></p>}{next && <button disabled={loading} className="mt-1 underline" onClick={more}>Показати ще</button>}</section>
}
function Page({ children, scrollRef }: { children: React.ReactNode; scrollRef?: React.RefObject<HTMLElement | null> }) { return <main ref={scrollRef} className="absolute inset-0 z-10 overflow-y-auto bg-slate-100 px-3 pb-8 pt-16 text-slate-900 dark:bg-slate-950 dark:text-slate-100"><div className="mx-auto max-w-5xl rounded-xl bg-white p-5 shadow-sm dark:bg-slate-900">{children}</div></main> }
