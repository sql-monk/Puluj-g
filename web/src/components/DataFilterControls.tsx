import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { EventKindDto, RegionDto, SourceDto, TaxonomyDto } from '../api/types'
import { kyivLabel, parseKyivInput, toKyivInput } from '../public/kyivTime'
import { normalizeTaxonomyQuery, parseDataQuery, resetDataQuery, serializeDataQuery, type DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

type Dictionaries = { taxonomy: TaxonomyDto; eventKinds: EventKindDto[]; sources: SourceDto[]; regions: RegionDto[] }

const emptyDictionaries: Dictionaries = { taxonomy: { categories: [] }, eventKinds: [], sources: [], regions: [] }
const ids = (event: React.ChangeEvent<HTMLSelectElement>) => [...event.target.selectedOptions].map((option) => Number(option.value)).filter(Number.isFinite)
const strings = (event: React.ChangeEvent<HTMLSelectElement>) => [...event.target.selectedOptions].map((option) => option.value)

/** One shared, section-scoped panel. U06/U08/U09/U12 receive the same URL contract, not copied controls. */
export default function DataFilterControls({ route }: { route: PublicRoute }) {
  const [dictionary, setDictionary] = useState<Dictionaries>(emptyDictionaries)
  const [dictionaryError, setDictionaryError] = useState<string | null>(null)
  const parsed = useMemo(() => parseDataQuery(route.query), [route.query])
  const normalized = useMemo(() => (dictionary.taxonomy.categories.length ? normalizeTaxonomyQuery(parsed.value, dictionary.taxonomy) : { value: parsed.value, removed: [] }), [dictionary.taxonomy, parsed.value])
  const filters = normalized.value
  const [draftQ, setDraftQ] = useState(filters.q ?? '')
  const [from, setFrom] = useState(filters.from ? toKyivInput(filters.from) : '')
  const [to, setTo] = useState(filters.to ? toKyivInput(filters.to) : '')
  const [dateError, setDateError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    Promise.all([api.taxonomy(), api.eventKinds(), api.sources(), api.regions()])
      .then(([taxonomy, eventKinds, sources, regions]) => active && setDictionary({ taxonomy, eventKinds, sources, regions }))
      .catch(() => active && setDictionaryError('Довідники ще недоступні; значення з URL збережені без змін.'))
    return () => { active = false }
  }, [])
  useEffect(() => setDraftQ(filters.q ?? ''), [filters.q])
  useEffect(() => { setFrom(filters.from ? toKyivInput(filters.from) : ''); setTo(filters.to ? toKyivInput(filters.to) : '') }, [filters.from, filters.to])
  useEffect(() => {
    const id = window.setTimeout(() => {
      if (draftQ === (filters.q ?? '')) return
      apply({ ...filters, q: draftQ || undefined, cursor: undefined })
    }, 350)
    return () => window.clearTimeout(id)
  // Only q is intentionally debounced; other controls are atomic applies.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftQ])

  const apply = (next: DataQuery, customPeriod = false) => {
    const clean = dictionary.taxonomy.categories.length ? normalizeTaxonomyQuery(next, dictionary.taxonomy).value : next
    const base = new URLSearchParams(route.query)
    // `preset` is analytics-relative time. A resolved custom interval wins it;
    // map preset=kyiv deliberately remains route-owned and untouched.
    if (customPeriod && route.section === 'analytics') base.delete('preset')
    window.location.hash = publicHash({ ...route, query: serializeDataQuery(base, { ...clean, cursor: clean.cursor }) })
  }
  const selection = <T extends keyof Pick<DataQuery, 'categoryIds' | 'classIds' | 'familyIds' | 'modelIds' | 'sourceIds'>>(key: T, value: DataQuery[T]) => apply({ ...filters, [key]: value, cursor: undefined })
  const selectedCategories = filters.categoryIds.length ? new Set(filters.categoryIds) : new Set(dictionary.taxonomy.categories.map((item) => item.id))
  const classes = dictionary.taxonomy.categories.filter((item) => selectedCategories.has(item.id)).flatMap((item) => item.classes)
  const selectedClasses = filters.classIds.length ? new Set(filters.classIds) : new Set(classes.map((item) => item.id))
  const families = classes.filter((item) => selectedClasses.has(item.id)).flatMap((item) => item.families)
  const selectedFamilies = filters.familyIds.length ? new Set(filters.familyIds) : new Set(families.map((item) => item.id))
  const models = families.filter((item) => selectedFamilies.has(item.id)).flatMap((item) => item.models)
  const unknownKinds = filters.eventKinds.filter((code) => !dictionary.eventKinds.some((kind) => kind.code === code))
  const knownCategoryIds = new Set(dictionary.taxonomy.categories.map((item) => item.id))
  const knownClassIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.map((child) => child.id)))
  const knownFamilyIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.flatMap((child) => child.families.map((family) => family.id))))
  const knownModelIds = new Set(dictionary.taxonomy.categories.flatMap((item) => item.classes.flatMap((child) => child.families.flatMap((family) => family.models.map((model) => model.id)))))
  const unknownSources = filters.sourceIds.filter((id) => !dictionary.sources.some((source) => source.id === id))
  const unknownRegion = filters.regionId && !dictionary.regions.some((region) => region.id === filters.regionId) ? filters.regionId : undefined
  const advancedSupported = route.section !== 'analytics'
  const noMessageResults = route.section === 'messages' && filters.hasResults === false
  const box = 'mt-1 w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm dark:border-slate-600 dark:bg-slate-800'

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
    <div className="flex items-center justify-between"><strong>Дані</strong><button className="text-xs text-blue-700 hover:underline dark:text-blue-300" onClick={() => apply(resetDataQuery(filters))}>Скинути фільтри</button></div>
    {(parsed.errors.length > 0 || dictionaryError || normalized.removed.length > 0 || unknownSources.length > 0 || unknownRegion) && <p className="rounded bg-amber-100 p-2 text-xs text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{[...parsed.errors, dictionaryError, normalized.removed.length ? `Прибрано несумісні: ${normalized.removed.join(', ')}` : '', unknownSources.length ? `Невідомі/історичні джерела: ${unknownSources.join(', ')}` : '', unknownRegion ? `Невідома/історична область: ${unknownRegion}` : ''].filter(Boolean).join(' ')}</p>}
    <label>Пошук<input className={box} value={draftQ} onChange={(event) => setDraftQ(event.target.value)} placeholder="Текст або код" /></label>
    {route.section !== 'messages' && <label>Тип сутності<select multiple className={box} value={filters.entityKinds} onChange={(event) => apply({ ...filters, entityKinds: strings(event), cursor: undefined })}>{['track', 'incident', 'alert', 'observation'].map((kind) => <option key={kind} value={kind}>{kind}</option>)}</select></label>}
    <label>Вид події<select disabled={noMessageResults} multiple className={box} value={filters.eventKinds} onChange={(event) => apply({ ...filters, eventKinds: strings(event), cursor: undefined })}>{dictionary.eventKinds.map((kind) => <option key={kind.code} value={kind.code}>{kind.nameUk} · {kind.code}</option>)}{unknownKinds.map((code) => <option key={code} value={code}>Недоступний historical code: {code}</option>)}</select></label>
    {route.section !== 'messages' && <label>Категорія події<select multiple className={box} value={filters.eventCategories} onChange={(event) => apply({ ...filters, eventCategories: strings(event), cursor: undefined })}>{['target', 'alert', 'incident', 'info'].map((category) => <option key={category} value={category}>{category}</option>)}</select></label>}
    <label>Категорія<select disabled={noMessageResults} multiple className={box} value={filters.categoryIds.map(String)} onChange={(event) => selection('categoryIds', ids(event))}>{dictionary.taxonomy.categories.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.categoryIds.filter((id) => !knownCategoryIds.has(id)).map((id) => <option key={id} value={id}>Historical category #{id}</option>)}</select></label>
    <label>Клас<select disabled={noMessageResults} multiple className={box} value={filters.classIds.map(String)} onChange={(event) => selection('classIds', ids(event))}>{classes.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.classIds.filter((id) => !knownClassIds.has(id)).map((id) => <option key={id} value={id}>Historical class #{id}</option>)}</select></label>
    <label>Сімейство<select disabled={noMessageResults} multiple className={box} value={filters.familyIds.map(String)} onChange={(event) => selection('familyIds', ids(event))}>{families.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.familyIds.filter((id) => !knownFamilyIds.has(id)).map((id) => <option key={id} value={id}>Historical family #{id}</option>)}</select></label>
    <label>Модель<select disabled={noMessageResults} multiple className={box} value={filters.modelIds.map(String)} onChange={(event) => selection('modelIds', ids(event))}>{models.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{filters.modelIds.filter((id) => !knownModelIds.has(id)).map((id) => <option key={id} value={id}>Historical model #{id}</option>)}</select></label>
    <label>Джерела<select multiple className={box} value={filters.sourceIds.map(String)} onChange={(event) => selection('sourceIds', ids(event))}>{dictionary.sources.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{unknownSources.map((id) => <option key={id} value={id}>Historical source #{id}</option>)}</select></label>
    <label>Область<select disabled={noMessageResults} className={box} value={filters.regionId ?? ''} onChange={(event) => apply({ ...filters, regionId: event.target.value ? Number(event.target.value) : undefined, cursor: undefined })}><option value="">Всі області</option>{dictionary.regions.filter((item) => item.level === 'Region' || item.level === 'City').map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{unknownRegion && <option value={unknownRegion}>Historical region #{unknownRegion}</option>}</select></label>
    {advancedSupported ? <div className="grid grid-cols-2 gap-2">{route.section !== 'messages' && <label>Статус<select className={box} value={filters.status ?? ''} onChange={(event) => apply({ ...filters, status: event.target.value || undefined, cursor: undefined })}><option value="">Будь-який</option><option value="active">Активний</option><option value="closed">Закритий</option></select></label>}<label>Впевненість<select disabled={noMessageResults} className={box} value={filters.confidence ?? ''} onChange={(event) => apply({ ...filters, confidence: event.target.value || undefined, cursor: undefined })}><option value="">Будь-яка</option><option value="high">Висока</option><option value="medium">Середня</option><option value="low">Низька</option></select></label><label>Локація<select disabled={noMessageResults} className={box} value={filters.location ?? ''} onChange={(event) => apply({ ...filters, location: event.target.value || undefined, cursor: undefined })}><option value="">Будь-яка</option><option value="located">Є локація</option><option value="unlocated">Без локації</option></select></label><label>Результати<select className={box} value={filters.hasResults === undefined ? '' : String(filters.hasResults)} onChange={(event) => { const noResults = event.target.value === 'false'; apply({ ...filters, hasResults: event.target.value ? !noResults : undefined, eventKinds: noResults ? [] : filters.eventKinds, categoryIds: noResults ? [] : filters.categoryIds, classIds: noResults ? [] : filters.classIds, familyIds: noResults ? [] : filters.familyIds, modelIds: noResults ? [] : filters.modelIds, regionId: noResults ? undefined : filters.regionId, confidence: noResults ? undefined : filters.confidence, location: noResults ? undefined : filters.location, cursor: undefined }) }}><option value="">Будь-які</option><option value="true">Є</option><option value="false">Немає</option></select></label>{route.section === 'messages' && <label className="col-span-2">Результат обробки<select className={box} value={filters.outcome ?? ''} onChange={(event) => apply({ ...filters, outcome: event.target.value || undefined, cursor: undefined })}><option value="">Будь-який</option><option value="pending">Очікує</option><option value="awaiting_llm">Очікує розпізнавання</option><option value="parsed_projection_pending">Проєкція ще формується</option><option value="completed">Завершено</option><option value="no_facts">Результатів не знайдено</option><option value="failed">Помилка обробки</option><option value="skipped">Пропущено</option><option value="legacy_processed">Історично оброблено</option></select></label>}</div> : <p className="rounded bg-slate-100 p-2 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">Аналітика застосовує лише підтримані кожною метрикою фільтри. Під графіками явно показано застосовані й недоступні поля; недоступне поле не маскує результат.</p>}
    <fieldset><legend>Період (Europe/Kyiv)</legend><div className="mt-1 flex flex-wrap gap-1">{[[24, '24 год'], [168, '7 д'], [720, '30 д'], [2160, '90 д']].map(([hours, label]) => <button key={hours} type="button" className="rounded border border-slate-300 px-2 py-1 text-xs dark:border-slate-600" onClick={() => preset(Number(hours))}>{label}</button>)}</div><div className="mt-1 flex gap-1"><input aria-label="Початок, Europe/Kyiv" type="datetime-local" className={box} value={from} onChange={(event) => setFrom(event.target.value)} /><input aria-label="Кінець, Europe/Kyiv" type="datetime-local" className={box} value={to} onChange={(event) => setTo(event.target.value)} /></div><button type="button" className="mt-1 rounded bg-blue-600 px-2 py-1 text-xs text-white" onClick={applyDates}>Застосувати період</button>{dateError && <div role="alert" className="mt-1 text-xs text-red-700 dark:text-red-300">{dateError}</div>}{filters.from && filters.to && <div className="mt-1 text-[11px] text-slate-500">{kyivLabel(filters.from)} — {kyivLabel(filters.to)}</div>}</fieldset>
    <Chips filters={filters} onChange={apply} />
  </div>
}

function Chips({ filters, onChange }: { filters: DataQuery; onChange: (next: DataQuery) => void }) {
  const entries: [keyof DataQuery, string][] = [
    ['q', filters.q ? `Пошук: ${filters.q}` : ''], ['eventKinds', filters.eventKinds.length ? `Види: ${filters.eventKinds.join(', ')}` : ''], ['eventCategories', filters.eventCategories.length ? `Категорії: ${filters.eventCategories.join(', ')}` : ''], ['categoryIds', filters.categoryIds.length ? `Категорія: ${filters.categoryIds.join(', ')}` : ''], ['classIds', filters.classIds.length ? `Клас: ${filters.classIds.join(', ')}` : ''], ['familyIds', filters.familyIds.length ? `Сімейство: ${filters.familyIds.join(', ')}` : ''], ['modelIds', filters.modelIds.length ? `Модель: ${filters.modelIds.join(', ')}` : ''], ['sourceIds', filters.sourceIds.length ? `Джерела: ${filters.sourceIds.join(', ')}` : ''], ['regionId', filters.regionId ? `Область #${filters.regionId}` : ''], ['outcome', filters.outcome ? `Результат: ${filters.outcome}` : ''], ['from', filters.from && filters.to ? 'Період' : ''],
  ].filter(([, label]) => label) as [keyof DataQuery, string][]
  if (!entries.length) return null
  return <div aria-label="Активні фільтри" className="flex flex-wrap gap-1">{entries.map(([key, label]) => <button key={key} className="rounded-full bg-blue-100 px-2 py-0.5 text-xs text-blue-900 dark:bg-blue-950 dark:text-blue-100" onClick={() => { const next = { ...filters, cursor: undefined }; if (key === 'from') { delete next.from; delete next.to } else if (Array.isArray(next[key])) (next[key] as unknown[]) = []; else delete next[key]; onChange(next) }}>{label} ×</button>)}</div>
}
