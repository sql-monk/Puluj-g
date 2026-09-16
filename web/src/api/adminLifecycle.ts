import { adminCall } from './admin'

/** P15 (§10, ADR-0013): the message lifecycle analytics — admin-only client over /api/admin/analytics/lifecycle*. */

export interface LifecycleReconciliationDto {
  at: string
  windowHours: number
  rawRows: number
  posts: number
  edits: number
  projectedRaw: number
  projectedPosts: number
  missingRoots: number
  lateAnalyses: number
  lateCompletions: number
  pendingAnalysis: number
  pendingDomain: number
  unavailableTimings: number
  unavailableCompletion: number
}

export interface LifecycleStatusDto {
  available: boolean
  backfill: { cursor: number; maxRawMessageId: number; caughtUp: boolean; at?: string | null }
  reconciliation?: LifecycleReconciliationDto | null
}

export interface LifecycleBucketDto {
  at: string
  raw: number
  analyzed: number
  withFacts: number
  domainCompleted: number
  failed: number
  noText: number
}

export interface LifecycleSourceDto {
  sourceId: number
  code: string
  raw: number
  posts: number
  edits: number
  noText: number
  withPayload: number
  facts: number
  textLengthP50?: number | null
  collectDelayP50Seconds?: number | null
  collectDelayP95Seconds?: number | null
  live: number
  history: number
  maxGapSeconds?: number | null
}

export interface LifecycleFunnelDto {
  raw: number
  posts: number
  stored: number
  analyzed: number
  withFacts: number
  domainCompleted: number
  visible: number
  stuckAnalysis: number
  stuckDomain: number
  unavailableTimings: number
  unavailableCompletion: number
  storedToAnalyzedP50Seconds?: number | null
  storedToAnalyzedP95Seconds?: number | null
  analyzedToDomainP50Seconds?: number | null
  analyzedToDomainP95Seconds?: number | null
}

export interface LifecycleCostModelDto {
  model: string
  calls: number
  inputTokens: number
  cacheTokens: number
  outputTokens: number
  costUsd: number
  latencyP50Ms?: number | null
  latencyP95Ms?: number | null
  failures: number
  late: number
}

export interface LifecycleReportDto {
  from: string
  to: string
  hours: number
  bucket: 'hour' | 'day' | string
  funnel: LifecycleFunnelDto
  timeline: LifecycleBucketDto[]
  sources: LifecycleSourceDto[]
  parse: { outcomes: Record<string, number>; methods: Record<string, number>; multiFact: number; unlocated: number; totalFacts: number; ruleVersions: Record<string, number>; modelVersions: Record<string, number> }
  quality: { reviewOutcomes: Record<string, number>; precisionSamples?: number | null; precisionRecall: string; rulesVsLlm: string }
  cost: { calls: number; roots: number; costUsd: number; inputTokens: number; cacheTokens: number; outputTokens: number; cacheShare?: number | null; byModel: LifecycleCostModelDto[]; bySource: { key: string; value: number }[] }
  results: { eventKinds: Record<string, number>; incidentPrecision: Record<string, number>; incidents: number; tracks: number; alerts: number; incidentsWithProvenance: number; activeIncidents: number }
  history: { runs: { runId: string; kind: string; pipelineVersion?: string | null; rows: number; outcomes: Record<string, number> }[]; activeGenerationIncidents: number; incidentsByGeneration: { key: string; value: number }[] }
  reconciliation?: LifecycleReconciliationDto | null
}

export const adminLifecycle = {
  report: (hours: 24 | 168 | 720) => adminCall<LifecycleReportDto>('GET', `/api/admin/analytics/lifecycle?hours=${hours}`),
  status: () => adminCall<LifecycleStatusDto>('GET', '/api/admin/analytics/lifecycle/status'),
  backfill: (actor: string, reason: string, reset = false) => adminCall<{ cursor: number; maxRawMessageId: number; processed: number }>('POST', `/api/admin/analytics/lifecycle/backfill${reset ? '?reset=true' : ''}`, { actor, reason }),
  reconcile: (actor: string, reason: string, hours = 48) => adminCall<LifecycleReconciliationDto>('POST', `/api/admin/analytics/lifecycle/reconcile?hours=${hours}`, { actor, reason }),
}
