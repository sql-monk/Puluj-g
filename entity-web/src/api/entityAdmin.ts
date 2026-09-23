import { adminCall, type ServiceStatusDto } from './admin'

export interface EeExtractor { extractor_id: number; name: string; code: string; enabled: boolean; execution_order: number; timeout_ms: number }
export interface EeField { name: string; type: string; required: boolean }
export interface EeMapConfig { enabled: boolean; renderer: 'point' | 'icon' | 'line' | 'polygon'; geometryField?: string; latitudeField?: string; longitudeField?: string; labelField?: string; timeField?: string; statusField?: string; lifetimeMinutes?: number; svg?: string; color?: string; width?: number; opacity?: number; dash?: string }
export interface EeDefinition { entity_definition_id: number; entity_name: string; table_name: string; fields: EeField[]; map_settings?: EeMapConfig; enabled: boolean }

/** What the delivery queue holds now. `failed` is terminal (a failed delivery is not retried). */
export interface EeQueueSnapshot {
  queued: number
  inProgress: number
  failed: number
  failedLastHour: number
  failedLast24h: number
  lastFailureAt?: string
  lastError?: string
  oldestQueuedAt?: string
  lastSuccessAt?: string
}
export interface EeOverview {
  queue: EeQueueSnapshot
  extractors: number
  definitions: number
  activeRuns: number
  extractor: { available: boolean; statusCode?: number; error?: string }
  llmEnabled: boolean
  status: ServiceStatusDto
}
/** One EntityExtractor:* setting: the stored value (if any), where the applied one comes from, and what it means. */
export interface EeSetting { key: string; label: string; value?: string; source: 'db' | 'config' | 'default'; effective: string; default: string; format: string; hint: string }

export type EeDeliveryStatus = 'pending' | 'in_progress' | 'succeeded' | 'failed'
export interface EeDelivery {
  delivery_id: string
  raw_message_id: number
  source_id: number
  source_code: string
  origin: string
  status: EeDeliveryStatus
  enqueued_at: string
  claimed_at?: string
  completed_at?: string
  claimed_by?: string
  /** 1 — entities were written, 0 — nothing to write (not an error). */
  result?: number
  attempts: number
  last_error?: string
  run_error?: string
}
export interface EeRun {
  processing_run_id: number
  delivery_id: string
  raw_message_id: number
  source_id: number
  source_code: string
  status: 'processing' | 'completed' | 'failed' | string
  result?: number
  started_at: string
  completed_at?: string
  error?: string
}

export const entityAdmin = {
  overview: () => adminCall<EeOverview>('GET', '/api/admin/ee/overview'),
  settings: () => adminCall<EeSetting[]>('GET', '/api/admin/ee/settings'),
  saveSettings: (values: Record<string, string | null>) => adminCall<{ saved: number }>('PUT', '/api/admin/ee/settings', { values }),
  extractors: () => adminCall<EeExtractor[]>('GET', '/api/admin/ee/extractors'),
  saveExtractor: (extractor: { extractorId?: number; name: string; code: string; enabled: boolean; executionOrder: number; timeoutMs: number }) => adminCall<EeExtractor>('PUT', '/api/admin/ee/extractors', extractor),
  deleteExtractor: (id: number) => adminCall<void>('DELETE', `/api/admin/ee/extractors/${id}`),
  validate: (code: string) => adminCall<Record<string, unknown>>('POST', '/api/admin/ee/extractors/validate', { code }),
  test: (code: string, text: string, timeoutMs?: number) => adminCall<Record<string, unknown>>('POST', '/api/admin/ee/extractors/test', { code, message: { rawMessageId: 'test', text }, timeoutMs }),
  definitions: () => adminCall<EeDefinition[]>('GET', '/api/admin/ee/definitions'),
  createDefinition: (request: { entityName: string; fields: EeField[]; map: EeMapConfig; enabled: boolean }) => adminCall<EeDefinition>('POST', '/api/admin/ee/definitions', request),
  updateDefinitionMap: (id: number, map: EeMapConfig) => adminCall<void>('PUT', `/api/admin/ee/definitions/${id}/map`, map),
  deliveries: (status: EeDeliveryStatus | 'all', cursor?: string, limit = 50) => {
    const query = new URLSearchParams({ status, limit: String(limit) })
    if (cursor) query.set('cursor', cursor)
    return adminCall<{ items: EeDelivery[]; nextCursor?: string }>('GET', `/api/admin/ee/deliveries?${query}`)
  },
  runs: (beforeId?: number, limit = 50) => adminCall<{ items: EeRun[]; nextBeforeId?: number }>('GET', `/api/admin/ee/runs?limit=${limit}${beforeId === undefined ? '' : `&beforeId=${beforeId}`}`),
  enqueue: (rawMessageId: string) => adminCall<unknown>('POST', `/api/admin/ee/enqueue/${encodeURIComponent(rawMessageId)}`, {}),
}
