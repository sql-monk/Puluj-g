import { adminCall } from './admin'

/** P13 (§9, §8.7, ADR-0012): message-platform ops snapshot, lane/quarantine/scale controls, message explorer — admin-only clients over /api/admin/*. */

export interface ConsumerLaneDto {
  subscription: string
  lane: string
  queue: string
  state: string
  consuming: boolean
  inFlight: number
  prefetch: number
  consumerTag: string
  delivered: number
  duplicates: number
  requeued: number
}

export interface LaneStateDto {
  state: 'active' | 'paused' | 'draining' | string
  reason?: string | null
  actor?: string | null
  changedAt?: string | null
}

export interface SubscriptionLaneOpsDto {
  subscription: string
  lane: string
  required: boolean
  registryStatus: string
  laneState: LaneStateDto
  pending: number
  inFlight: number
  retryHour: number
  adminRetryHour: number
  quarantined: number
  oldestPendingAgeSeconds?: number | null
  eventTimeLagSeconds?: number | null
  oldestRunningAttemptAgeSeconds?: number | null
  waitP50Ms?: number | null
  waitP95Ms?: number | null
  waitP99Ms?: number | null
  processingP50Ms?: number | null
  processingP95Ms?: number | null
  processingP99Ms?: number | null
  expectedHour: number
  completedHour: number
  noopHour: number
  failedHour: number
  expected5m: number
  completed5m: number
  consumers: number
  ready?: number | null
  unacked?: number | null
  brokerConsumers?: number | null
  source: 'db' | 'management' | string
}

export interface AlarmDto {
  code: string
  severity: 'info' | 'warn' | 'error' | string
  scope: string
  message: string
}

export interface WorkerOpsDto {
  name: string
  heartbeatAt?: string | null
  statusAt?: string | null
  stale: boolean
  stuck: boolean
  runningAttempts: number
  completedHour: number
  lastSuccessAt?: string | null
  lastErrorAt?: string | null
  lastError?: string | null
  roles: string[]
  broker?: { connected: boolean; endpoint: string } | null
  llm?: { enabled: boolean; model: string; pausedUntil?: string | null; pauseReason?: string | null; calls: number; failures: number } | null
  consumers: ConsumerLaneDto[]
}

export interface MessagingOpsDto {
  at: string
  topologyVersion: number
  subscriptions: SubscriptionLaneOpsDto[]
  roots: { receivedHour: number; completedHour: number; pending: number; needsAttention: number }
  broker: { connected?: boolean | null; connectedWorkers: string[]; disconnectedWorkers: string[]; management?: { available: boolean; reason?: string | null; nodes: { name: string; running: boolean; memAlarm: boolean; diskAlarm: boolean }[] } | null }
  outbox: { unconfirmed: number; oldestAgeSeconds?: number | null; relayRetries: number; unroutable: number; confirmP50Ms?: number | null; confirmP95Ms?: number | null; publishedHour: number }
  inbox: { rowsHour: number; supersededHour: number; processing: number }
  reconciliation?: { at: string; worker: string; outboxUnconfirmed: number; outboxOldestAgeSeconds: number; overdueCount: number; unknownSubscriptions: string[]; quarantineOpen: number; declareFailed: string[]; outboxDeleted: number; inboxDeleted: number } | null
  workers: WorkerOpsDto[]
  backfill: { source: string; status?: string | null; checkpoint?: string | null; lastSuccessAt?: string | null; consecutiveFailures: number; lastError?: string | null }[]
  alarms: AlarmDto[]
  slo: { oldestAgeSeconds: Record<string, number>; outboxUnconfirmedSeconds: number; outboxCriticalSeconds: number; staleHeartbeatSeconds: number; inflightStuckSeconds: number; requiredConsumerMissingSeconds: number }
}

export interface QuarantineRowDto {
  quarantineId: number
  subscriptionId: string
  eventId: string
  lane: string
  reason: string
  error?: string | null
  quarantinedAt: string
  resolvedAt?: string | null
  resolvedBy?: string | null
  resolution?: string | null
  eventType?: string | null
  rawMessageId?: number | null
}

export interface ControlAuditDto {
  auditId: number
  action: string
  subscriptionId?: string | null
  lane?: string | null
  actor: string
  reason: string
  at: string
  details?: unknown
}

export interface MessageSearchRowDto {
  rawMessageId: number
  sourceId: number
  sourceCode: string
  sourceMessageId: string
  publishedAt: string
  receivedAt: string
  status: string
  extractions: number
  observations: number
  targets: number
  llmCalls: number
  analysisOutcome?: string | null
  method?: string | null
  lastOutcome?: string | null
  textPreview: string
  reactions: { kind: string; value: string; count: number }[]
}

export interface MessageSearchPageDto {
  items: MessageSearchRowDto[]
  totalCount: number
  page: number
  pageSize: number
}

export interface LifecycleAttemptDto {
  attemptId: number
  worker: string
  state: string
  startedAt: string
  finishedAt?: string | null
  error?: string | null
  retryOfAttemptId?: number | null
  retryReason?: string | null
}

