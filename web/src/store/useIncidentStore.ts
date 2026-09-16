import { create } from 'zustand'
import { incidentsApi, type IncidentDto } from '../api/incidents'
import { buildCatalog, EMPTY_CATALOG, type Catalog, type CatalogKindDto } from '../catalog/catalog'
import { useStore } from './useStore'

/**
 * Incidents on the client (P11, ADR-0011). Realtime pushes carry whole DTOs with a revision: a revision the store already
 * has (or an older one, out of order) is ignored; a retracted, suppressed or merged incident is dropped. Pushes are
 * at-most-once — the durable path is the window reload: on (re)connect, on the server's `Resync`, and every
 * DELTA_RELOAD_MS from the last checkpoint, so nothing depends on receiving every packet.
 */

export const DELTA_RELOAD_MS = 60_000
/** History reloads (scrubbing, replay ticks every second) are trailing-throttled to this: the recorded-mode query is the heaviest one (review B3). */
export const HISTORY_RELOAD_MS = 3_000
/** A server `Resync` reaches every client of a replica at once: the reload is spread over this window (review N6). */
export const RESYNC_JITTER_MS = 5_000
/** The delta reload starts this much before the checkpoint: an incident whose last report landed just before it is not missed. */
export const CHECKPOINT_SLACK_MS = 5 * 60_000
export const PUSH_BATCH_MS = 300

export interface IncidentState {
  byId: Record<number, IncidentDto>
  catalog: Catalog
  /** Server time of the last window/delta load (`IncidentPageDto.to`); the next delta starts CHECKPOINT_SLACK_MS before it. */
  checkpoint: string | null
  /** Kinds the viewer switched off (separate from catalog visibility). */
  hiddenKinds: Set<string>
  selectedId: number | null
  loading: boolean
  windowHours: number
  apply: (dto: IncidentDto) => boolean
  applyMany: (dtos: IncidentDto[]) => number
  setCatalog: (kinds: CatalogKindDto[]) => void
  toggleKind: (code: string) => void
  select: (id: number | null) => void
  /** Full reload of the window (live: the incident window up to now; history: what the system knew at `at`). A stale response never overwrites a newer one. */
  reloadWindow: () => Promise<void>
  /** `reloadWindow` after a random delay (the server's Resync): jitter, not a herd. */
  resync: (maxDelayMs?: number) => void
  /** Live only: everything reported since the checkpoint (minus the slack). */
  reloadDelta: () => Promise<void>
  /** Drops what fell out of the window (by the kind's map lifetime) — the layer builder filters the same way; this keeps memory bounded. */
  prune: (now: Date) => void
}

/** Whether `next` supersedes `current`: a first sight, or a strictly newer revision. */
export function supersedes(current: IncidentDto | undefined, next: IncidentDto): boolean {
  return current === undefined || next.revision > current.revision
}

/** Retracted (including merged-away) and suppressed incidents leave the client; the DTO says so itself. */
export function isGone(dto: IncidentDto): boolean {
  return dto.state === 'retracted' || dto.suppressed || dto.mergedIntoIncidentId !== undefined
}

function applyInto(byId: Record<number, IncidentDto>, dto: IncidentDto): boolean {
  const current = byId[dto.id]
  if (!supersedes(current, dto)) return false
  if (isGone(dto)) {
    if (current === undefined) return false
    delete byId[dto.id]
    return true
  }
  byId[dto.id] = dto
  return true
}

let reloadSeq = 0

