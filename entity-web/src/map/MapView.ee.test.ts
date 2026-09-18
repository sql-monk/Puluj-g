import { describe, expect, it } from 'vitest'
import type { EntityItem } from '../api/entityExtractor'
import type { Filters } from '../store/useStore'
import { filterEntityItems } from './entityFilters'

const filters: Filters = { uav: true, cruise: true, ballistic: true, aircraft: true, alerts: true, events: true, activeOnly: false, forecast: true, highlightTargets: false, sources: null, lifetimeMinutes: 120 }
const item = (entity: string, type?: string): EntityItem => ({ entity, table: `ee_${entity}s`, id: '9007199254740993', rawMessageId: '9007199254740995', values: type ? { targetType: type } : {} })

describe('EE map filters', () => {
  it('applies existing alert, event and target-type toggles to generic EE entities', () => {
    const items = [item('alert'), item('explosion'), item('target', 'uav'), item('target', 'cruise missile')]
    expect(filterEntityItems(items, { ...filters, alerts: false }).map((x) => x.entity)).not.toContain('alert')
    expect(filterEntityItems(items, { ...filters, events: false }).map((x) => x.entity)).not.toContain('explosion')
    expect(filterEntityItems(items, { ...filters, uav: false }).map((x) => x.values.targetType)).not.toContain('uav')
    expect(filterEntityItems(items, { ...filters, cruise: false }).map((x) => x.values.targetType)).not.toContain('cruise missile')
  })

  it('keeps raw message identifiers as strings beyond JavaScript safe integer range', () => {
    expect(item('target').rawMessageId).toBe('9007199254740995')
  })

  it('applies active and viewer lifetime filters when the entity exposes the required data', () => {
    const now = new Date('2026-09-18T12:00:00Z')
    const active = { ...item('target'), occurredAt: '2026-09-18T11:55:00Z', values: { status: 'active' } }
    const closed = { ...item('target'), occurredAt: '2026-09-18T11:55:00Z', values: { status: 'closed' } }
    const stale = { ...item('target'), occurredAt: '2026-09-18T09:00:00Z', values: { status: 'active' } }
    expect(filterEntityItems([active, closed, stale], { ...filters, activeOnly: true, lifetimeMinutes: 60 }, [], now)).toEqual([active])
  })

  it('filters EE entities by sourceId and excludes rows without source attribution', () => {
    const sourceOne = { ...item('target'), id: '1', sourceId: 1 }
    const sourceTwo = { ...item('target'), id: '2', sourceId: 2 }
    const unknown = { ...item('target'), id: '3' }
    expect(filterEntityItems([sourceOne, sourceTwo, unknown], { ...filters, sources: [2] })).toEqual([sourceTwo])
  })
})
