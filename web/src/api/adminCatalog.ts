import { adminCall } from './admin'

/** P12 (§8.7): the catalog editor and the incident review queue — admin-only clients over /api/admin/*. */

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
  createsIncident: boolean
  enabled: boolean
  mapVisible: boolean
  sortOrder: number
  policyVersion: number
  presentationOverriddenAt?: string
  legacyEventType?: string
  dedupPolicy?: unknown
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

export interface ReviewCandidateDto {
  incidentId: number
  kind: string
  state: string
  suppressed: boolean
  mergedIntoIncidentId?: number
  revision: number
  sameKind: boolean
  mergeable: boolean
}

export interface ReviewItemDto {
  incidentId: number
  kind: string
  state: string
  suppressed: boolean
  eventAt: string
  lastReportedAt: string
  locationPlaceId?: number
  accuracyKm?: number
  sourceCount: number
  revision: number
  flags: { observationId: string; relation: string; ambiguous: number[]; near: number[] }[]
  candidates: ReviewCandidateDto[]
}

export interface MergePreviewDto {
  sourceId: number
  targetId: number
  allowed: boolean
  refusal?: string
  movedObservationIds: string[]
  sourceCountAfter: number
  firstReportedAtAfter: string
  lastReportedAtAfter: string
  stateAfter: string
  locationFrom?: string
  accuracyKmAfter?: number
  confidenceAfter: string
  targetRevision: number
  sourceRevision: number
}

export interface AdminIncidentDto {
  incidentId: number
  kind: string
  state: string
  suppressed: boolean
  eventAt: string
  firstReportedAt: string
  lastReportedAt: string
  locationPlaceId?: number
  accuracyKm?: number
  sourceCount: number
  revision: number
  closureReason?: string
  mergedIntoIncidentId?: number
  observations: { observationId: string; legacyTargetId?: number; sourceId: number; sourceCode?: string; relation: string; score: number; effectiveAt: string; segmentText?: string; rawText?: string; rawUrl?: string; decisionReason?: unknown }[]
  revisions: { revision: number; change: string; effectiveAt: string; recordedAt: string; actor: string; reason?: string }[]
}

export interface CommandResult {
  incidentId: number
  change: string
  state: string
  suppressed: boolean
  revision: number
}

const enc = encodeURIComponent

export const adminCatalog = {
  kinds: () => adminCall<AdminCatalogDto>('GET', '/api/admin/event-kinds'),
  update: (code: string, patch: KindUpdate) => adminCall<AdminKindDto>('PUT', `/api/admin/event-kinds/${enc(code)}`, patch),
  audit: (code: string) => adminCall<KindAuditDto[]>('GET', `/api/admin/event-kinds/${enc(code)}/audit`),
}

export const adminIncidents = {
  review: (hours = 24) => adminCall<{ since: string; count: number; truncated: boolean; items: ReviewItemDto[] }>('GET', `/api/admin/incidents/review?hours=${hours}`),
  get: (id: number) => adminCall<AdminIncidentDto>('GET', `/api/admin/incidents/${id}`),
  mergePreview: (id: number, targetId: number) => adminCall<MergePreviewDto>('GET', `/api/admin/incidents/${id}/merge-preview?targetId=${targetId}`),
  merge: (sourceId: number, targetId: number, actor: string, reason: string) => adminCall<CommandResult[]>('POST', '/api/admin/incidents/merge', { sourceId, targetId, actor, reason }),
  split: (id: number, observationIds: string[], actor: string, reason: string) => adminCall<CommandResult[]>('POST', `/api/admin/incidents/${id}/split`, { observationIds, actor, reason }),
  command: (id: number, action: 'resolve' | 'retract' | 'confirm' | 'suppress' | 'unsuppress', actor: string, reason: string) => adminCall<CommandResult>('POST', `/api/admin/incidents/${id}/${action}`, { actor, reason }),
}