export const useIncidentStore = create<IncidentState>((set, get) => ({
  byId: {},
  catalog: EMPTY_CATALOG,
  checkpoint: null,
  hiddenKinds: new Set(),
  selectedId: null,
  loading: false,
  windowHours: 24,

  apply: (dto) => {
    const byId = { ...get().byId }
    if (!applyInto(byId, dto)) return false
    set({ byId, selectedId: byId[get().selectedId ?? -1] ? get().selectedId : null })
    return true
  },
  applyMany: (dtos) => {
    const byId = { ...get().byId }
    let changed = 0
    for (const dto of dtos) if (applyInto(byId, dto)) changed++
    if (changed > 0) set({ byId, selectedId: byId[get().selectedId ?? -1] ? get().selectedId : null })
    return changed
  },
  resync: (maxDelayMs = RESYNC_JITTER_MS) => {
    setTimeout(() => void get().reloadWindow(), Math.random() * maxDelayMs)
  },
  setCatalog: (kinds) => set({ catalog: buildCatalog(kinds) }),
  toggleKind: (code) => {
    const hidden = new Set(get().hiddenKinds)
    if (hidden.has(code)) hidden.delete(code)
    else hidden.add(code)
    set({ hiddenKinds: hidden })
  },
  select: (selectedId) => set({ selectedId }),

  reloadWindow: async () => {
    const main = useStore.getState()
    const seq = ++reloadSeq
    set({ loading: true })
    try {
      const hours = get().windowHours
      const result =
        main.mode === 'history' && main.at
          ? await incidentsApi.window({ mode: 'recorded', asOf: main.at, from: new Date(main.at.getTime() - hours * 3600_000), to: main.at })
          : await incidentsApi.window({ from: new Date(Date.now() - hours * 3600_000) })
      if (seq !== reloadSeq) return // a newer reload is in flight: its answer, not this one, is the truth
      // A reload is the truth for the window: what is not in it any more (retracted, suppressed, out of the window) goes —
      // except a revision a push delivered while the fetch was in flight, which is newer than the page.
      const current = get().byId
      const byId: Record<number, IncidentDto> = {}
      for (const dto of result.items) applyInto(byId, dto)
      for (const dto of Object.values(current)) if (byId[dto.id] && dto.revision > byId[dto.id].revision) byId[dto.id] = dto
      set({ byId, checkpoint: result.to, selectedId: byId[get().selectedId ?? -1] ? get().selectedId : null })
    } finally {
      if (seq === reloadSeq) set({ loading: false })
    }
  },
  reloadDelta: async () => {
    const { checkpoint } = get()
    if (useStore.getState().mode === 'history' || !checkpoint) return get().reloadWindow()
    const result = await incidentsApi.window({ from: new Date(new Date(checkpoint).getTime() - CHECKPOINT_SLACK_MS) })
    get().applyMany(result.items)
    set({ checkpoint: result.to })
  },
  prune: (now) => {
    const { byId, catalog } = get()
    const kept: Record<number, IncidentDto> = {}
    let dropped = 0
    for (const dto of Object.values(byId)) {
      const age = (now.getTime() - new Date(dto.lastReportedAt).getTime()) / 60_000
      if (age <= catalog.lifetimeMinutesOf(dto.kind)) kept[dto.id] = dto
      else dropped++
    }
    if (dropped > 0) set({ byId: kept })
  },
}))

/** Coalesces pushes into one store update per PUSH_BATCH_MS: a burst of a thousand revisions is one render. */
export function createPushBatcher(store: Pick<IncidentState, 'applyMany'> = useIncidentStore.getState(), batchMs = PUSH_BATCH_MS) {
  let pending: IncidentDto[] = []
  let timer: ReturnType<typeof setTimeout> | null = null
  const flush = () => {
    timer = null
    if (pending.length === 0) return
    const batch = pending
    pending = []
    store.applyMany(batch)
  }
  return {
    push(dto: IncidentDto) {
      pending.push(dto)
      if (timer === null) timer = setTimeout(flush, batchMs)
    },
    flush,
  }
}

let bound = false

/** Trailing throttle: the first call runs at once, calls inside the window collapse into one run at its end. */
export function trailingThrottle(fn: () => void, ms: number): () => void {
  let last = 0
  let timer: ReturnType<typeof setTimeout> | null = null
  return () => {
    const now = Date.now()
    if (timer !== null) return
    if (now - last >= ms) {
      last = now
      fn()
      return
    }
    timer = setTimeout(() => {
      timer = null
      last = Date.now()
      fn()
    }, ms - (now - last))
  }
}

/**
 * Wires the incident store to the main store's connection state and to the clock: a (re)connect reloads the window,
 * every DELTA_RELOAD_MS a delta is fetched, and a mode/time change reloads (history changes throttled: replay ticks and
 * scrubbing must not run the recorded-mode query once a second). Idempotent; the store never edits `useStore`.
 */
export function bindIncidentRealtime(deps: { delayMs?: number; historyMs?: number } = {}): () => void {
  if (bound) return () => {}
  bound = true
  const reloadHistory = trailingThrottle(() => void useIncidentStore.getState().reloadWindow(), deps.historyMs ?? HISTORY_RELOAD_MS)
  const unsubscribe = useStore.subscribe((s, prev) => {
    if (s.connection === 'connected' && prev.connection !== 'connected') void useIncidentStore.getState().reloadWindow()
    else if (s.mode !== prev.mode) void useIncidentStore.getState().reloadWindow()
    else if (s.mode === 'history' && s.at?.getTime() !== prev.at?.getTime()) reloadHistory()
  })
  const timer = setInterval(() => {
    if (useStore.getState().connection === 'connected' && useStore.getState().mode === 'live') void useIncidentStore.getState().reloadDelta()
  }, deps.delayMs ?? DELTA_RELOAD_MS)
  return () => {
    unsubscribe()
    clearInterval(timer)
    bound = false
  }
}
