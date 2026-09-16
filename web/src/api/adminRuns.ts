import { adminCall } from './admin'

/** P14 (ADR-0005, §11): replay runs and generations — admin-only client over /api/admin/ops/runs. */

export type RunState = 'created' | 'running' | 'paused' | 'verified' | 'promoted' | 'rolled_back' | 'cancelled' | 'failed' | 'completed' | 'superseded' | string

export interface RunCheckpointDto {
  published: number
  total: number
  lastRawMessageId: number
  lastPublishedAt?: string | null
  done: boolean
  error?: string | null
}

export interface RunDto {
  runId: string
  lane: string
  kind: 'live' | 'history' | 'replay' | string
  state: RunState
  generationId?: string | null
  generationActive: boolean
  promotedAt?: string | null
  rolledBackAt?: string | null
  verifiedBy?: string | null
  supersedesRunId?: string | null
  replaysRunId?: string | null
  versions?: Record<string, unknown> | null
  scope?: (Record<string, unknown> & { from?: string; to?: string; source_ids?: number[] | null; verification?: Record<string, unknown>; promoted_from?: string | null; catchup_from?: string }) | null
  checkpoint?: RunCheckpointDto | null
  createdBy: string
  createdAt: string
  updatedAt: string
  finishedAt?: string | null
}

export type RunAction = 'start' | 'pause' | 'resume' | 'cancel' | 'catchup' | 'verify' | 'promote' | 'rollback'

export const adminRuns = {
  list: (limit = 50) => adminCall<RunDto[]>('GET', `/api/admin/ops/runs?limit=${limit}`),
  createReplay: (from: string, to: string, sourceIds: number[] | null, actor: string, reason: string) =>
    adminCall<{ ok: boolean; runId: string }>('POST', '/api/admin/ops/runs/replay', { from, to, sourceIds, actor, reason }),
  act: (runId: string, action: RunAction, actor: string, reason: string, opts?: { force?: boolean; watermark?: string }) =>
    adminCall<{ ok: boolean; report?: Record<string, unknown>; generation?: string; restored?: string | null; watermark?: string }>(
      'POST',
      `/api/admin/ops/runs/${runId}/${action}${opts?.force ? '?force=true' : ''}`,
      { actor, reason, watermark: opts?.watermark ?? null },
    ),
}
