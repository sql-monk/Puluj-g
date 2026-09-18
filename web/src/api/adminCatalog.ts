import { adminCall } from './admin'

/** Admin-only event catalog client. */

export interface AdminKindDto {
  eventKindId: number
  code: string
  nameUk: string
  category: string
  defaultSeverity?: string
  stateModel?: string
  requiresLocationForMap: boolean
  renderMode?: string
  mapColor?: string
  mapIcon?: string
  mapLifetime?: string
  enabled: boolean
  mapVisible: boolean
  sortOrder: number
  policyVersion: number
  presentationOverriddenAt?: string
  legacyEventType?: string
  metadata?: unknown
}

export interface AdminCatalogDto {
  iconVocabulary: string[]
  renderModes: string[]
  kinds: AdminKindDto[]
}

export interface KindUpdate {
  nameUk?: string
  mapVisible?: boolean
  mapColor?: string
  mapIcon?: string
  mapLifetime?: string
  renderMode?: string
  sortOrder?: number
  enabled?: boolean
  requiresLocationForMap?: boolean
  actor: string
  reason: string
  force?: boolean
}

export interface KindAuditDto {
  auditId: number
  action: string
  actor: string
  reason: string
  at: string
  before?: Record<string, unknown>
  after?: Record<string, unknown>
}

const enc = encodeURIComponent

export const adminCatalog = {
  kinds: () => adminCall<AdminCatalogDto>('GET', '/api/admin/event-kinds'),
  update: (code: string, patch: KindUpdate) => adminCall<AdminKindDto>('PUT', `/api/admin/event-kinds/${enc(code)}`, patch),
  audit: (code: string) => adminCall<KindAuditDto[]>('GET', `/api/admin/event-kinds/${enc(code)}/audit`),
}
