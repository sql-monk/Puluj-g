import type { StatsFilterMetaDto, StatsPeriodDto } from '../api/types'
import type { DataQuery } from '../public/query'
import { serializeDataQuery } from '../public/query'
import { publicHash } from '../public/routes'

/**
 * The source report counts saved message revisions by PublishedAt. This is the
 * one analytics population with an exactly equivalent public catalogue; other
 * aggregate charts intentionally retain their accessible table fallback.
 */
export function sourceMessagesHref(sourceId: number, period: Pick<StatsPeriodDto, 'from' | 'to'>, filter: DataQuery): string {
  const query = serializeDataQuery(new URLSearchParams(), {
    ...filter,
    sourceIds: [sourceId],
    from: new Date(period.from),
    to: new Date(period.to),
    cursor: undefined,
  })
  return publicHash({ section: 'messages', query })
}

/**
 * Source counters under a result-derived filter use canonical facts in an
 * EXISTS predicate; the message catalogue intentionally lists every saved
 * revision and cannot reproduce that predicate.  A link is therefore safe
 * only for the unfiltered/source-only raw-message population.
 */
export function canSourceMessagesDrillDown(meta: StatsFilterMetaDto): boolean {
  return meta.unavailable.length === 0 && meta.applied.every((item) => item.startsWith('sourceIds='))
}
