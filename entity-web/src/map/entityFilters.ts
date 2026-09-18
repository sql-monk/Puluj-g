import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import type { Filters } from '../store/useStore'

export function filterEntityItems(items: EntityItem[], filters: Filters, definitions: EntityDefinition[] = [], referenceTime = new Date(), kinds: string[] = [], from?: Date, to?: Date) {
  const byName = new Map(definitions.map((definition) => [definition.entityName, definition]))
  return items.filter((item) => {
    if (kinds.length && !kinds.some((kind) => kind.toLowerCase() === item.entity.toLowerCase() || kind.toLowerCase() === item.table.toLowerCase())) return false
    if (from && (!item.occurredAt || new Date(item.occurredAt) < from)) return false
    if (to && (!item.occurredAt || new Date(item.occurredAt) > to)) return false
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
    if (item.occurredAt && referenceTime.getTime() - new Date(item.occurredAt).getTime() > filters.lifetimeMinutes * 60_000) return false
    return true
  })
}
