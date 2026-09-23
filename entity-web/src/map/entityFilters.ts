import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import type { Filters } from '../store/useStore'

export function filterEntityItems(items: EntityItem[], filters: Filters, definitions: EntityDefinition[] = [], referenceTime = new Date(), kinds: string[] = [], from?: Date, to?: Date, searchQuery = '') {
  const byName = new Map(definitions.map((definition) => [definition.entityName, definition]))
  const search = searchQuery.trim().toLocaleLowerCase('uk-UA')
  return items.filter((item) => {
    if (kinds.length && !kinds.some((kind) => kind.toLowerCase() === item.entity.toLowerCase() || kind.toLowerCase() === item.table.toLowerCase())) return false
    const occurredAt = item.occurredAt ? new Date(item.occurredAt).getTime() : undefined
    if (occurredAt !== undefined && (!Number.isFinite(occurredAt) || occurredAt > referenceTime.getTime())) return false
    if (from && (occurredAt === undefined || occurredAt < from.getTime())) return false
    if (to && (occurredAt === undefined || occurredAt >= to.getTime())) return false
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
    if (occurredAt !== undefined && referenceTime.getTime() - occurredAt > filters.lifetimeMinutes * 60_000) return false
    return true
  })
}
