import type { TaxonomyDto } from '../api/types'

/** Data state belongs to the URL. Presentation preferences (theme, density, map layers) intentionally do not. */
export interface DataQuery {
  eventKinds: string[]
  entityKinds: string[]
  eventCategories: string[]
  categoryIds: number[]
  classIds: number[]
  familyIds: number[]
  modelIds: number[]
  sourceIds: number[]
  regionId?: number
  from?: Date
  to?: Date
  q?: string
  status?: string
  confidence?: string
  location?: string
  hasResults?: boolean
  outcome?: string
  sort?: string
  cursor?: string
  pageSize?: number
  dataset?: string
}

export interface ParsedDataQuery {
  value: DataQuery
  errors: string[]
}

const LIST_KEYS = ['eventKinds', 'entityKinds', 'eventCategories', 'categoryIds', 'classIds', 'familyIds', 'modelIds', 'sourceIds'] as const
const ALL_KEYS = [...LIST_KEYS, 'regionId', 'from', 'to', 'q', 'status', 'confidence', 'location', 'hasResults', 'outcome', 'sort', 'cursor', 'pageSize'] as const
const CANONICAL_ORDER = ['metric', 'preset', 'eventKinds', 'entityKinds', 'eventCategories', 'categoryIds', 'classIds', 'familyIds', 'modelIds', 'sourceIds', 'regionId', 'from', 'to', 'q', 'status', 'confidence', 'location', 'hasResults', 'outcome', 'sort', 'cursor', 'pageSize', 'dataset']

const empty = (): DataQuery => ({ eventKinds: [], entityKinds: [], eventCategories: [], categoryIds: [], classIds: [], familyIds: [], modelIds: [], sourceIds: [] })

