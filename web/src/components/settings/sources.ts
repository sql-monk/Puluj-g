import type { AdminSourceDto } from '../../api/admin'

export type SourceSortKey = 'name' | 'type' | 'trustLevel' | 'priority' | 'status' | 'rawMessageCount'

export function filterSources(sources: AdminSourceDto[], query: string): AdminSourceDto[] {
  const term = query.trim().toLocaleLowerCase('uk')
  if (!term) return sources
  return sources.filter((source) => [source.name, source.channel, source.code].some((value) => value?.toLocaleLowerCase('uk').includes(term)))
}

/** Stable presentation sort for the source settings table; source name breaks ties. */
export function sortSources(sources: AdminSourceDto[], key: SourceSortKey, asc: boolean): AdminSourceDto[] {
  return [...sources].sort((a, b) => {
    const first = a[key]
    const second = b[key]
    const value = typeof first === 'string' && typeof second === 'string' ? first.localeCompare(second, 'uk') : Number(first) - Number(second)
    if (value !== 0) return asc ? value : -value
    return a.name.localeCompare(b.name, 'uk')
  })
}
