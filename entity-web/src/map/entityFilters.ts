import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import type { Filters } from '../store/useStore'
import { isRetiredEntity } from '../entities/presentation'

export function filterEntityItems(items: EntityItem[], filters: Filters, definitions: EntityDefinition[] = [], referenceTime = new Date(), kinds: string[] = [], from?: Date, to?: Date, searchQuery = '') {
  const byName = new Map(definitions.map((definition) => [definition.entityName, definition]))
  const search = searchQuery.trim().toLocaleLowerCase('uk-UA')
  return items.filter((item) => {
    if (isRetiredEntity(item.entity, item.table)) return false
    if (kinds.length && !kinds.some((kind) => kind.toLowerCase() === item.entity.toLowerCase() || kind.toLowerCase() === item.table.toLowerCase())) return false
    // An event without a time can never leave a time window: it has no place on the map.
    if (!item.occurredAt) return false
    const occurredAt = new Date(item.occurredAt).getTime()
    if (!Number.isFinite(occurredAt) || occurredAt > referenceTime.getTime()) return false
    if (from && occurredAt < from.getTime()) return false
    if (to && occurredAt >= to.getTime()) return false
    if (search && !JSON.stringify([item.entity, item.table, item.id, item.rawMessageId, item.values]).toLocaleLowerCase('uk-UA').includes(search)) return false
    if (filters.sources?.length && (item.sourceId === undefined || !filters.sources.includes(item.sourceId))) return false
    const kind = item.entity.toLowerCase()
    if ((kind === 'alert' || kind === 'alerts') && !filters.alerts) return false
    if (['impact', 'explosion', 'airdefenseaction', 'launch', 'takeoff'].includes(kind.replaceAll('_', '')) && !filters.events) return false
    const type = String(item.values.targetType ?? item.values.aircraftType ?? item.values.type ?? '').toLowerCase()
    if ((type.includes('uav') || type.includes('drone') || type.includes('shahed')) && !filters.uav) return false
    if (type.includes('cruise') && !filters.cruise) return false
    if (type.includes('ballistic') && !filters.ballistic) return false
    if ((type.includes('aircraft') || type.includes('plane') || type.includes('aviation')) && !filters.aircraft) return false
    const statusField = byName.get(item.entity)?.map.statusField
    const status = item.values[statusField ?? 'status'] ?? item.values.active
    if (filters.activeOnly && status !== undefined) {
      const normalized = String(status).toLowerCase()
      if (!(status === true || ['active', 'open', 'ongoing', 'true'].includes(normalized))) return false
    }
    // A state (an alert) lasts until its own next row ends it; the server keeps only the latest row per key, so
    // the viewer's marker lifetime would hide an alert that is still on. The definition's lifetime bounds it instead.
    const definition = byName.get(item.entity)
    const lifetimeMinutes = definition?.map.keyField ? definition.map.lifetimeMinutes : filters.lifetimeMinutes
    if (lifetimeMinutes && referenceTime.getTime() - occurredAt > lifetimeMinutes * 60_000) return false
    return true
  })
}
