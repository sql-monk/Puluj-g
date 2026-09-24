import type { CollectorStatusDto } from '../api/admin'

export type CollectorSortKey = 'name' | 'type' | 'status' | 'lastPolledAt' | 'lastSuccessAt' | 'lastMessageAt' | 'messages24h' | 'lastError'

function dateValue(value?: string): number | null {
  if (!value) return null
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? null : parsed
}

function displayName(collector: CollectorStatusDto): string {
  return collector.type === 'Telegram' ? collector.channelTitle?.trim() || collector.name : collector.name
}

/** Lower values mean a source needs attention sooner. */
function statusValue(collector: CollectorStatusDto): number {
  if (!collector.enabled) return 3
  if (collector.consecutiveFailures > 0) return 0
  return collector.lastSuccessAt ? 2 : 1
}

function value(collector: CollectorStatusDto, key: CollectorSortKey): string | number | null {
  switch (key) {
    case 'name': return displayName(collector)
    case 'type': return collector.type
    case 'status': return statusValue(collector)
    case 'lastPolledAt': return dateValue(collector.lastPolledAt)
    case 'lastSuccessAt': return dateValue(collector.lastSuccessAt)
    case 'lastMessageAt': return dateValue(collector.lastMessageAt)
    case 'messages24h': return collector.messages24h
    case 'lastError': return collector.lastError || null
  }
}

/** Stable client-side sort for the collectors table. Missing values always stay last. */
export function sortCollectors(list: CollectorStatusDto[], key: CollectorSortKey, asc: boolean): CollectorStatusDto[] {
  return [...list].sort((a, b) => {
    const first = value(a, key)
    const second = value(b, key)
    if (first === second) return a.name.localeCompare(b.name, 'uk') || a.sourceId - b.sourceId
    if (first === null) return 1
    if (second === null) return -1
    const comparison = typeof first === 'string' && typeof second === 'string'
      ? first.localeCompare(second, 'uk')
      : (first as number) - (second as number)
    return asc ? comparison : -comparison
  })
}

const QUIET_AFTER_MS = 24 * 3_600_000

/**
 * "Тиша N дн" when the source's newest message is older than a day: the state badge speaks only of polling, so a quiet
 * channel with a healthy collector must not read as fresh content. Null while fresh or never received.
 */
export function quietLabel(lastMessageAt?: string | null, now: number = Date.now()): string | null {
  if (!lastMessageAt) return null
  const at = Date.parse(lastMessageAt)
  if (Number.isNaN(at) || now - at < QUIET_AFTER_MS) return null
  return `тиша ${Math.floor((now - at) / 86_400_000)} дн`
}
