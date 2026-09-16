import { create } from 'zustand'
import type { Geometry } from 'geojson'
import type { PublicSection } from '../public/routes'
import { api } from '../api/client'
import type { AlertDto, PredecessorsDto, DisplayMode, MapId, TargetDto, RegionDto, SourceDto, TrackDto } from '../api/types'
import type { Home } from '../eta/computeEta'
import { getPalette, type MapPalette } from '../map/palette'
import { defaultMapConfig, mergeTargets, mergeTracks, nearestLifetime, pruneTracks, type MapConfig } from './liveWindow'

export type Mode = 'live' | 'history'
export type Theme = 'light' | 'sepia' | 'graphite' | 'dark' | 'midnight' | 'olive'

/**
 * Colour themes: the palette lives in index.css (data-theme on <html>). `dark` = light text on dark panels
 * (the `dark` class), `mapDark` = the dark basemap. Two light, one in between (dark panels over a light map), two dark.
 */
export const THEMES: { id: Theme; label: string; dark: boolean; mapDark: boolean }[] = [
  { id: 'light', label: 'Світла', dark: false, mapDark: false },
  { id: 'sepia', label: 'Сепія (світла, тепла)', dark: false, mapDark: false },
  { id: 'graphite', label: 'Графіт (середня)', dark: true, mapDark: false },
  { id: 'dark', label: 'Темна', dark: true, mapDark: true },
  { id: 'midnight', label: 'Опівнічна (темна, синя)', dark: true, mapDark: true },
  { id: 'olive', label: 'Олива (темна, зелена)', dark: true, mapDark: true },
]

export function themeIsDark(theme: Theme): boolean {
  return THEMES.find((t) => t.id === theme)?.dark ?? false
}

export function themeMapIsDark(theme: Theme): boolean {
  return THEMES.find((t) => t.id === theme)?.mapDark ?? false
}
export type Connection = 'connected' | 'reconnecting' | 'disconnected'

/** A leg of the selected target's family the viewer clicked: the two reports it joins and its probability. */
export interface SelectedLink {
  trackId: MapId
  fromTargetId: MapId
  toTargetId: MapId
  probability: number
  pathProbability: number
  kind: string
}

export interface Filters {
  uav: boolean
  cruise: boolean
  ballistic: boolean
  aircraft: boolean
  alerts: boolean
  /** Localized, non-track reports such as explosions and air-defence activity. */
  events: boolean
  activeOnly: boolean
  /** Forecast cone and dashed centreline ahead of every marker (crumbs and predecessors belong to the selected target only). */
  forecast: boolean
  /** Highlight tracks near the viewer's point or heading towards it (needs a home point). */
  highlightTargets: boolean
  /** Source ids to show; null = every source. Tracks need at least one selected source, feed items their own. */
  sources: number[] | null
  /** How long after its last message a target stays on the map, minutes. */
  lifetimeMinutes: number
}

interface State {
  tracks: Record<string, TrackDto>
  alerts: Record<string, AlertDto>
  events: Record<string, TargetDto>
  regions: RegionDto[]
  sources: SourceDto[]
  /** The live windows the server works with (lifetime choices, feed depth); defaults until /api/map/config answers. */
  mapConfig: MapConfig
  mode: Mode
  at: Date | null
  now: Date
  connection: Connection
  filters: Filters
  home: Home | null
  theme: Theme
  /** Left panel (filters) shown; folded by default, persisted once the viewer toggles it. */
  panelOpen: boolean
  /** U03 migration: each public section remembers its own drawer state; `panelOpen` remains a legacy fallback. */
  panelOpenBySection: Partial<Record<PublicSection, boolean>>
  selectedTrackId: MapId | null
  /** The clicked leg between two reports of the selected target's family (its window is open). */
  selectedLink: SelectedLink | null
  /** Feed of recent targets, newest first (live mode only). */
  targets: TargetDto[]
  /** Oblast clicked on the map: highlighted border + feed filter. */
  selectedRegionId: number | null
  /** Monotonic explicit selection token: a repeated click is also a request to centre the region. */
  regionCameraRequest: number
  /** Probable predecessors (two generations) of the selected track's newest report; null while loading or none. */
  predecessors: PredecessorsDto | null
  /** Polygons fetched one by one for alerts on places the regions payload does not carry (hromadas, cities). */
  placeGeometries: Record<number, Geometry>
  loading: boolean
  error: string | null

