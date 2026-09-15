// Admin panel client (served by Puluj.Admin, same origin). The admin token (if the server has one) is kept in
// localStorage and sent as X-Admin-Token.
import type { SourceRatingReportDto } from './types'

export interface SettingDto {
  key: string
  value?: string
  isSecret: boolean
  hasValue: boolean
  source: 'db' | 'config' | 'default' | 'none'
}

export interface AdminSourceDto {
  id: number
  code: string
  name: string
  type: string
  enabled: boolean
  trustLevel: number
  priority: number
  url?: string
  channel?: string
  pollingIntervalSeconds?: number
  homeRegion?: string
  /** A token is stored on the source (the value itself never comes back). */
  hasToken: boolean
  rawMessageCount: number
  lastSuccessAt?: string
  lastMessageAt?: string
  consecutiveFailures: number
  lastError?: string
  status: 'disabled' | 'idle' | 'stale' | 'ok'
}

export interface SourcePatch {
  enabled?: boolean
  trustLevel?: number
  name?: string
  priority?: number
  pollingIntervalSeconds?: number
  channel?: string
  url?: string
  homeRegion?: string
  /** API token to store on the source; empty string removes it. */
  token?: string
}

export interface AdminStatusDto {
  alertsConfigured: boolean
  telegramConfigured: boolean
  llmConfigured: boolean
  telegramStatus?: string
  adminTokenSet: boolean
  workerAlive: boolean
  workerLastSeen?: string
}

export interface TestResultDto {
  ok: boolean
  message: string
}

// ---- Operations (status, statistics, logs) ----

export interface ServiceStatusDto {
  name: string
  status: 'ok' | 'warn' | 'down' | 'unknown'
  detail?: string
  lastSeen?: string
}
export interface OpsOverviewDto {
  generatedAt: string
  services: ServiceStatusDto[]
  db: { version: string; sizeBytes: number; connections: number; lastMigration?: string; migrationCount: number }
  /** Message processor instances with a fresh heartbeat. */
  processorCount: number
}
export interface CollectorStatusDto {
  sourceId: number
  code: string
  name: string
  type: string
  enabled: boolean
  lastPolledAt?: string
  lastSuccessAt?: string
  lastMessageAt?: string
  lastError?: string
  consecutiveFailures: number
  messages24h: number
  /** Messages received per hour for the last 24 hours, oldest first. */
  perHour: number[]
}
export interface ProcessingErrorDto {
  id: number
  occurredAt: string
  stage: string
  message: string
  sourceId?: number
  rawMessageId?: number
  exception?: string
}

// ---- Instances, containers, pipeline (docs/plan-admin-ops.md §2.1, §2.4) ----

