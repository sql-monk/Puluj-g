import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api/client'
import { connectMapHub } from './api/signalr'
import type { AlertDto, MapId, TargetDto, TrackDto } from './api/types'
import FeedPanel from './components/FeedPanel'
import DataFilterControls from './components/DataFilterControls'
import FilterPanel from './components/FilterPanel'
import KyivPanel from './components/KyivPanel'
import ReplayBar from './components/ReplayBar'
import StatsPage from './stats/StatsPage'
import TopBar from './components/TopBar'
import TrackDetailsDrawer from './components/TrackDetailsDrawer'
import KyivMapView from './map/KyivMapView'
import MapView from './map/MapView'
import EntityCatalogue from './entities/EntityCatalogue'
import MessagesCatalogue from './messages/MessagesCatalogue'
import { themeIsDark, themeMapIsDark, useStore } from './store/useStore'
import { historyWindow, isMapRoute, parsePublicHash, publicHash, type PublicRoute, type PublicSection } from './public/routes'
import { parseDataQuery } from './public/query'

const TICK_MS = 15_000
/** Hub events are applied to the store in batches this often: one re-render per batch instead of one per event. */
const EVENT_BATCH_MS = 300

export default function App() {
  const theme = useStore((s) => s.theme)
  const mode = useStore((s) => s.mode)
  const at = useStore((s) => s.at)
  const selectedTrackId = useStore((s) => s.selectedTrackId)
  const selectedRegionId = useStore((s) => s.selectedRegionId)
  const selectedTrack = useStore((s) => (s.selectedTrackId ? s.tracks[s.selectedTrackId] : undefined))
  const setHome = useStore((s) => s.setHome)
  const legacyPanelOpen = useStore((s) => s.panelOpen)
  const panelOpenBySection = useStore((s) => s.panelOpenBySection)
  const setPanelOpenFor = useStore((s) => s.setPanelOpenFor)
  // The feed opens folded too: a "Повідомлення (N)" button in the top-right corner unfolds it.
  const [feedOpen, setFeedOpen] = useState(false)
  const [picking, setPicking] = useState(false)
  // The details panel (left) shows whichever track is selected on the map, as long as it is open.
  const [detailsOpen, setDetailsOpen] = useState(false)
  const panelButton = useRef<HTMLButtonElement>(null)
  const [rememberedRoutes, setRememberedRoutes] = useState<Partial<Record<PublicSection, PublicRoute>>>({})
  const [route, setRoute] = useState<PublicRoute>(() => parsePublicHash(window.location.hash).route)
  const regionsLoaded = useStore((s) => s.regions.length > 0)
  const dark = themeIsDark(theme)
  const mapDark = themeMapIsDark(theme)
  const mapRoute = isMapRoute(route)
  const stats = route.section === 'analytics'
  const replay = mapRoute && route.mapMode === 'history'
  const kyivPreset = mapRoute && route.preset === 'kyiv'
  const replayWindow = useMemo(() => (replay ? historyWindow(route.query) : null), [replay, route.query])
  const panelOpen = panelOpenBySection[route.section] ?? legacyPanelOpen
  const dataQuery = useMemo(() => parseDataQuery(route.query).value, [route.query])
  const analyticsFilterUnavailable = Boolean(dataQuery.eventKinds.length || dataQuery.entityKinds.length || dataQuery.eventCategories.length || dataQuery.categoryIds.length || dataQuery.classIds.length || dataQuery.familyIds.length || dataQuery.modelIds.length || dataQuery.sourceIds.length || dataQuery.regionId || dataQuery.q || dataQuery.status || dataQuery.confidence || dataQuery.location || dataQuery.hasResults !== undefined || dataQuery.sort || dataQuery.cursor)
  const activeMap = mapRoute

  useEffect(() => {
    document.documentElement.classList.toggle('dark', dark)
    document.documentElement.dataset.theme = theme
  }, [dark, theme])

  // Fade / ETA are map resources: do not keep a timer while another section is active.
  useEffect(() => {
    if (!activeMap) return
    const id = window.setInterval(() => useStore.getState().tick(), TICK_MS)
    return () => window.clearInterval(id)
  }, [activeMap])

  // Hash is the public navigation source of truth. Legacy links replace to a canonical route without a second Back entry.
  useEffect(() => {
    const onHash = () => {
      const parsed = parsePublicHash(window.location.hash)
      if (parsed.shouldReplace) window.history.replaceState(null, '', parsed.canonicalHash)
      setRememberedRoutes((current) => ({ ...current, [parsed.route.section]: parsed.route }))
      setRoute(parsed.route)
    }
    onHash()
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  // Kyiv is an opt-in preset. Leaving it deliberately preserves region and selected details.
  useEffect(() => {
    if (!kyivPreset) return
    const s = useStore.getState()
    const kyiv = s.regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ')
    if (kyiv) s.selectRegion(kyiv.id)
  }, [kyivPreset, regionsLoaded])
  // A shared URL region filter is also an explicit map selection when the map is visible. This covers first load and
  // Back/Forward without coupling the camera to data refreshes.
  useEffect(() => {
    if (!activeMap || dataQuery.regionId === undefined || dataQuery.regionId === selectedRegionId) return
    useStore.getState().selectRegion(dataQuery.regionId)
  }, [activeMap, dataQuery.regionId, selectedRegionId])
  // The Kyiv presentation is deliberately narrow: following a link or a URL to another region returns to the country
  // map instead of trying to force an out-of-city polygon into Kyiv's bounded camera.
  useEffect(() => {
    if (!kyivPreset || selectedRegionId === null) return
    const regions = useStore.getState().regions
    const kyiv = regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ')
    const selected = regions.find((r) => r.id === selectedRegionId)
    if (!kyiv || !selected || selected.id === kyiv.id || selected.parentId === kyiv.id) return
    window.location.hash = publicHash({ ...route, preset: undefined })
  }, [kyivPreset, route, selectedRegionId])
  // The selected sources remain a presentation setting too, while the full U03
  // filter is sent with every snapshot/replay/timeline request below.
  useEffect(() => {
    if (!activeMap) return
    const store = useStore.getState()
    store.setFilter('sources', dataQuery.sourceIds.length ? dataQuery.sourceIds : null)
  }, [activeMap, dataQuery.sourceIds])
  // History is a route, rather than a local switch. ReplayBar owns its detailed clock once mounted.
  useEffect(() => {
    if (!mapRoute) return
    const store = useStore.getState()
    if (route.mapMode === 'history' && replayWindow) store.setMode('history', replayWindow.at)
    if (route.mapMode === 'live' && store.mode !== 'live') store.setMode('live')
  }, [mapRoute, replayWindow, route.mapMode])
  useEffect(() => {
    if (!activeMap) useStore.getState().setError(null)
  }, [activeMap])
  const closePanel = useCallback(() => {
    setPanelOpenFor(route.section, false)
    window.setTimeout(() => panelButton.current?.focus(), 0)
  }, [route.section, setPanelOpenFor])
  useEffect(() => {
    if (!panelOpen) return
    const panel = document.querySelector<HTMLElement>('[data-section-panel="open"]')
    const focusable = () => Array.from(panel?.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])') ?? [])
    window.setTimeout(() => focusable()[0]?.focus(), 0)
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        closePanel()
      } else if (event.key === 'Tab') {
        const items = focusable()
        if (items.length === 0) return
        const index = items.indexOf(document.activeElement as HTMLElement)
        if (event.shiftKey && (index <= 0 || index === -1)) {
          event.preventDefault()
          items.at(-1)?.focus()
        } else if (!event.shiftKey && index === items.length - 1) {
          event.preventDefault()
          items[0]?.focus()
        }
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [closePanel, panelOpen])
  useEffect(() => {
    if (!panelOpen || !window.matchMedia('(max-width: 767px)').matches) return
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { document.body.style.overflow = previous }
  }, [panelOpen])
  const toggleReplay = () => {
    window.location.hash = publicHash({ ...route, mapMode: replay ? 'live' : 'history' })
  }
  const setHistoryWindow = useCallback((history: { from: Date; to: Date; at: Date }) => {
    const query = new URLSearchParams(route.query)
    query.set('from', history.from.toISOString())
    query.set('to', history.to.toISOString())
    query.set('at', history.at.toISOString())
    const next = publicHash({ ...route, mapMode: 'history', query })
    if (window.location.hash !== next) window.location.hash = next
  }, [route])

  // Region polygons, source filters and live windows are map-only resources.
  useEffect(() => {
    if (!activeMap) return
    api
      .mapConfig()
      .then((c) => useStore.getState().setMapConfig(c))
      .catch(() => undefined) // the defaults equal the server's
    api
      .regions()
      .then((r) => useStore.getState().setRegions(r))
      .catch((e: Error) => useStore.getState().setError(`Регіони: ${e.message}`))
    api
      .sources()
      .then((s) => useStore.getState().setSources(s))
      .catch((e: Error) => useStore.getState().setError(`Джерела: ${e.message}`))
  }, [activeMap])

  // Snapshot requests can overlap while the timeline slider moves; only the latest one may land in the store.
  const snapshotSeq = useRef(0)
  const snapshotAbort = useRef<AbortController | null>(null)
  useEffect(() => {
    // Invalidates a late response after navigating away from the map.
    if (!activeMap) {
      snapshotSeq.current += 1
      snapshotAbort.current?.abort()
    }
  }, [activeMap])
  const loadSnapshot = useCallback(async () => {
    const s = useStore.getState()
    const seq = ++snapshotSeq.current
    snapshotAbort.current?.abort()
    const controller = new AbortController()
    snapshotAbort.current = controller
    s.setLoading(true)
    try {
      const hasMapFilter = Object.values(dataQuery).some((value) => Array.isArray(value) ? value.length > 0 : value !== undefined)
      const [snap, feed] = await Promise.all([api.snapshot(s.mode === 'history' && s.at ? s.at : undefined, false, dataQuery, controller.signal), s.mode === 'live' && !hasMapFilter ? api.targets(s.mapConfig.feedHours) : Promise.resolve(null)])
      if (seq !== snapshotSeq.current) return
      if (feed) useStore.getState().setTargets(feed)
      // The store applies "active only" itself so toggling the filter needs no round-trip.
      useStore.getState().setSnapshot(snap.tracks, snap.alerts, snap.events)
      s.setError(null)
    } catch (e) {
      if ((e as Error).name === 'AbortError') return
      if (seq === snapshotSeq.current) s.setError(`Не вдалося завантажити стан: ${(e as Error).message}`)
    } finally {
      if (seq === snapshotSeq.current) s.setLoading(false)
    }
  }, [dataQuery])

  // Live: snapshot + realtime. History: snapshot at the selected instant, realtime ignored.
  useEffect(() => {
    if (!activeMap) return
    void loadSnapshot()
  }, [loadSnapshot, activeMap, mode, at, dataQuery])

  useEffect(() => {
    if (!activeMap) return
    const store = useStore.getState()
    // Events are buffered and flushed together: a burst (a busy night, a reprocess) then costs one store update per
    // batch, not one per event. The last state of a track or alert within a batch wins.
    const pending = { tracks: new Map<MapId, TrackDto>(), alerts: new Map<MapId, AlertDto>(), events: [] as TargetDto[], targets: [] as TargetDto[] }
    let timer: number | null = null
    const filtered = Object.values(dataQuery).some((value) => Array.isArray(value) ? value.length > 0 : value !== undefined)
    const flush = () => {
      timer = null
      const s = useStore.getState()
      // Hub deltas are intentionally incomplete for canonical filters. Reload the
      // server-filtered snapshot rather than accidentally re-introducing a row.
      if (filtered) {
        pending.tracks.clear(); pending.alerts.clear(); pending.events = []; pending.targets = []
        if (s.mode === 'live') void loadSnapshot()
        return
      }
      if (pending.tracks.size > 0) s.upsertTracks([...pending.tracks.values()])
      if (pending.alerts.size > 0) s.upsertAlerts([...pending.alerts.values()])
      if (pending.events.length > 0) s.upsertEvents(pending.events)
      if (pending.targets.length > 0) s.addTargets(pending.targets)
      pending.tracks.clear()
      pending.alerts.clear()
      pending.events = []
      pending.targets = []
    }
    const schedule = () => {
      if (timer === null) timer = window.setTimeout(flush, EVENT_BATCH_MS)
    }
    // The first "connected" arrives while the mount effect is already loading the snapshot: only a re-connect reloads.
    let connectedBefore = false
    const connection = connectMapHub({
      trackUpserted: (t) => {
        pending.tracks.set(t.id, t)
        schedule()
      },
      trackClosed: (t) => {
        pending.tracks.set(t.id, t)
        schedule()
      },
      alertChanged: (a) => {
        pending.alerts.set(a.id, a)
        schedule()
      },
      targetCreated: (o) => {
        pending.targets.push(o)
        if (o.eventType === 'ExplosionReport' || o.eventType === 'AirDefenseActivity' || o.eventType === 'TargetCancelled') pending.events.push(o)
        schedule()
      },
      connectionChanged: (state) => {
        store.setConnection(state)
        if (state === 'connected') {
          if (connectedBefore && useStore.getState().mode === 'live') void loadSnapshot()
          connectedBefore = true
        }
      },
    })
    return () => {
      if (timer !== null) window.clearTimeout(timer)
      void connection.stop()
    }
  }, [loadSnapshot, activeMap, dataQuery])

  const pickHome = picking
    ? (lon: number, lat: number) => {
        setHome({ lon, lat })
        setPicking(false)
      }
    : null

  return (
    <div className="relative h-full w-full overflow-hidden bg-slate-100 dark:bg-slate-950" data-feed={feedOpen ? 'open' : 'closed'}>
      {activeMap && (kyivPreset ? <KyivMapView dark={mapDark} theme={theme} onDetails={() => setDetailsOpen(true)} layoutKey={`${panelOpen}-${feedOpen}-${replay}-${detailsOpen}`} /> : <MapView dark={mapDark} theme={theme} onPickHome={pickHome} onDetails={() => setDetailsOpen(true)} layoutKey={`${panelOpen}-${feedOpen}-${replay}-${detailsOpen}`} />)}
      <TopBar route={route} rememberedRoutes={rememberedRoutes} panelOpen={panelOpen} onTogglePanel={() => panelOpen ? closePanel() : setPanelOpenFor(route.section, true)} panelButtonRef={panelButton} />
      {stats && <StatsPage filterUnavailable={analyticsFilterUnavailable} />}
      {route.section === 'entities' && <EntityCatalogue route={route} query={dataQuery} />}
      {route.section === 'messages' && <MessagesCatalogue route={route} query={dataQuery} />}
      {mapRoute && (kyivPreset ? (
        <KyivPanel route={route} open={panelOpen} onClose={closePanel} />
      ) : (
        <FilterPanel route={route} open={panelOpen} onClose={closePanel} picking={picking} onPickingChange={setPicking} onReplay={() => !replay && toggleReplay()} />
      ))}
      {!mapRoute && <SectionPanel route={route} open={panelOpen} onClose={closePanel} section={route.section as Exclude<PublicRoute['section'], 'map'>} />}
      {panelOpen && <button type="button" className="pointer-events-auto absolute inset-0 z-[9] bg-slate-950/35 md:hidden" onClick={closePanel} aria-label="Закрити панель" />}
      {mapRoute && !panelOpen && !detailsOpen && (
        <button
          className="pointer-events-auto absolute left-3 top-14 z-10 hidden rounded-lg bg-white/95 px-3 py-1.5 text-sm shadow md:block dark:bg-slate-900/95 dark:text-slate-100"
          onClick={() => setPanelOpenFor(route.section, true)}
          title="Показати панель фільтрів"
        >
          ☰ Фільтри
        </button>
      )}
      {replay && replayWindow && <ReplayBar key={`${replayWindow.from.toISOString()}-${replayWindow.to.toISOString()}`} initialWindow={replayWindow} query={dataQuery} onHistoryChange={setHistoryWindow} onClose={toggleReplay} />}
      {mapRoute && <FeedPanel open={feedOpen} onToggle={() => setFeedOpen((o) => !o)} />}
      {detailsOpen && mapRoute && <TrackDetailsDrawer onClose={() => setDetailsOpen(false)} />}
      {mapRoute && selectedTrackId && !selectedTrack && !detailsOpen && (
        <div className="pointer-events-auto absolute bottom-3 left-1/2 z-20 -translate-x-1/2 rounded bg-white/90 px-3 py-1 text-xs shadow dark:bg-slate-900/90 dark:text-slate-100">
          Трек більше не відображається.{' '}
          <button className="underline" onClick={() => setDetailsOpen(true)}>
            Деталі
          </button>
        </div>
      )}
    </div>
  )
}

function SectionPanel({ route, open, onClose, section }: { route: PublicRoute; open: boolean; onClose: () => void; section: Exclude<PublicRoute['section'], 'map'> }) {
  const label = section === 'analytics' ? 'Фільтри аналітики' : section === 'entities' ? 'Фільтри каталогу' : 'Фільтри повідомлень'
  return <aside data-section-panel={open ? 'open' : 'closed'} inert={!open} className={`pointer-events-auto absolute bottom-0 z-20 max-h-[60vh] w-full overflow-y-auto rounded-t-xl bg-white/95 p-3 shadow-lg backdrop-blur transition-transform md:bottom-auto md:left-3 md:top-14 md:max-h-[calc(100vh-5rem)] md:w-72 md:rounded-xl dark:bg-slate-900/95 dark:text-slate-100 ${open ? 'translate-y-0' : 'pointer-events-none translate-y-full md:-translate-x-[120%] md:translate-y-0'}`} aria-hidden={!open}><div className="mb-3 flex items-center justify-between"><strong>{label}</strong><button onClick={onClose} aria-label="Згорнути панель" className="rounded px-2 py-1 hover:bg-slate-200 dark:hover:bg-slate-700">‹</button></div><DataFilterControls route={route} /></aside>
}
