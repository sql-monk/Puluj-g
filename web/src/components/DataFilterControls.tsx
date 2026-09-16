import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { EventKindDto, RegionDto, SourceDto, TaxonomyDto } from '../api/types'
import { kyivLabel, parseKyivInput, toKyivInput } from '../public/kyivTime'
import { normalizeTaxonomyQuery, parseDataQuery, resetDataQuery, serializeDataQuery, type DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

type Dictionaries = { taxonomy: TaxonomyDto; eventKinds: EventKindDto[]; sources: SourceDto[]; regions: RegionDto[] }

const emptyDictionaries: Dictionaries = { taxonomy: { categories: [] }, eventKinds: [], sources: [], regions: [] }

type Choice<T extends string | number> = { value: T; label: string }

function MultiSelect<T extends string | number>({ label, choices, selected, onChange, emptyLabel = 'Усі' }: { label: string; choices: Choice<T>[]; selected: T[]; onChange: (next: T[]) => void; emptyLabel?: string }) {
  const selectedSet = new Set(selected)
  const selectedLabels = choices.filter((choice) => selectedSet.has(choice.value)).map((choice) => choice.label)
  const unknownCount = selected.length - selectedLabels.length
  const summary = selected.length === 0 ? emptyLabel : `${selectedLabels.slice(0, 2).join(', ')}${selectedLabels.length > 2 ? ` +${selectedLabels.length - 2}` : ''}${unknownCount > 0 ? ` +${unknownCount}` : ''}`
  return <details className="group rounded border border-slate-300 bg-white dark:border-slate-600 dark:bg-slate-800"><summary className="cursor-pointer list-none px-2 py-1.5 text-sm marker:content-none"><span className="flex items-center justify-between gap-2"><span>{label}</span><span className="max-w-40 truncate text-xs text-slate-500 group-open:hidden dark:text-slate-400">{summary}</span><span className="text-slate-400 group-open:rotate-180">⌄</span></span></summary><div className="max-h-52 overflow-y-auto border-t border-slate-200 p-2 dark:border-slate-700">{choices.length === 0 ? <p className="text-xs text-slate-500">Немає доступних значень.</p> : <div className="flex flex-col gap-1">{choices.map((choice) => <label key={String(choice.value)} className="flex cursor-pointer items-center gap-2 rounded px-1 py-0.5 hover:bg-slate-100 dark:hover:bg-slate-700"><input type="checkbox" checked={selectedSet.has(choice.value)} onChange={(event) => onChange(event.target.checked ? [...selected, choice.value] : selected.filter((value) => value !== choice.value))} /><span>{choice.label}</span></label>)}</div>}</div></details>
}

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
    <div className="flex items-center justify-between"><strong>Фільтрувати дані</strong><button className="text-xs text-blue-700 hover:underline dark:text-blue-300" onClick={() => apply(resetDataQuery(filters))}>Скинути все</button></div>
    {(parsed.errors.length > 0 || dictionaryError || normalized.removed.length > 0 || unknownSources.length > 0 || unknownRegion) && <p className="rounded bg-amber-100 p-2 text-xs text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{[...parsed.errors, dictionaryError, normalized.removed.length ? `Прибрано несумісні: ${normalized.removed.join(', ')}` : '', unknownSources.length ? `Невідомі/історичні джерела: ${unknownSources.join(', ')}` : '', unknownRegion ? `Невідома/історична область: ${unknownRegion}` : ''].filter(Boolean).join(' ')}</p>}
    <label>Пошук<input className={box} value={draftQ} onChange={(event) => setDraftQ(event.target.value)} placeholder="Текст або код" /></label>
    <label>Область<select className={box} value={filters.regionId ?? ''} onChange={(event) => apply({ ...filters, regionId: event.target.value ? Number(event.target.value) : undefined, cursor: undefined })}><option value="">Всі області</option>{dictionary.regions.filter((item) => item.level === 'Region' || item.level === 'City').map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}{unknownRegion && <option value={unknownRegion}>Historical region #{unknownRegion}</option>}</select></label>
    <MultiSelect label="Джерела" selected={filters.sourceIds} onChange={(value) => selection('sourceIds', value)} choices={[...dictionary.sources.map((item) => ({ value: item.id, label: item.name })), ...unknownSources.map((id) => ({ value: id, label: `Історичне джерело #${id}` }))]} />
    <details className="rounded border border-slate-200 p-2 dark:border-slate-700"><summary className="cursor-pointer font-medium">Тип і класифікація</summary><div className="mt-2 flex flex-col gap-2"><MultiSelect label="Тип сутності" selected={filters.entityKinds} onChange={(value) => apply({ ...filters, entityKinds: value, cursor: undefined })} choices={['track', 'incident', 'alert', 'observation'].map((value) => ({ value, label: value }))} /><MultiSelect label="Вид події" selected={filters.eventKinds} onChange={(value) => apply({ ...filters, eventKinds: value, cursor: undefined })} choices={[...dictionary.eventKinds.map((kind) => ({ value: kind.code, label: kind.nameUk })), ...unknownKinds.map((code) => ({ value: code, label: `Недоступний historical code: ${code}` }))]} /><MultiSelect label="Категорія події" selected={filters.eventCategories} onChange={(value) => apply({ ...filters, eventCategories: value, cursor: undefined })} choices={['target', 'alert', 'incident', 'info'].map((value) => ({ value, label: value }))} /><MultiSelect label="Категорія" selected={filters.categoryIds} onChange={(value) => selection('categoryIds', value)} choices={[...dictionary.taxonomy.categories.map((item) => ({ value: item.id, label: item.name })), ...filters.categoryIds.filter((id) => !knownCategoryIds.has(id)).map((id) => ({ value: id, label: `Historical category #${id}` }))]} /><MultiSelect label="Клас" selected={filters.classIds} onChange={(value) => selection('classIds', value)} choices={[...classes.map((item) => ({ value: item.id, label: item.name })), ...filters.classIds.filter((id) => !knownClassIds.has(id)).map((id) => ({ value: id, label: `Historical class #${id}` }))]} /><MultiSelect label="Сімейство" selected={filters.familyIds} onChange={(value) => selection('familyIds', value)} choices={[...families.map((item) => ({ value: item.id, label: item.name })), ...filters.familyIds.filter((id) => !knownFamilyIds.has(id)).map((id) => ({ value: id, label: `Historical family #${id}` }))]} /><MultiSelect label="Модель" selected={filters.modelIds} onChange={(value) => selection('modelIds', value)} choices={[...models.map((item) => ({ value: item.id, label: item.name })), ...filters.modelIds.filter((id) => !knownModelIds.has(id)).map((id) => ({ value: id, label: `Historical model #${id}` }))]} /></div></details>
    {advancedSupported ? <div className="grid grid-cols-2 gap-2"><label>{route.section === 'messages' ? 'Результат обробки' : 'Статус'}<select className={box} value={filters.status ?? ''} onChange={(event) => apply({ ...filters, status: event.target.value || undefined, cursor: undefined })}><option value="">Будь-який</option>{route.section === 'messages' ? <><option value="pending">Очікує</option><option value="awaiting_llm">Очікує аналізу</option><option value="parsed_projection_pending">Розбір завершено, результат готується</option><option value="completed">Оброблено</option><option value="no_facts">Результатів не знайдено</option><option value="failed">Помилка обробки</option><option value="skipped">Пропущено</option><option value="legacy_processed">Оброблено (legacy)</option></> : <><option value="active">Активний</option><option value="closed">Закритий</option></>}</select></label><label>Впевненість<select className={box} value={filters.confidence ?? ''} onChange={(event) => apply({ ...filters, confidence: event.target.value || undefined, cursor: undefined })}><option value="">Будь-яка</option><option value="high">Висока</option><option value="medium">Середня</option><option value="low">Низька</option></select></label><label>Локація<select className={box} value={filters.location ?? ''} onChange={(event) => apply({ ...filters, location: event.target.value || undefined, cursor: undefined })}><option value="">Будь-яка</option><option value="known">Є локація</option><option value="missing">Без локації</option></select></label><label>Результати<select className={box} value={filters.hasResults === undefined ? '' : String(filters.hasResults)} onChange={(event) => apply({ ...filters, hasResults: event.target.value ? event.target.value === 'true' : undefined, cursor: undefined })}><option value="">Будь-які</option><option value="true">Є</option><option value="false">Немає</option></select></label></div> : <p className="rounded bg-slate-100 p-2 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">Додаткові filters для обраної метрики Аналітики ще не підтримує API; активні URL chips не приховуються.</p>}
    <fieldset><legend>Період (Europe/Kyiv)</legend><div className="mt-1 flex flex-wrap gap-1">{[[24, '24 год'], [168, '7 д'], [720, '30 д'], [2160, '90 д']].map(([hours, label]) => <button key={hours} type="button" className="rounded border border-slate-300 px-2 py-1 text-xs dark:border-slate-600" onClick={() => preset(Number(hours))}>{label}</button>)}</div><div className="mt-1 flex gap-1"><input aria-label="Початок, Europe/Kyiv" type="datetime-local" className={box} value={from} onChange={(event) => setFrom(event.target.value)} /><input aria-label="Кінець, Europe/Kyiv" type="datetime-local" className={box} value={to} onChange={(event) => setTo(event.target.value)} /></div><button type="button" className="mt-1 rounded bg-blue-600 px-2 py-1 text-xs text-white" onClick={applyDates}>Застосувати період</button>{dateError && <div role="alert" className="mt-1 text-xs text-red-700 dark:text-red-300">{dateError}</div>}{filters.from && filters.to && <div className="mt-1 text-[11px] text-slate-500">{kyivLabel(filters.from)} — {kyivLabel(filters.to)}</div>}</fieldset>
    <Chips filters={filters} onChange={apply} />
  </div>
}

function Chips({ filters, onChange }: { filters: DataQuery; onChange: (next: DataQuery) => void }) {
  const entries: [keyof DataQuery, string][] = [
    ['q', filters.q ? `Пошук: ${filters.q}` : ''],
    ['entityKinds', filters.entityKinds.length ? `Сутності: ${filters.entityKinds.length}` : ''],
    ['eventKinds', filters.eventKinds.length ? `Види: ${filters.eventKinds.length}` : ''],
    ['eventCategories', filters.eventCategories.length ? `Категорії подій: ${filters.eventCategories.length}` : ''],
    ['categoryIds', filters.categoryIds.length ? `Категорії: ${filters.categoryIds.length}` : ''],
    ['classIds', filters.classIds.length ? `Класи: ${filters.classIds.length}` : ''],
    ['familyIds', filters.familyIds.length ? `Сімейства: ${filters.familyIds.length}` : ''],
    ['modelIds', filters.modelIds.length ? `Моделі: ${filters.modelIds.length}` : ''],
    ['sourceIds', filters.sourceIds.length ? `Джерела: ${filters.sourceIds.length}` : ''],
    ['regionId', filters.regionId ? `Область #${filters.regionId}` : ''],
    ['status', filters.status ? `Статус: ${filters.status}` : ''],
    ['confidence', filters.confidence ? `Впевненість: ${filters.confidence}` : ''],
    ['location', filters.location ? `Локація: ${filters.location}` : ''],
    ['hasResults', filters.hasResults === undefined ? '' : filters.hasResults ? 'Є результати' : 'Без результатів'],
    ['from', filters.from && filters.to ? 'Період' : ''],
  ].filter(([, label]) => label) as [keyof DataQuery, string][]
  if (!entries.length) return null
  return <div aria-label="Активні фільтри" className="flex flex-wrap gap-1">{entries.map(([key, label]) => <button key={key} className="rounded-full bg-blue-100 px-2 py-0.5 text-xs text-blue-900 dark:bg-blue-950 dark:text-blue-100" onClick={() => { const next = { ...filters, cursor: undefined }; if (key === 'from') { delete next.from; delete next.to } else if (Array.isArray(next[key])) (next[key] as unknown[]) = []; else delete next[key]; onChange(next) }}>{label} ×</button>)}</div>
}