  setSnapshot: (tracks: TrackDto[], alerts: AlertDto[], events: TargetDto[]) => void
  upsertTrack: (t: TrackDto) => void
  /** A batch of hub updates in one store change (the hub is flushed every few hundred ms). Tracks outside the window are dropped. */
  upsertTracks: (list: TrackDto[]) => void
  upsertAlert: (a: AlertDto) => void
  upsertAlerts: (list: AlertDto[]) => void
  upsertEvents: (list: TargetDto[]) => void
  setRegions: (r: RegionDto[]) => void
  setMapConfig: (c: MapConfig) => void
  setSources: (s: SourceDto[]) => void
  setMode: (mode: Mode, at?: Date | null) => void
  tick: () => void
  setConnection: (c: Connection) => void
  setFilter: <K extends keyof Filters>(key: K, value: Filters[K]) => void
  setHome: (h: Home | null) => void
  setTheme: (t: Theme) => void
  setPanelOpen: (open: boolean) => void
  setPanelOpenFor: (section: PublicSection, open: boolean) => void
  select: (id: MapId | null) => void
  selectLink: (link: SelectedLink | null) => void
  setTargets: (list: TargetDto[]) => void
  addTarget: (o: TargetDto) => void
  /** A batch of new reports for the feed; those outside the feed window are dropped. */
  addTargets: (list: TargetDto[]) => void
  selectRegion: (id: number | null) => void
  loadPredecessors: (trackId: MapId | null) => void
  /** Cached/deduplicated geometry request; callers can safely await it for an explicit camera request. */
  ensurePlaceGeometry: (placeId: number) => Promise<Geometry | undefined>
  setLoading: (v: boolean) => void
  setError: (e: string | null) => void
}

const HOME_KEY = 'puluj.home'
const THEME_KEY = 'puluj.theme'
const FILTERS_KEY = 'puluj.filters'
const PANEL_KEY = 'puluj.panel'
const PANELS_KEY = 'puluj.panels.v2'

function load<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key)
    if (!raw) return fallback
    const parsed = JSON.parse(raw) as T
    // Objects are merged over the defaults so new keys get their default; primitives are taken as-is.
    return typeof fallback === 'object' && fallback !== null && typeof parsed === 'object' && parsed !== null ? { ...fallback, ...parsed } : parsed
  } catch {
    return fallback
  }
}

function save(key: string, value: unknown) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, JSON.stringify(value))
  } catch {
    /* private mode etc. */
  }
}

const pendingGeometries = new Map<number, Promise<Geometry | undefined>>()

export const defaultFilters: Filters = {
  uav: true,
  cruise: true,
  ballistic: true,
  aircraft: false,
  alerts: true,
  events: true,
  activeOnly: true,
  forecast: true,
  highlightTargets: true,
  sources: null,
  lifetimeMinutes: 15,
}

