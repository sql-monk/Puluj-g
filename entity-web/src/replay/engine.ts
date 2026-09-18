import type { MapId, ReplayDto, TargetTypeDto } from '../api/types'
import { displayModeEnabled, type Filters } from '../store/useStore'

/** One reported position of a track (ms since epoch, degrees; deg = the reported course, if any). */
export interface ReplaySample {
  at: number
  lon: number
  lat: number
  deg: number | null
  approach: boolean
}

export interface ReplayTrack {
  id: MapId
  type: TargetTypeDto
  /** Oldest first. */
  samples: ReplaySample[]
}

/** Where a track is at an instant of the replay, reconstructed between its reports. */
export interface ReplayPosition {
  id: MapId
  type: TargetTypeDto
  lon: number
  lat: number
  /** Heading of the glyph: the direction of movement between reports, else the reported course; NaN = unknown (dot). */
  rotation: number
  opacity: number
  approach: boolean
}

/** A marker fades in over this much replay time after its first report. */
const FADE_IN_MS = 60_000

function bearing(a: ReplaySample, b: ReplaySample): number {
  const toRad = Math.PI / 180
  const φ1 = a.lat * toRad
  const φ2 = b.lat * toRad
  const Δλ = (b.lon - a.lon) * toRad
  const y = Math.sin(Δλ) * Math.cos(φ2)
  const x = Math.cos(φ1) * Math.sin(φ2) - Math.sin(φ1) * Math.cos(φ2) * Math.cos(Δλ)
  return ((Math.atan2(y, x) * 180) / Math.PI + 360) % 360
}

/**
 * The track's position at `t`: linear between the report before and the report after (the nose along that motion),
 * at the last report afterwards (the nose on its reported course) until the lifetime has passed. Null before the first
 * report and after the last one plus the lifetime. Opacity: a short fade-in after the first report, and the same fade
 * after the last one as the live map uses (down to 0.45 over the lifetime).
 */
export function positionAt(track: ReplayTrack, t: number, lifetimeMs: number): ReplayPosition | null {
  const s = track.samples
  if (s.length === 0) return null
  const first = s[0]
  const last = s[s.length - 1]
  if (t < first.at || t > last.at + lifetimeMs) return null
  let lon: number
  let lat: number
  let rotation: number
  let approach: boolean
  if (t >= last.at) {
    lon = last.lon
    lat = last.lat
    approach = last.approach
    const prev = s.length >= 2 ? s[s.length - 2] : null
    const moved = prev !== null && (prev.lon !== last.lon || prev.lat !== last.lat)
    rotation = last.deg ?? (moved ? bearing(prev!, last) : NaN)
  } else {
    let i = 0
    while (i < s.length - 2 && s[i + 1].at <= t) i++
    const a = s[i]
    const b = s[i + 1]
    const f = b.at <= a.at ? 1 : (t - a.at) / (b.at - a.at)
    lon = a.lon + (b.lon - a.lon) * f
    lat = a.lat + (b.lat - a.lat) * f
    const moved = a.lon !== b.lon || a.lat !== b.lat
    rotation = moved ? bearing(a, b) : (a.deg ?? b.deg ?? NaN)
    approach = a.approach
  }
  const fadeIn = 0.3 + 0.7 * Math.min(1, (t - first.at) / FADE_IN_MS)
  const fadeOut = t <= last.at ? 1 : Math.max(0.45, 1 - 0.55 * ((t - last.at) / Math.max(1, lifetimeMs)))
  return { id: track.id, type: track.type, lon, lat, rotation, opacity: Math.min(fadeIn, fadeOut), approach }
}

type Listener = (t: number) => void

/**
 * The replay clock and its data. Owns the window's tracks (one payload) and the instant being shown, advances it on
 * animation frames while playing and tells its listeners (the map views, the transport bar) about every change; the
 * store's `at` is only written from here at a slow rate, since every write there costs a snapshot request for alerts.
 */
class ReplayEngine {
  tracks: ReplayTrack[] = []
  from = 0
  to = 0
  /** The instant shown, ms since epoch; 0 until the first seek. */
  t = 0
  /** Minutes of history per real second. */
  speed = 5
  playing = false
  private listeners = new Set<Listener>()
  private endListeners = new Set<() => void>()
  private frame: number | null = null
  private lastFrame = 0

  load(dto: ReplayDto) {
    this.from = Date.parse(dto.from)
    this.to = Date.parse(dto.to)
    this.tracks = dto.tracks.map((tr) => ({
      id: tr.id,
      type: tr.type,
      samples: tr.samples
        .map((s) => ({ at: Date.parse(s.at), lon: s.point.coordinates[0], lat: s.point.coordinates[1], deg: s.directionDeg ?? null, approach: s.approach }))
        .sort((a, b) => a.at - b.at),
    }))
    this.notify()
  }

  clear() {
    this.pause()
    this.tracks = []
    this.t = 0
  }

  seek(t: number) {
    this.t = t
    this.notify()
  }

  play() {
    if (this.playing) return
    this.playing = true
    this.lastFrame = performance.now()
    this.frame = requestAnimationFrame(this.tick)
  }

  pause() {
    this.playing = false
    if (this.frame !== null) cancelAnimationFrame(this.frame)
    this.frame = null
  }

  subscribe(fn: Listener): () => void {
    this.listeners.add(fn)
    return () => {
      this.listeners.delete(fn)
    }
  }

  onEnd(fn: () => void): () => void {
    this.endListeners.add(fn)
    return () => {
      this.endListeners.delete(fn)
    }
  }

  /** Every track visible at `t` under the class and lifetime filters, with its reconstructed position. */
  positions(t: number, filters: Filters): ReplayPosition[] {
    const lifetime = filters.lifetimeMinutes * 60_000
    const out: ReplayPosition[] = []
    for (const track of this.tracks) {
      if (!displayModeEnabled(track.type.displayMode, filters)) continue
      const p = positionAt(track, t, lifetime)
      if (p) out.push(p)
    }
    return out
  }

  private tick = (now: number) => {
    if (!this.playing) return
    const dt = (now - this.lastFrame) / 1000
    this.lastFrame = now
    this.t += this.speed * 60_000 * dt
    if (this.t >= this.to) {
      this.t = this.to
      this.pause()
      this.notify()
      for (const fn of this.endListeners) fn()
      return
    }
    this.notify()
    this.frame = requestAnimationFrame(this.tick)
  }

  private notify() {
    for (const fn of this.listeners) fn(this.t)
  }
}

export const replay = new ReplayEngine()
