import { adminCall } from './admin'

export interface EeExtractor { extractor_id: number; name: string; code: string; enabled: boolean; execution_order: number; timeout_ms: number }
export interface EeField { name: string; type: string; required: boolean }
export interface EeMapConfig { enabled: boolean; renderer: 'point' | 'icon' | 'line' | 'polygon'; geometryField?: string; latitudeField?: string; longitudeField?: string; labelField?: string; timeField?: string; statusField?: string; lifetimeMinutes?: number; svg?: string; color?: string; width?: number; opacity?: number; dash?: string }
export interface EeDefinition { entity_definition_id: number; entity_name: string; table_name: string; fields: EeField[]; map_settings?: EeMapConfig; enabled: boolean }

export const entityAdmin = {
  overview: () => adminCall<Record<string, unknown>>('GET', '/api/admin/ee/overview'),
  settings: () => adminCall<Record<string, string | null>>('GET', '/api/admin/ee/settings'),
  saveSettings: (values: Record<string, string | null>) => adminCall<{ saved: number }>('PUT', '/api/admin/ee/settings', { values }),
  extractors: () => adminCall<EeExtractor[]>('GET', '/api/admin/ee/extractors'),
  saveExtractor: (extractor: { extractorId?: number; name: string; code: string; enabled: boolean; executionOrder: number; timeoutMs: number }) => adminCall<EeExtractor>('PUT', '/api/admin/ee/extractors', extractor),
  deleteExtractor: (id: number) => adminCall<void>('DELETE', `/api/admin/ee/extractors/${id}`),
  validate: (code: string) => adminCall<Record<string, unknown>>('POST', '/api/admin/ee/extractors/validate', { code }),
  test: (code: string, text: string, timeoutMs?: number) => adminCall<Record<string, unknown>>('POST', '/api/admin/ee/extractors/test', { code, message: { rawMessageId: 'test', text }, timeoutMs }),
  definitions: () => adminCall<EeDefinition[]>('GET', '/api/admin/ee/definitions'),
  createDefinition: (request: { entityName: string; fields: EeField[]; map: EeMapConfig; enabled: boolean }) => adminCall<EeDefinition>('POST', '/api/admin/ee/definitions', request),
  updateDefinitionMap: (id: number, map: EeMapConfig) => adminCall<void>('PUT', `/api/admin/ee/definitions/${id}/map`, map),
  deliveries: (limit = 100) => adminCall<Record<string, unknown>[]>('GET', `/api/admin/ee/deliveries?limit=${limit}`),
  runs: (limit = 100) => adminCall<Record<string, unknown>[]>('GET', `/api/admin/ee/runs?limit=${limit}`),
  enqueue: (rawMessageId: string) => adminCall<unknown>('POST', `/api/admin/ee/enqueue/${encodeURIComponent(rawMessageId)}`, {}),
}