export interface StageTimingDto {
  samples: number
  meanMs: number
  p50Ms: number
  p90Ms: number
  maxMs: number
}
export interface ClaimDto {
  rawMessageId: number
  since: string
}
export interface ProcessingStatusDto {
  concurrency: number
  processed: number
  skipped: number
  failed: number
  retried: number
  retriedTransient: number
  perMinute1: number
  perMinute5: number
  parse: StageTimingDto
  lock: StageTimingDto
  store: StageTimingDto
  total: StageTimingDto
  lastProcessedAt?: string
  lastRawMessageId?: number
  claims: ClaimDto[]
}
export interface LlmStatusDto {
  enabled: boolean
  model: string
  pausedUntil?: string
  pauseReason?: string
  calls: number
  failures: number
}
export interface ProcessingPauseDto {
  reason: string
  sourceStatus?: string
}
/** What an instance writes about itself every 10 s (`Runtime:Worker:{name}:Status`). */
export interface WorkerStatusDto {
  instance: string
  host: string
  roles: string[]
  version: string
  builtAt: string
  startedAt: string
  at: string
  pid: number
  workingSetBytes: number
  cpuPercent: number
  threads: number
  processing?: ProcessingStatusDto
  llm?: LlmStatusDto
  paused?: string
  pause?: ProcessingPauseDto
}
export interface WorkerInstanceDto {
  name: string
  kind: 'processor' | 'collector-telegram' | 'collector-alerts' | 'analytics' | 'worker' | 'migrate' | 'other'
  alive: boolean
  heartbeatAt?: string
  status?: WorkerStatusDto
  processed24h: number
  inProgress: number
  containerId?: string
  containerName?: string
  containerState?: string
  cpuPercent?: number
  memoryBytes?: number
}
export interface ContainerDto {
  id: string
  name: string
  service: string
  image: string
  state: 'running' | 'exited' | 'restarting' | 'paused' | 'created' | 'dead' | string
  status: string
  startedAt?: string
  cpuPercent?: number
  memoryBytes?: number
  memoryLimitBytes?: number
  /** False for admin / postgis / migrate: view only. */
  controllable: boolean
  replicaNumber?: number
}
export interface ContainersDto {
  available: boolean
  unavailable?: string
  project: string
  containers: ContainerDto[]
  processorReplicas: number
}
export interface ContainerActionResultDto {
  ok: boolean
  message: string
  output: string
}
export interface ScaleResultDto {
  result: ContainerActionResultDto
  containers?: ContainersDto
}
export interface PipelineTotalsDto {
  received: number
  processed: number
  skipped: number
  failed: number
  pending: number
  inProgress: number
  targets: number
  duplicates: number
  tracks: number
  errors: number
  p50Ms?: number
  p90Ms?: number
  meanMs?: number
}
export interface PipelineSourceDto {
  sourceId: number
  code: string
  name: string
  type: string
  enabled: boolean
  received: number
  processed: number
  skipped: number
  failed: number
  pending: number
  withTargets: number
  targets: number
  tracks: number
  medianLagSeconds?: number
  p50Ms?: number
  p90Ms?: number
  /** Received per bucket, aligned with PipelineReportDto.bucketStarts. */
  series: number[]
}
export interface PipelineBucketDto {
  at: string
  received: number
  processed: number
  targets: number
  errors: number
  transient: number
  p50Ms?: number
  p90Ms?: number
}
export interface PipelineInstanceDto {
  instance: string
  processed: number
  p50Ms?: number
  p90Ms?: number
  lastAt?: string
}
export interface PipelineReportDto {
  from: string
  to: string
  bucket: 'hour' | 'day'
  bucketStarts: string[]
  totals: PipelineTotalsDto
  sources: PipelineSourceDto[]
  timeline: PipelineBucketDto[]
  instances: PipelineInstanceDto[]
  queue: Record<string, number>
  errorsByStage: Record<string, number>
  recentErrors: ProcessingErrorDto[]
}
export interface DbReportDto {
  version: string
  sizeBytes: number
  tables: {
    name: string
    rows: number
    bytes: number
    inserts: number
    updates: number
    deletes: number
    deadRows: number
    lastVacuumAt?: string
    lastAnalyzeAt?: string
  }[]
  migrations: string[]
  connections: { role: string; connections: number }[]
  monitoring: {
    activeConnections: number
    idleConnections: number
    transactionsCommitted: number
    transactionsRolledBack: number
    cacheHitRatio: number
    deadRows: number
  }
}

export interface LlmUsageBucketDto {
  at: string
  calls: number
  inputTokens: number
  outputTokens: number
  estimatedCostUsd: number
}
export interface LlmRequestDto {
  id: number
  occurredAt: string
  rawMessageId?: number
  sourceId: number
  sourceCode: string
  worker: string
  model: string
  promptVersion: string
  outcome: string
  statusCode?: number
  durationMs: number
  inputTokens?: number
  cacheWriteTokens?: number
  cacheReadTokens?: number
  outputTokens?: number
  estimatedCostUsd?: number
  factsCount: number
  error?: string
}
export interface LlmUsageReportDto {
  from: string
  to: string
  calls: number
  withFacts: number
  empty: number
  refusals: number
  failures: number
  inputTokens: number
  cacheWriteTokens: number
  cacheReadTokens: number
  outputTokens: number
  estimatedCostUsd: number
  meanDurationMs?: number
  timeline: LlmUsageBucketDto[]
  recent: LlmRequestDto[]
}
export interface LlmRequestDetailDto {
  request: LlmRequestDto
  requestText: string
  systemPrompt: string
  responseText?: string
}
export interface DbQueryResultDto {
  columns: string[]
  rows: (string | null)[][]
  truncated: boolean
  elapsedMs: number
}
export interface LogFileDto {
  name: string
  service: string
  bytes: number
  modifiedAt: string
}
export interface LogTailDto {
  file: string
  lines: string[]
  truncated: boolean
  bytes: number
}

const TOKEN_KEY = 'puluj.adminToken'

export function getAdminToken(): string {
  try {
    return localStorage.getItem(TOKEN_KEY) ?? ''
  } catch {
    return ''
  }
}

export function setAdminToken(token: string) {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token)
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    /* ignore */
  }
}

