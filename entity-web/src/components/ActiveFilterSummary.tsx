import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { RegionDto, SourceDto } from '../api/types'
import { entityLabel } from '../entities/presentation'
import { kyivLabel } from '../public/kyivTime'
import { serializeDataQuery, type DataQuery } from '../public/query'
import { publicHash, type PublicRoute } from '../public/routes'

function withoutFilter(filters: DataQuery, key: keyof DataQuery): DataQuery {
  const next = { ...filters, cursor: undefined }
  if (key === 'from') { delete next.from; delete next.to }
  else if (Array.isArray(next[key])) (next[key] as unknown[]) = []
  else delete next[key]
  return next
}

function navigate(route: PublicRoute, query: DataQuery) {
  window.location.hash = publicHash({ ...route, query: serializeDataQuery(new URLSearchParams(route.query), query) })
}

export default function ActiveFilterSummary({ route, filters, entitySection = false, targetAnalytics = true }: { route: PublicRoute; filters: DataQuery; entitySection?: boolean; targetAnalytics?: boolean }) {
  const [sources, setSources] = useState<SourceDto[]>([])
  const [regions, setRegions] = useState<RegionDto[]>([])
  useEffect(() => {
    let active = true
    Promise.all([filters.sourceIds.length ? api.sources() : Promise.resolve([]), filters.regionId ? api.regions() : Promise.resolve([])])
      .then(([nextSources, nextRegions]) => { if (active) { setSources(nextSources); setRegions(nextRegions) } })
      .catch(() => {})
    return () => { active = false }
  }, [filters.sourceIds.length, filters.regionId])

  const sourceNames = filters.sourceIds.map((id) => sources.find((source) => source.id === id)?.name ?? `джерело №${id}`).join(', ')
  const regionName = filters.regionId ? regions.find((region) => region.id === filters.regionId)?.name ?? `область №${filters.regionId}` : ''
  const entries: [keyof DataQuery, string][] = [
    ['q', entitySection && filters.q ? `Пошук: ${filters.q}` : ''],
    ['entityKinds', entitySection && filters.entityKinds.length ? `Типи: ${filters.entityKinds.map(entityLabel).join(', ')}` : ''],
    ['eventKinds', !entitySection && targetAnalytics && filters.eventKinds.length ? `Види: ${filters.eventKinds.join(', ')}` : ''],
    ['eventCategories', !entitySection && targetAnalytics && filters.eventCategories.length ? `Категорії: ${filters.eventCategories.join(', ')}` : ''],
    ['categoryIds', !entitySection && targetAnalytics && filters.categoryIds.length ? `Категорії №${filters.categoryIds.join(', ')}` : ''],
    ['classIds', !entitySection && targetAnalytics && filters.classIds.length ? `Класи №${filters.classIds.join(', ')}` : ''],
    ['familyIds', !entitySection && targetAnalytics && filters.familyIds.length ? `Сімейства №${filters.familyIds.join(', ')}` : ''],
    ['modelIds', !entitySection && targetAnalytics && filters.modelIds.length ? `Моделі №${filters.modelIds.join(', ')}` : ''],
    ['sourceIds', filters.sourceIds.length ? `Джерела: ${sourceNames}` : ''],
    ['regionId', !entitySection && filters.regionId ? `Область: ${regionName}` : ''],
    ['from', filters.from && filters.to ? `${kyivLabel(filters.from)} — ${kyivLabel(filters.to)}` : ''],
  ].filter((entry): entry is [keyof DataQuery, string] => Boolean(entry[1]))
  if (!entries.length) return null

  const remove = (key: keyof DataQuery) => {
    const next = withoutFilter(filters, key)
    navigate(route, next)
  }
  return <div aria-label="Активні фільтри" className="mt-2 flex flex-wrap items-center gap-1"><span className="text-xs text-slate-500">Активні фільтри:</span>{entries.map(([key, label]) => <button type="button" key={key} title="Прибрати фільтр" className="max-w-full break-words rounded-lg bg-blue-100 px-2 py-1 text-left text-xs text-blue-900 dark:bg-blue-950 dark:text-blue-100" onClick={() => remove(key)}>{label} ×</button>)}</div>
}
