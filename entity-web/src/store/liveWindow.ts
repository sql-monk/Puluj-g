import type { TargetDto, TrackDto } from '../api/types'

/**
 * The live map's time windows, as the server hands them out (GET /api/map/config). The defaults equal the server's
 * defaults so the map behaves the same before the config arrives or when the request fails.
 */
export interface MapConfig {
  /** Choices of "how long a marker stays after its last message" on the panel, minutes. */
  lifetimeOptionsMinutes: number[]
  /** The longest choice: nothing older than this is kept in the store (the server sends nothing older either). */
  maxLifetimeMinutes: number
  /** How far back the feed goes in live mode, hours. */
  feedHours: number
}

export const defaultMapConfig: MapConfig = {
  lifetimeOptionsMinutes: [5, 10, 15, 20, 30, 45, 60, 120],
  maxLifetimeMinutes: 120,
  feedHours: 6,
}

/** The option closest to a remembered value that the current option list no longer offers. */
export function nearestLifetime(minutes: number, options: number[]): number {
  if (options.length === 0) return minutes
  return options.reduce((best, m) => (Math.abs(m - minutes) < Math.abs(best - minutes) ? m : best), options[0])
}

/** Could the track still be on the map for any lifetime setting? (Its status does not matter: the viewer's filter does that.) */
export function isTrackLive(t: TrackDto, now: Date, maxLifetimeMinutes: number): boolean {
  return now.getTime() - new Date(t.lastSeenAt).getTime() <= maxLifetimeMinutes * 60_000
}

/** Does the report fall inside the feed window? */
export function isTargetFresh(o: TargetDto, now: Date, feedHours: number): boolean {
  return now.getTime() - new Date(o.observedAt).getTime() <= feedHours * 3_600_000
}

/** The tracks still inside the window; the same object when nothing had to go (no re-render for nothing). */
export function pruneTracks(tracks: Record<string, TrackDto>, now: Date, cfg: MapConfig): Record<string, TrackDto> {
  let changed = false
  const kept: Record<string, TrackDto> = {}
  for (const t of Object.values(tracks)) {
    if (isTrackLive(t, now, cfg.maxLifetimeMinutes)) kept[t.id] = t
    else changed = true
  }
  return changed ? kept : tracks
}

/** Incoming tracks laid over the current ones; those outside the window are dropped rather than stored. */
export function mergeTracks(tracks: Record<string, TrackDto>, incoming: TrackDto[], now: Date, cfg: MapConfig): Record<string, TrackDto> {
  const live = incoming.filter((t) => isTrackLive(t, now, cfg.maxLifetimeMinutes))
  if (live.length === 0) return tracks
  const merged = { ...tracks }
  for (const t of live) merged[t.id] = t
  return merged
}

/** Newest first, no duplicates, nothing outside the feed window, at most `cap` items. */
export function mergeTargets(list: TargetDto[], incoming: TargetDto[], now: Date, cfg: MapConfig, cap = 500): TargetDto[] {
  const seen = new Set(list.map((o) => o.id))
  const fresh: TargetDto[] = []
  for (const o of incoming) {
    if (seen.has(o.id) || !isTargetFresh(o, now, cfg.feedHours)) continue
    seen.add(o.id)
    fresh.push(o)
  }
  if (fresh.length === 0) return list
  fresh.sort((a, b) => new Date(b.observedAt).getTime() - new Date(a.observedAt).getTime() || String(b.id).localeCompare(String(a.id)))
  return [...fresh, ...list].slice(0, cap)
}
