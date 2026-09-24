import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { EventKindDto, RegionDto, SourceDto, TaxonomyDto } from '../api/types'
import { kyivLabel, parseKyivInput, toKyivInput } from '../public/kyivTime'
import { normalizeTaxonomyQuery, parseDataQuery, resetDataQuery, serializeDataQuery, type DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'
import { entityApi } from '../api/entityExtractor'
import EntityIcon from './EntityIcon'
import { entityLabel, isRetiredEntity } from '../entities/presentation'

type Dictionaries = { taxonomy: TaxonomyDto; eventKinds: EventKindDto[]; sources: SourceDto[]; regions: RegionDto[]; entityKinds: string[] }

const emptyDictionaries: Dictionaries = { taxonomy: { categories: [] }, eventKinds: [], sources: [], regions: [], entityKinds: [] }
const ids = (event: React.ChangeEvent<HTMLSelectElement>) => [...event.target.selectedOptions].map((option) => Number(option.value)).filter(Number.isFinite)
const strings = (event: React.ChangeEvent<HTMLSelectElement>) => [...event.target.selectedOptions].map((option) => option.value)

/** One shared, section-scoped panel. U06/U08/U09/U12 receive the same URL contract, not copied controls. */
export default function DataFilterControls({ route }: { route: PublicRoute }) {
  const analyticsSection = route.section === 'analytics'
  const targetAnalytics = analyticsSection && route.query.get('metric') !== 'alerts'
  const entitySection = !analyticsSection
  const [dictionary, setDictionary] = useState<Dictionaries>(emptyDictionaries)
  const [dictionaryError, setDictionaryError] = useState<string | null>(null)
  const parsed = useMemo(() => parseDataQuery(route.query), [route.query])
  const normalized = useMemo(() => (targetAnalytics && dictionary.taxonomy.categories.length ? normalizeTaxonomyQuery(parsed.value, dictionary.taxonomy) : { value: parsed.value, removed: [] }), [dictionary.taxonomy, parsed.value, targetAnalytics])
  const filters = normalized.value
  const [draftQ, setDraftQ] = useState(filters.q ?? '')
  const [from, setFrom] = useState(filters.from ? toKyivInput(filters.from) : '')
  const [to, setTo] = useState(filters.to ? toKyivInput(filters.to) : '')
  const [dateError, setDateError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    Promise.all([
      targetAnalytics ? api.taxonomy() : Promise.resolve(emptyDictionaries.taxonomy),
      targetAnalytics ? api.eventKinds() : Promise.resolve([]),
      api.sources(),
      analyticsSection ? api.regions() : Promise.resolve([]),
      entitySection ? entityApi.definitions() : Promise.resolve([]),
    ])
      .then(([taxonomy, eventKinds, sources, regions, definitions]) => {
        if (!active) return
        setDictionary({ taxonomy, eventKinds, sources, regions, entityKinds: definitions.filter((definition) => !isRetiredEntity(definition.entityName, definition.tableName)).map((definition) => definition.entityName) })
        setDictionaryError(null)
      })
      .catch(() => active && setDictionaryError('Довідники ще недоступні; значення з URL збережені без змін.'))
    return () => { active = false }
  }, [analyticsSection, targetAnalytics, entitySection])
  useEffect(() => setDraftQ(filters.q ?? ''), [filters.q])
  useEffect(() => { setFrom(filters.from ? toKyivInput(filters.from) : ''); setTo(filters.to ? toKyivInput(filters.to) : '') }, [filters.from, filters.to])
  const apply = useCallback((next: DataQuery, customPeriod = false) => {
    const clean = targetAnalytics && dictionary.taxonomy.categories.length ? normalizeTaxonomyQuery(next, dictionary.taxonomy).value : next
    const base = new URLSearchParams(route.query)
    if (customPeriod && route.section === 'analytics') base.delete('preset')
    window.location.hash = publicHash({ ...route, query: serializeDataQuery(base, clean) })
  }, [dictionary.taxonomy, route, targetAnalytics])
  useEffect(() => {
    if (!entitySection || draftQ === (filters.q ?? '')) return
    const id = window.setTimeout(() => apply({ ...filters, q: draftQ || undefined, cursor: undefined }), 350)
    return () => window.clearTimeout(id)
  }, [draftQ, filters, apply, entitySection])

  const selection = <T extends keyof Pick<DataQuery, 'categoryIds' | 'classIds' | 'familyIds' | 'modelIds' | 'sourceIds'>>(key: T, value: DataQuery[T]) => apply({ ...filters, [key]: value, cursor: undefined })
  const selectedCategories = filters.categoryIds.length ? new Set(filters.categoryIds) : new Set(dictionary.taxonomy.categories.map((item) => item.id))
  const classes = dictionary.taxonomy.categories.filter((item) => selectedCategories.has(item.id)).flatMap((item) => item.classes)
  const selectedClasses = filters.classIds.length ? new Set(filters.classIds) : new Set(classes.map((item) => item.id))
  const families = classes.filter((item) => selectedClasses.has(item.id)).flatMap((item) => item.families)
  const selectedFamilies = filters.familyIds.length ? new Set(filters.familyIds) : new Set(families.map((item) => item.id))
  const models = families.filter((item) => selectedFamilies.has(item.id)).flatMap((item) => item.models)
  const unknownKinds = filters.eventKinds.filter((code) => !dictionary.eventKinds.some((kind) => kind.code === code))
  const unknownEntityKinds = filters.entityKinds.filter((kind) => !dictionary.entityKinds.includes(kind))
  const knownCategoryIds = new Set(dictionary.taxonomy.categories.map((item) => item.id))
  const knownClassIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.map((child) => child.id)))
  const knownFamilyIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.flatMap((child) => child.families.map((family) => family.id))))
  const knownModelIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.flatMap((child) => child.families.flatMap((family) => family.models.map((model) => model.id)))))
  const unknownSources = filters.sourceIds.filter((id) => !dictionary.sources.some((source) => source.id === id))
  const unknownRegion = analyticsSection && filters.regionId && !dictionary.regions.some((region) => region.id === filters.regionId) ? filters.regionId : undefined
  const box = 'mt-1 min-w-0 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus-visible:outline-2 focus-visible:outline-blue-500 dark:border-slate-600 dark:bg-slate-800'
  const toggleKind = (kind: string) => apply({ ...filters, entityKinds: filters.entityKinds.includes(kind) ? filters.entityKinds.filter((value) => value !== kind) : [...filters.entityKinds, kind], cursor: undefined })
  const toggleSource = (id: number) => selection('sourceIds', filters.sourceIds.includes(id) ? filters.sourceIds.filter((value) => value !== id) : [...filters.sourceIds, id])

  const applyDates = () => {
    const start = parseKyivInput(from)
    const end = parseKyivInput(to)
    if (!start || !end || start >= end) {
      setDateError('Вкажіть коректний інтервал Europe/Kyiv; неіснуюча DST-година не приймається.')
      return
    }
    setDateError(null)
    const next = { ...filters, from: start, to: end, cursor: undefined }
    const mapMode = route.section === 'map' && route.mapMode === 'live' && end.getTime() < Date.now() - 60_000 ? 'history' : route.mapMode
    const base = new URLSearchParams(route.query)
    if (route.section === 'analytics') base.delete('preset')
    window.location.hash = publicHash({ ...route, mapMode, query: serializeDataQuery(base, next) })
  }
  const preset = (hours: number) => {
    const end = new Date(Math.floor(Date.now() / 300_000) * 300_000)
    apply({ ...filters, from: new Date(end.getTime() - hours * 3600_000), to: end, cursor: undefined }, true)
  }
  return <div className="flex flex-col gap-3 text-sm">
    <div className="flex items-center justify-between"><strong className="text-xs uppercase tracking-wide text-slate-500 dark:text-slate-400">Фільтри даних</strong><button type="button" className="rounded py-1 text-xs font-medium text-blue-700 hover:underline dark:text-blue-300" onClick={() => apply(resetDataQuery(filters))}>Скинути фільтри даних</button></div>
    <Chips filters={filters} onChange={apply} entitySection={entitySection} targetAnalytics={targetAnalytics} />
    {(parsed.errors.length > 0 || dictionaryError || normalized.removed.length > 0 || unknownSources.length > 0 || unknownRegion) && <p className="rounded bg-amber-100 p-2 text-xs text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{[...parsed.errors, dictionaryError, normalized.removed.length ? `Прибрано несумісні: ${normalized.removed.join(', ')}` : '', unknownSources.length ? `Невідомі/історичні джерела: ${unknownSources.join(', ')}` : '', unknownRegion ? `Невідома/історична область: ${unknownRegion}` : ''].filter(Boolean).join(' ')}</p>}
    {entitySection && <><label>Пошук<input className={box} value={draftQ} onChange={(event) => setDraftQ(event.target.value)} placeholder="Текст або код" /></label>
    <fieldset aria-label="Тип сутності" className="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><legend className="px-1 font-semibold">Тип сутності</legend><p className="mb-2 text-xs text-slate-500 dark:text-slate-400">{filters.entityKinds.length ? `Обрано: ${filters.entityKinds.length}` : 'Усі типи · оберіть потрібні'}</p><div className="max-h-44 space-y-1 overflow-y-auto">{[...dictionary.entityKinds, ...unknownEntityKinds].map((kind) => <label key={kind} className="flex min-h-9 cursor-pointer items-center gap-2 rounded-lg px-1 hover:bg-slate-50 dark:hover:bg-slate-800"><input type="checkbox" className="h-4 w-4 shrink-0 accent-blue-600" checked={filters.entityKinds.includes(kind)} onChange={() => toggleKind(kind)} /><EntityIcon entity={kind} className="h-6 w-6 shrink-0" /><span className="min-w-0 break-words">{isRetiredEntity(kind) ? 'Треки вимкнено' : dictionary.entityKinds.includes(kind) ? entityLabel(kind) : `Недоступна сутність: ${kind}`}</span></label>)}</div>{filters.entityKinds.some((kind) => isRetiredEntity(kind)) && <p className="mt-2 text-xs text-amber-700 dark:text-amber-300">Збережений фільтр треків не показує записів. Зніміть його, щоб обрати інші типи.</p>}</fieldset></>}
    {targetAnalytics && <><label>Вид події<select multiple className={box} value={filters.eventKinds} onChange={(event) => apply({ ...filters, eventKinds: strings(event), cursor: undefined })}>{dictionary.eventKinds.map((kind) => <option key={kind.code} value={kind.code}>{kind.nameUk} · {kind.code}</option>)}{unknownKinds.map((code) => <option key={code} value={code}>Недоступний historical code: {code}</option>)}</select></label>
    <label>Категорія події<select multiple className={box} value={filters.eventCategories} onChange={(event) => apply({ ...filters, eventCategories: strings(event), cursor: undefined })}>{['target', 'alert', 'event', 'info'].map((category) => <option key={category} value={category}>{category}</option>)}</select></label>
    <label>Категорія<select multiple className={box} value={filters.categoryIds.map(String)} onChange={(event) => selection('categoryIds', ids(event))}>{dictionary.taxonomy.categories.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.categoryIds.filter((id) => !knownCategoryIds.has(id)).map((id) => <option key={id} value={id}>Historical category #{id}</option>)}</select></label>
    <label>Клас<select multiple className={box} value={filters.classIds.map(String)} onChange={(event) => selection('classIds', ids(event))}>{classes.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.classIds.filter((id) => !knownClassIds.has(id)).map((id) => <option key={id} value={id}>Historical class #{id}</option>)}</select></label>
    <label>Сімейство<select multiple className={box} value={filters.familyIds.map(String)} onChange={(event) => selection('familyIds', ids(event))}>{families.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.familyIds.filter((id) => !knownFamilyIds.has(id)).map((id) => <option key={id} value={id}>Historical family #{id}</option>)}</select></label>
    <label>Модель<select multiple className={box} value={filters.modelIds.map(String)} onChange={(event) => selection('modelIds', ids(event))}>{models.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.modelIds.filter((id) => !knownModelIds.has(id)).map((id) => <option key={id} value={id}>Historical model #{id}</option>)}</select></label></>}
    <details className="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><summary className="cursor-pointer rounded font-semibold focus-visible:outline-2 focus-visible:outline-blue-500">Джерела <span className="font-normal text-slate-500 dark:text-slate-400">· {filters.sourceIds.length ? `обрано ${filters.sourceIds.length}` : 'усі'}</span></summary><fieldset aria-label="Джерела" className="mt-2 max-h-44 space-y-1 overflow-y-auto">{[...dictionary.sources.map((item) => ({ id: item.id, label: item.name })), ...unknownSources.map((id) => ({ id, label: `Недоступне джерело #${id}` }))].map((item) => <label key={item.id} className="flex min-h-9 cursor-pointer items-center gap-2 rounded-lg px-1 hover:bg-slate-50 dark:hover:bg-slate-800"><input type="checkbox" className="h-4 w-4 shrink-0 accent-blue-600" checked={filters.sourceIds.includes(item.id)} onChange={() => toggleSource(item.id)} /><span className="min-w-0 break-words">{item.label}</span></label>)}{!dictionary.sources.length && !unknownSources.length && <p className="text-xs text-slate-500">Список джерел поки недоступний</p>}</fieldset></details>
    {!entitySection && <label>Область<select className={box} value={filters.regionId ?? ''} onChange={(event) => apply({ ...filters, regionId: event.target.value ? Number(event.target.value) : undefined, cursor: undefined })}><option value="">Всі області</option>{dictionary.regions.filter((item) => item.level === 'Region' || item.level === 'City').map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{unknownRegion && <option value={unknownRegion}>Historical region #{unknownRegion}</option>}</select></label>}
    <details className="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><summary className="cursor-pointer rounded font-semibold focus-visible:outline-2 focus-visible:outline-blue-500">Період <span className="font-normal text-slate-500 dark:text-slate-400">· {filters.from ? 'власний' : 'поточний зріз'}</span></summary><fieldset className="mt-3 min-w-0"><legend className="text-xs text-slate-500 dark:text-slate-400">Час Києва · Europe/Kyiv</legend><div className="mt-1 flex flex-wrap gap-1">{[[24, '24 год'], [168, '7 д'], [720, '30 д'], [2160, '90 д']].map(([hours, label]) => <button key={hours} type="button" className="rounded border border-slate-300 px-2 py-1 text-xs dark:border-slate-600" onClick={() => preset(Number(hours))}>{label}</button>)}</div><div className="mt-1 grid grid-cols-1 gap-1"><input aria-label="Початок, Europe/Kyiv" type="datetime-local" className={box} value={from} onChange={(event) => setFrom(event.target.value)} /><input aria-label="Кінець, Europe/Kyiv" type="datetime-local" className={box} value={to} onChange={(event) => setTo(event.target.value)} /></div><button type="button" className="mt-1 rounded bg-blue-600 px-2 py-1 text-xs text-white" onClick={applyDates}>Застосувати період</button>{dateError && <div role="alert" className="mt-1 text-xs text-red-700 dark:text-red-300">{dateError}</div>}{filters.from && filters.to && <div className="mt-1 text-[11px] text-slate-500">{kyivLabel(filters.from)} — {kyivLabel(filters.to)}</div>}</fieldset></details>
  </div>
}

function Chips({ filters, onChange, entitySection = false, targetAnalytics = true }: { filters: DataQuery; onChange: (next: DataQuery) => void; entitySection?: boolean; targetAnalytics?: boolean }) {
  const entries: [keyof DataQuery, string][] = [
    ['q', filters.q ? `Пошук: ${filters.q}` : ''], ['entityKinds', filters.entityKinds.length ? `Сутності: ${filters.entityKinds.map((kind) => isRetiredEntity(kind) ? 'Треки вимкнено' : entityLabel(kind)).join(', ')}` : ''], ['eventKinds', filters.eventKinds.length ? `Види: ${filters.eventKinds.join(', ')}` : ''], ['eventCategories', filters.eventCategories.length ? `Категорії: ${filters.eventCategories.join(', ')}` : ''], ['categoryIds', filters.categoryIds.length ? `Категорія: ${filters.categoryIds.join(', ')}` : ''], ['classIds', filters.classIds.length ? `Клас: ${filters.classIds.join(', ')}` : ''], ['familyIds', filters.familyIds.length ? `Сімейство: ${filters.familyIds.join(', ')}` : ''], ['modelIds', filters.modelIds.length ? `Модель: ${filters.modelIds.join(', ')}` : ''], ['sourceIds', filters.sourceIds.length ? `Джерела: ${filters.sourceIds.join(', ')}` : ''], ['regionId', filters.regionId ? `Область #${filters.regionId}` : ''], ['from', filters.from && filters.to ? 'Період' : ''],
  ].filter(([key, label]) => label && (entitySection ? ['q', 'entityKinds', 'sourceIds', 'from'].includes(key) : !['q', 'entityKinds'].includes(key) && (targetAnalytics || ['sourceIds', 'regionId', 'from'].includes(key)))) as [keyof DataQuery, string][]
  if (!entries.length) return null
  return <div aria-label="Активні фільтри" className="flex flex-wrap gap-1">{entries.map(([key, label]) => <button key={key} className="max-w-full break-words rounded-lg bg-blue-100 px-2 py-1 text-left text-xs text-blue-900 dark:bg-blue-950 dark:text-blue-100" onClick={() => { const next = { ...filters, cursor: undefined }; if (key === 'from') { delete next.from; delete next.to } else if (Array.isArray(next[key])) (next[key] as unknown[]) = []; else delete next[key]; onChange(next) }}>{label} ×</button>)}</div>
}
