import type { AdminSourceDto } from '../../api/admin'

export type SourceSortKey = 'name' | 'type' | 'trustLevel' | 'priority' | 'status' | 'rawMessageCount'

/** Telegram's observed title is preferred, but a configured source name remains a reliable fallback. */
export function telegramTitle(name: string, channelTitle?: string): string {
  return channelTitle?.trim() || name
}

/** The stored channel value is normalized without '@'; tolerate a legacy value that still includes it. */
export function telegramUsername(channel?: string): string {
  return channel?.trim() ? `@${channel.trim().replace(/^@+/, '')}` : 'username не задано'
}

export function filterSources(sources: AdminSourceDto[], query: string): AdminSourceDto[] {
  const term = query.trim().toLocaleLowerCase('uk')
  if (!term) return sources
  return sources.filter((source) => [source.name, source.channel, source.code].some((value) => value?.toLocaleLowerCase('uk').includes(term)))
}

/**
 * What a bulk action would touch: every selected source that still exists, and which of them the current search hides.
 * A selection survives a filter change, so its scope must be said, not implied by the visible rows.
 */
export function selectionScope(selected: ReadonlySet<number>, visible: AdminSourceDto[], all: AdminSourceDto[]): { sources: AdminSourceDto[]; hidden: AdminSourceDto[] } {
  const shown = new Set(visible.map((s) => s.id))
  const sources = all.filter((s) => selected.has(s.id))
  return { sources, hidden: sources.filter((s) => !shown.has(s.id)) }
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