export class AdminError extends Error {
  status: number
  /** The parsed JSON body of the failed response, when there was one (container actions answer with their result). */
  body?: unknown
  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.status = status
    this.body = body
  }
}

/** Shared by the other admin-side clients (api/analytics.ts). */
export async function adminCall<T>(method: string, path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
  return call<T>(method, path, body, extraHeaders)
}

async function call<T>(method: string, path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...extraHeaders }
  const token = getAdminToken()
  if (token) headers['X-Admin-Token'] = token
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (!res.ok) {
    let msg = `HTTP ${res.status}`
    let body: unknown
    try {
      const j = (await res.json()) as { error?: string; title?: string; message?: string; result?: { message?: string } }
      body = j
      msg = j.error ?? j.title ?? j.message ?? j.result?.message ?? msg
    } catch {
      /* no body */
    }
    throw new AdminError(res.status, msg, body)
  }
  // Some actions answer with an empty 200/204 body.
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const admin = {
  settings: () => call<SettingDto[]>('GET', '/api/admin/settings'),
  saveSettings: (values: Record<string, string | null>) => call<{ saved: number }>('PUT', '/api/admin/settings', { values }),
  status: () => call<AdminStatusDto>('GET', '/api/admin/status'),
  sources: () => call<AdminSourceDto[]>('GET', '/api/admin/sources'),
  updateSource: (id: number, patch: SourcePatch) => call<AdminSourceDto>('PUT', `/api/admin/sources/${id}`, patch),
  createSource: (req: { name: string; type: string; channel?: string; url?: string; trustLevel?: number; priority?: number; pollingIntervalSeconds?: number }) =>
    call<AdminSourceDto>('POST', '/api/admin/sources', req),
  deleteSource: (id: number) => call<void>('DELETE', `/api/admin/sources/${id}`),
  telegramCode: (code: string) => call<void>('POST', '/api/admin/telegram/code', { code }),
  testAlerts: (token?: string) => call<TestResultDto>('POST', '/api/admin/test/alerts', {}, token ? { 'X-Test-Token': token } : undefined),
  /** Earned source rating with per-day history, copy pairs and groups. */
  sourceRating: (days = 14) => call<SourceRatingReportDto>('GET', `/api/admin/sources/rating?days=${days}`),
  ops: {
    overview: () => call<OpsOverviewDto>('GET', '/api/admin/ops/overview'),
    collectors: () => call<CollectorStatusDto[]>('GET', '/api/admin/ops/collectors'),
    workers: () => call<WorkerInstanceDto[]>('GET', '/api/admin/ops/workers'),
    containers: () => call<ContainersDto>('GET', '/api/admin/ops/containers'),
    containerAction: (id: string, verb: 'restart' | 'stop' | 'start') => call<ContainerActionResultDto>('POST', `/api/admin/ops/containers/${encodeURIComponent(id)}/${verb}`, {}),
    scale: (replicas: number) => call<ScaleResultDto>('POST', '/api/admin/ops/processors/scale', { replicas }),
    pipeline: (hours: 24 | 168 | 720) => call<PipelineReportDto>('GET', `/api/admin/ops/pipeline?hours=${hours}`),
    llm: (hours: 24 | 168 | 720 = 168) => call<LlmUsageReportDto>('GET', `/api/admin/ops/llm?hours=${hours}`),
    llmRequest: (id: number) => call<LlmRequestDetailDto>('GET', `/api/admin/ops/llm/requests/${id}`),
    db: () => call<DbReportDto>('GET', '/api/admin/ops/db'),
    dbTableRows: (name: string, limit = 50) => call<DbQueryResultDto>('GET', `/api/admin/ops/db/tables/${encodeURIComponent(name)}/rows?limit=${limit}`),
    dbQuery: (sql: string) => call<DbQueryResultDto>('POST', '/api/admin/ops/db/query', { sql }),
    reprocess: () => call<{ queued: number; analyticsReset: boolean }>('POST', '/api/admin/ops/reprocess', { confirmation: 'REPROCESS_DERIVED_DATA' }),
    logFiles: () => call<LogFileDto[]>('GET', '/api/admin/logs/files'),
    logTail: (file: string, lines: number, filter: string, level: string) => {
      const q = new URLSearchParams({ file, lines: String(lines) })
      if (filter) q.set('filter', filter)
      if (level) q.set('level', level)
      return call<LogTailDto>('GET', `/api/admin/logs?${q}`)
    },
  },
}