function stableList(values: string[]): string[] {
  return [...new Set(values.filter(Boolean))].sort((a, b) => a.localeCompare(b, 'en', { numeric: true }))
}
function readList(params: URLSearchParams, key: (typeof LIST_KEYS)[number], errors: string[]): string[] {
  const raw = params.get(key)
  if (!raw) return []
  const values = raw.split(',').filter(Boolean)
  if (values.length !== raw.split(',').length) errors.push(`${key}: порожнє значення`)
  if (key.endsWith('Ids')) {
    const invalid = values.filter((value) => !/^\d+$/.test(value))
    if (invalid.length) errors.push(`${key}: некоректний ID`)
    return stableList(values.filter((value) => /^\d+$/.test(value)))
  }
  return stableList(values)
}
function numberParam(params: URLSearchParams, key: 'regionId' | 'pageSize', errors: string[]): number | undefined {
  const raw = params.get(key)
  if (raw === null) return undefined
  if (!/^\d+$/.test(raw) || Number(raw) < 1) {
    errors.push(`${key}: некоректний ID`)
    return undefined
  }
  return Number(raw)
}
function wireDate(value: string | null): Date | null {
  if (!value || !/(?:Z|[+-]\d{2}:\d{2})$/i.test(value)) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** Parses only known U01 public data fields; empty selection is deliberately "all". */
export function parseDataQuery(params: URLSearchParams): ParsedDataQuery {
  const errors: string[] = []
  const value = empty()
  for (const key of LIST_KEYS) {
    const values = readList(params, key, errors)
    if (key === 'eventKinds' || key === 'entityKinds' || key === 'eventCategories') value[key] = values
    else value[key] = values.map(Number)
  }
  value.regionId = numberParam(params, 'regionId', errors)
  value.pageSize = numberParam(params, 'pageSize', errors)
  const dataset = params.get('dataset')?.trim()
  if (dataset) value.dataset = dataset
  const fromRaw = params.get('from')
  const toRaw = params.get('to')
  if (fromRaw || toRaw) {
    const from = wireDate(fromRaw)
    const to = wireDate(toRaw)
    if (!from || !to || from >= to) errors.push('Період має містити коректні UTC початок і кінець')
    else {
      value.from = from
      value.to = to
    }
  }
  const q = params.get('q')?.trim()
  if (q) value.q = q
  for (const key of ['status', 'confidence', 'location', 'outcome', 'sort', 'cursor'] as const) {
    const valueAtKey = params.get(key)?.trim()
    if (valueAtKey) value[key] = valueAtKey
  }
  if (params.has('hasResults')) {
    const raw = params.get('hasResults')
    if (raw === 'true') value.hasResults = true
    else if (raw === 'false') value.hasResults = false
    else errors.push('hasResults: очікується true або false')
  }
  return { value, errors }
}

/** A taxonomy parent change removes only incompatible descendants, never unrelated data filters. */
export function normalizeTaxonomyQuery(value: DataQuery, taxonomy: TaxonomyDto): { value: DataQuery; removed: string[] } {
  const categories = new Set(taxonomy.categories.map((category) => category.id))
  // A missing dictionary item may be a disabled historical value. Preserve it;
  // only a *known* child made incompatible by a known parent is removed.
  const selectedCategories = value.categoryIds.some((id) => categories.has(id)) ? new Set(value.categoryIds.filter((id) => categories.has(id))) : categories
  const classes = taxonomy.categories.filter((category) => selectedCategories.has(category.id)).flatMap((category) => category.classes)
  const allowedClasses = new Set(classes.map((item) => item.id))
  const allClasses = new Set(taxonomy.categories.flatMap((category) => category.classes.map((item) => item.id)))
  const keptClasses = value.classIds.filter((id) => !allClasses.has(id) || allowedClasses.has(id))
  const selectedClasses = keptClasses.some((id) => allowedClasses.has(id)) ? new Set(keptClasses.filter((id) => allowedClasses.has(id))) : allowedClasses
  const families = classes.filter((item) => selectedClasses.has(item.id)).flatMap((item) => item.families)
  const allowedFamilies = new Set(families.map((item) => item.id))
  const allFamilies = new Set(taxonomy.categories.flatMap((category) => category.classes.flatMap((item) => item.families.map((family) => family.id))))
  const keptFamilies = value.familyIds.filter((id) => !allFamilies.has(id) || allowedFamilies.has(id))
  const selectedFamilies = keptFamilies.some((id) => allowedFamilies.has(id)) ? new Set(keptFamilies.filter((id) => allowedFamilies.has(id))) : allowedFamilies
  const allowedModels = new Set(families.filter((item) => selectedFamilies.has(item.id)).flatMap((item) => item.models.map((model) => model.id)))
  const allModels = new Set(taxonomy.categories.flatMap((category) => category.classes.flatMap((item) => item.families.flatMap((family) => family.models.map((model) => model.id)))))
  const keptModels = value.modelIds.filter((id) => !allModels.has(id) || allowedModels.has(id))
  const removed = [
    ...value.classIds.filter((id) => allClasses.has(id) && !keptClasses.includes(id)).map((id) => `classIds=${id}`),
    ...value.familyIds.filter((id) => allFamilies.has(id) && !keptFamilies.includes(id)).map((id) => `familyIds=${id}`),
    ...value.modelIds.filter((id) => allModels.has(id) && !keptModels.includes(id)).map((id) => `modelIds=${id}`),
  ]
  return { value: { ...value, classIds: keptClasses, familyIds: keptFamilies, modelIds: keptModels }, removed }
}

function setList(params: URLSearchParams, key: (typeof LIST_KEYS)[number], values: readonly (number | string)[]) {
  const next = stableList(values.map(String))
  if (next.length) params.set(key, next.join(','))
}

/** Replaces public filter fields but preserves route-owned state such as metric, preset, dataset and map preset. */
export function serializeDataQuery(base: URLSearchParams, value: DataQuery): URLSearchParams {
  const params = new URLSearchParams(base)
  for (const key of ALL_KEYS) params.delete(key)
  setList(params, 'eventKinds', value.eventKinds)
  setList(params, 'entityKinds', value.entityKinds)
  setList(params, 'eventCategories', value.eventCategories)
  setList(params, 'categoryIds', value.categoryIds)
  setList(params, 'classIds', value.classIds)
  setList(params, 'familyIds', value.familyIds)
  setList(params, 'modelIds', value.modelIds)
  setList(params, 'sourceIds', value.sourceIds)
  if (value.regionId) params.set('regionId', String(value.regionId))
  if (value.from && value.to && value.from < value.to) {
    params.set('from', value.from.toISOString())
    params.set('to', value.to.toISOString())
  }
  if (value.q?.trim()) params.set('q', value.q.trim())
  for (const key of ['status', 'confidence', 'location', 'outcome', 'sort', 'cursor'] as const) if (value[key]) params.set(key, value[key])
  if (value.hasResults !== undefined) params.set('hasResults', String(value.hasResults))
  if (value.pageSize) params.set('pageSize', String(value.pageSize))
  if (value.dataset) params.set('dataset', value.dataset)
  return canonicalizeQuery(params)
}

/** Stable key ordering makes links comparable and avoids URL rewrite loops. */
export function canonicalizeQuery(params: URLSearchParams): URLSearchParams {
  const entries = [...params.entries()].sort(([a, av], [b, bv]) => {
    const ai = CANONICAL_ORDER.indexOf(a)
    const bi = CANONICAL_ORDER.indexOf(b)
    return (ai < 0 ? CANONICAL_ORDER.length : ai) - (bi < 0 ? CANONICAL_ORDER.length : bi) || a.localeCompare(b) || av.localeCompare(bv)
  })
  return new URLSearchParams(entries)
}

export function resetDataQuery(value: DataQuery): DataQuery {
  return { ...empty(), sort: value.sort }
}
