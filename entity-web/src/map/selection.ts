import { useState } from 'react'
import type { EntityItem } from '../api/entityExtractor'

type EntityIdentity = Pick<EntityItem, 'entity' | 'id'>

export function snapshotReferenceTime(history: boolean, now: Date, at?: Date | null, snapshotAt?: string): Date {
  if (!history) return now
  const received = snapshotAt ? new Date(snapshotAt) : undefined
  return received && Number.isFinite(received.getTime()) ? received : at ?? now
}

/** Keep identity only: refreshed values come from the same filtered snapshot as the map. */
export function useEntitySelection(items: EntityItem[]) {
  const [identity, setIdentity] = useState<EntityIdentity | null>(null)
  const selected = identity ? items.find((item) => item.entity === identity.entity && item.id === identity.id) : undefined
  // Clear during reconciliation so removing a filter cannot resurrect a dismissed selection.
  if (identity && !selected) setIdentity(null)
  const select = (item: EntityIdentity | null) => setIdentity(item ? { entity: item.entity, id: item.id } : null)
  return { selected, select }
}