export interface LifecycleDeliveryDto {
  subscription: string
  lane?: string | null
  expectedAt: string
  outcome?: string | null
  completedAt?: string | null
  reason?: string | null
  actor?: string | null
  attempts: LifecycleAttemptDto[]
  attemptsTruncated: boolean
}

export interface LifecycleEventDto {
  eventId: string
  eventType: string
  lane: string
  occurredAt: string
  publishedAt?: string | null
  confirmedAt?: string | null
  causationId?: string | null
  producer: string
  deliveries: LifecycleDeliveryDto[]
}

export interface MessageLifecycleDto {
  rawMessageId: number
  sourceId: number
  sourceCode: string
  sourceMessageId: string
  publishedAt: string
  receivedAt: string
  status: string
  text?: string | null
  url?: string | null
  events: LifecycleEventDto[]
  eventsTruncated: boolean
  extractions: { extractionId: string; runId: string; version: number; method: string; outcome: string; versions?: string | null; finalizedBy: string; createdAt: string; observations: number; error?: string | null }[]
  observations: { observationId: string; extractionId: string; kind: string; category: string; effectiveAt: string; payloadPreview: string; legacyTargetId?: number | null }[]
  targets: LifecycleTargetDto[]
  llmRequests: LifecycleLlmRequestDto[]
  derived: { kind: string; id: number; label: string }[]
  quarantine: { quarantineId: number; subscriptionId: string; lane: string; reason: string; error?: string | null; quarantinedAt: string; resolvedAt?: string | null; resolution?: string | null; envelopePreview: string; envelopeTruncated: boolean }[]
  summary: { completion: string; waiting: string[]; completed: string[]; failed: string[] }
}

export interface LifecycleTargetDto {
  targetId: number
  segmentIndex: number
  eventType: string
  eventKind?: string | null
  observedAt: string
  objectCount?: number | null
  classification?: string | null
  location?: string | null
  locationAccuracyKm?: number | null
  duplicateOfTargetId?: number | null
}

export interface LifecycleLlmRequestDto {
  llmRequestId: number
  occurredAt: string
  model: string
  promptVersion: string
  outcome: string
  statusCode?: number | null
  durationMs: number
  inputTokens?: number | null
  cacheWriteTokens?: number | null
  cacheReadTokens?: number | null
  outputTokens?: number | null
  estimatedCostUsd?: number | null
  factsCount: number
  error?: string | null
}

export type MessageView = 'all' | 'ignored' | 'llm' | 'targets' | 'events' | 'failed'

export const adminOps = {
  messaging: (fresh = false) => adminCall<MessagingOpsDto>('GET', `/api/admin/ops/messaging${fresh ? '?fresh=true' : ''}`),
  quarantine: (subscription?: string, open = true, limit = 100) =>
    adminCall<QuarantineRowDto[]>('GET', `/api/admin/ops/messaging/quarantine?open=${open}&limit=${limit}${subscription ? `&subscription=${encodeURIComponent(subscription)}` : ''}`),
  audit: (limit = 100) => adminCall<ControlAuditDto[]>('GET', `/api/admin/ops/messaging/audit?limit=${limit}`),
  setLane: (subscription: string, lane: string, state: 'active' | 'paused' | 'draining') =>
    adminCall<{ ok: boolean; scope: string; state: string; lane?: SubscriptionLaneOpsDto | null }>('POST', `/api/admin/ops/messaging/lanes/${encodeURIComponent(subscription)}/${encodeURIComponent(lane)}`, { state }),
  retry: (quarantineId: number) => adminCall<{ ok: boolean; outboxId: number }>('POST', `/api/admin/ops/messaging/quarantine/${quarantineId}/retry`, {}),
  waive: (quarantineId: number) => adminCall<{ ok: boolean; waived: number }>('POST', `/api/admin/ops/messaging/quarantine/${quarantineId}/waive`, {}),
  scale: (service: 'processor' | 'messaging', replicas: number) =>
    adminCall<{ ok: boolean; message: string; output: string }>('POST', '/api/admin/ops/messaging/scale', { service, replicas }),
  search: (q: string, hours: number, view: MessageView = 'all', sourceIds: number[] = [], page = 1, sort = 'receivedAt', direction: 'asc' | 'desc' = 'desc', limit = 100) => {
    const sources = sourceIds.map((id) => `sourceIds=${encodeURIComponent(id)}`).join('&')
    return adminCall<MessageSearchPageDto>('GET', `/api/admin/messages?q=${encodeURIComponent(q)}&hours=${hours}&view=${view}&limit=${limit}&page=${page}&sort=${encodeURIComponent(sort)}&direction=${direction}${sources ? `&${sources}` : ''}`)
  },
  lifecycle: (rawId: number) => adminCall<MessageLifecycleDto>('GET', `/api/admin/messages/${rawId}/lifecycle`),
}
