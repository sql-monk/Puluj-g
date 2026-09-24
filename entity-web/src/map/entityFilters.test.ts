import { describe, expect, it } from 'vitest'
import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import type { Filters } from '../store/useStore'
import { filterEntityItems } from './entityFilters'

const now = new Date('2026-09-24T12:00:00Z')
const filters: Filters = { uav: true, cruise: true, ballistic: true, aircraft: true, alerts: true, events: true, activeOnly: false, forecast: true, highlightTargets: false, sources: null, lifetimeMinutes: 120 }
const row = (id: string, occurredAt?: string): EntityItem => ({ entity: 'target', table: 'ee_targets', id, sourceId: 2, occurredAt, values: { place: 'Київ', detail: { label: 'Shahed' } } })

describe('EE map search and time boundaries', () => {
  it('uses a half-open interval including from and excluding to', () => {
    const from = new Date('2026-09-24T11:00:00Z')
    const to = new Date('2026-09-24T11:30:00Z')
    const rows = [row('before', '2026-09-24T10:59:59Z'), row('from', from.toISOString()), row('inside', '2026-09-24T11:29:59Z'), row('to', to.toISOString()), row('missing'), row('invalid', 'invalid')]
    expect(filterEntityItems(rows, filters, [], now, [], from, to).map(x => x.id)).toEqual(['from', 'inside'])
  })

  it('excludes future, malformed and missing timestamps without requiring an explicit period', () => {
    const rows = [row('now', now.toISOString()), row('future', '2026-09-24T12:00:00.001Z'), row('invalid', 'bad'), row('undated')]
    expect(filterEntityItems(rows, filters, [], now).map(x => x.id)).toEqual(['now'])
  })

  it('keeps a keyed state for its definition lifetime, not the viewer marker lifetime', () => {
    const alerts = { entityName: 'alert', map: { keyField: 'stateKey', lifetimeMinutes: 1440 } } as EntityDefinition
    const alert = (id: string, occurredAt: string): EntityItem => ({ entity: 'alert', table: 'ee_alerts', id, occurredAt, values: { status: 'active' } })
    const rows = [alert('three-hours', '2026-09-24T09:00:00Z'), alert('two-days', '2026-09-22T12:00:00Z'), row('target-three-hours', '2026-09-24T09:00:00Z')]
    expect(filterEntityItems(rows, filters, [alerts], now).map(x => x.id)).toEqual(['three-hours'])
  })

  it('uses the replay reference instead of wall-clock time', () => {
    const at = new Date('2026-09-24T11:00:00Z')
    expect(filterEntityItems([row('at', at.toISOString()), row('later', '2026-09-24T11:01:00Z')], filters, [], at).map(x => x.id)).toEqual(['at'])
  })

  it('searches case-insensitively across text, nested values and opaque IDs', () => {
    const rows = [row('9007199254740993', '2026-09-24T11:59:00Z')]
    for (const q of [' КИЇВ ', 'shaHED', '9007199254740993']) expect(filterEntityItems(rows, filters, [], now, [], undefined, undefined, q)).toEqual(rows)
    expect(filterEntityItems(rows, filters, [], now, [], undefined, undefined, 'absent')).toEqual([])
    expect(filterEntityItems(rows, filters, [], now, [], undefined, undefined, ' ')).toEqual(rows)
  })

  it('combines search with existing source, kind, status and lifetime filters', () => {
    const definition = { entityName: 'target', map: { statusField: 'state' } } as EntityDefinition
    const active = { ...row('active', '2026-09-24T11:30:00Z'), values: { label: 'Київ', state: 'active' } }
    const rows = [active, { ...active, id: 'wrong-source', sourceId: 3 }, { ...active, id: 'wrong-kind', entity: 'alert', table: 'ee_alerts' }, { ...active, id: 'closed', values: { label: 'Київ', state: 'closed' } }, { ...active, id: 'old', occurredAt: '2026-09-24T09:00:00Z' }]
    expect(filterEntityItems(rows, { ...filters, activeOnly: true, sources: [2] }, [definition], now, ['EE_TARGETS'], undefined, undefined, 'Київ')).toEqual([active])
  })
})