export const useStore = create<State>((set, get) => ({
  tracks: {},
  alerts: {},
  events: {},
  regions: [],
  sources: [],
  mapConfig: defaultMapConfig,
  mode: 'live',
  at: null,
  now: new Date(),
  connection: 'disconnected',
  filters: load(FILTERS_KEY, defaultFilters),
  home: load<Home | null>(HOME_KEY, null),
  // Unknown or retired ids (the old "system") fall back to the plain dark theme.
  theme: ((t) => (THEMES.some((x) => x.id === t) ? t : 'dark'))(load<Theme>(THEME_KEY, 'dark')),
  // The map opens uncluttered: the panel stays folded until the viewer opens it (then their choice is remembered).
  panelOpen: load<boolean>(PANEL_KEY, false),
  panelOpenBySection: load<Partial<Record<PublicSection, boolean>>>(PANELS_KEY, {}),
  selectedTrackId: null,
  selectedLink: null,
  targets: [],
  selectedRegionId: null,
  regionCameraRequest: 0,
  predecessors: null,
  placeGeometries: {},
  loading: false,
  error: null,

  setSnapshot: (tracks, alerts, events) =>
    set({
      tracks: Object.fromEntries(tracks.map((t) => [t.id, t])),
      alerts: Object.fromEntries(alerts.map((a) => [a.id, a])),
      events: Object.fromEntries(events.map((o) => [o.id, o])),
    }),
  upsertTrack: (t) => get().upsertTracks([t]),
  upsertTracks: (list) =>
    set((s) => {
      if (s.mode !== 'live' || list.length === 0) return {}
      const tracks = mergeTracks(s.tracks, list, new Date(), s.mapConfig)
      return tracks === s.tracks ? {} : { tracks }
    }),
  upsertAlert: (a) => get().upsertAlerts([a]),
  upsertAlerts: (list) =>
    set((s) => {
      if (s.mode !== 'live' || list.length === 0) return {}
      const alerts = { ...s.alerts }
      for (const a of list) {
        if (a.endedAt) delete alerts[a.id]
        else alerts[a.id] = a
      }
      return { alerts }
    }),
  upsertEvents: (list) =>
    set((s) => {
      if (s.mode !== 'live' || list.length === 0) return {}
      const cutoff = Date.now() - s.mapConfig.maxLifetimeMinutes * 60_000
      const events = { ...s.events }
      for (const o of list) if (new Date(o.observedAt).getTime() >= cutoff && isMapEvent(o)) events[o.id] = o
      return { events }
    }),
  setRegions: (regions) => set({ regions }),
  setMapConfig: (mapConfig) =>
    set((s) => {
      // A remembered lifetime the server no longer offers snaps to the closest option.
      const lifetime = nearestLifetime(s.filters.lifetimeMinutes, mapConfig.lifetimeOptionsMinutes)
      if (lifetime === s.filters.lifetimeMinutes) return { mapConfig }
      const filters = { ...s.filters, lifetimeMinutes: lifetime }
      save(FILTERS_KEY, filters)
      return { mapConfig, filters }
    }),
  setSources: (sources) => set({ sources }),
  // Scrubbing inside history keeps the selected track; crossing live<->history drops it (ids may not exist there).
  setMode: (mode, at = null) => set((s) => ({ mode, at, selectedTrackId: s.mode === mode ? s.selectedTrackId : null })),
  // Besides the clock, the tick drops what fell out of the live window, so a long session does not keep every
  // track it ever received (in history the store is frozen at `at`).
  tick: () =>
    set((s) => {
      const now = new Date()
      if (s.mode !== 'live') return { now }
      const tracks = pruneTracks(s.tracks, now, s.mapConfig)
      const cutoff = now.getTime() - s.mapConfig.maxLifetimeMinutes * 60_000
      const events = Object.fromEntries(Object.entries(s.events).filter(([, o]) => new Date(o.observedAt).getTime() >= cutoff))
      return { now, ...(tracks === s.tracks ? {} : { tracks }), ...(Object.keys(events).length === Object.keys(s.events).length ? {} : { events }) }
    }),
  setConnection: (connection) => set({ connection }),
  setFilter: (key, value) =>
    set((s) => {
      const filters = { ...s.filters, [key]: value }
      save(FILTERS_KEY, filters)
      return { filters }
    }),
  setHome: (home) => {
    save(HOME_KEY, home)
    set({ home })
  },
  setTheme: (theme) => {
    save(THEME_KEY, theme)
    set({ theme })
  },
  setPanelOpen: (panelOpen) => {
    save(PANEL_KEY, panelOpen)
    set({ panelOpen })
  },
  setPanelOpenFor: (section, open) =>
    set((state) => {
      const panelOpenBySection = { ...state.panelOpenBySection, [section]: open }
      save(PANELS_KEY, panelOpenBySection)
      return { panelOpenBySection }
    }),
  select: (selectedTrackId) => set((s) => ({ selectedTrackId, selectedLink: null, predecessors: s.selectedTrackId === selectedTrackId ? s.predecessors : null })),
  selectLink: (selectedLink) => set({ selectedLink }),
  loadPredecessors: (trackId) => {
    if (trackId === null) {
      set({ predecessors: null })
      return
    }
    api
      .predecessors(trackId, 2)
      .then((p) => {
        if (get().selectedTrackId === trackId) set({ predecessors: p })
      })
      .catch(() => {
        if (get().selectedTrackId === trackId) set({ predecessors: null })
      })
  },
  ensurePlaceGeometry: (placeId) => {
    const cached = get().placeGeometries[placeId]
    if (cached) return Promise.resolve(cached)
    const pending = pendingGeometries.get(placeId)
    if (pending) return pending
    const request = api
      .placeGeometry(placeId)
      .then((g) => {
        set((s) => ({ placeGeometries: { ...s.placeGeometries, [placeId]: g } }))
        return g
      })
      .catch(() => undefined)
      .finally(() => pendingGeometries.delete(placeId))
    pendingGeometries.set(placeId, request)
    return request
  },
  setTargets: (targets) => set({ targets }),
  addTarget: (o) => get().addTargets([o]),
  addTargets: (list) =>
    set((s) => {
      if (s.mode !== 'live' || list.length === 0) return {}
      const targets = mergeTargets(s.targets, list, new Date(), s.mapConfig)
      return targets === s.targets ? {} : { targets }
    }),
  selectRegion: (selectedRegionId) => set((s) => ({ selectedRegionId, regionCameraRequest: selectedRegionId === null ? s.regionCameraRequest : s.regionCameraRequest + 1 })),
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
}))

// Exposed for dev-time scripting (Playwright); the store module is also loaded by node-side tests, where there is no window.
if (import.meta.env.DEV && typeof window !== 'undefined') Object.assign(window, { __store: useStore })

export function displayModeEnabled(mode: DisplayMode, f: Filters): boolean {
  switch (mode) {
    case 'uav':
      return f.uav
    case 'cruise':
      return f.cruise
    case 'ballistic':
      return f.ballistic
    case 'aircraft':
      return f.aircraft
  }
}

/** Only these fact types are point-in-time map events; alerts and moving targets have dedicated map models. */
export function isMapEvent(o: TargetDto): boolean {
  return o.eventType === 'ExplosionReport' || o.eventType === 'AirDefenseActivity' || o.eventType === 'TargetCancelled'
}

/** Does the source filter let this source through? */
export function sourceEnabled(sourceId: number, f: Filters): boolean {
  return f.sources === null || f.sources.includes(sourceId)
}

/** The "clock" of the map: wall time in live mode, the selected instant in history mode. */
export function effectiveNow(s: Pick<State, 'mode' | 'at' | 'now'>): Date {
  return s.mode === 'history' && s.at ? s.at : s.now
}

/** The map palette of the current theme (markers, vectors, alert fills, land). */
export function usePalette(): MapPalette {
  return getPalette(useStore((s) => s.theme))
}
