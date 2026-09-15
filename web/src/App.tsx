import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api/client'
import { connectMapHub } from './api/signalr'
import type { AlertDto, TargetDto, TrackDto } from './api/types'
import FeedPanel from './components/FeedPanel'
import FilterPanel from './components/FilterPanel'
import KyivPanel from './components/KyivPanel'
import ReplayBar from './components/ReplayBar'
import StatsPage from './stats/StatsPage'
import TopBar, { type Page } from './components/TopBar'
import TrackDetailsDrawer from './components/TrackDetailsDrawer'
import KyivMapView from './map/KyivMapView'
import MapView from './map/MapView'
import { themeIsDark, themeMapIsDark, useStore } from './store/useStore'

const TICK_MS = 15_000
/** Hub events are applied to the store in batches this often: one re-render per batch instead of one per event. */
const EVENT_BATCH_MS = 300

export default function App() {
  const theme = useStore((s) => s.theme)
  const mode = useStore((s) => s.mode)
  const at = useStore((s) => s.at)
  const selectedTrackId = useStore((s) => s.selectedTrackId)
  const selectedTrack = useStore((s) => (s.selectedTrackId ? s.tracks[s.selectedTrackId] : undefined))
  const setHome = useStore((s) => s.setHome)
  const panelOpen = useStore((s) => s.panelOpen)
  const setPanelOpen = useStore((s) => s.setPanelOpen)
  // The feed opens folded too: a "Повідомлення (N)" button in the top-right corner unfolds it.
  const [feedOpen, setFeedOpen] = useState(false)
  const [replay, setReplay] = useState(false)
  const [picking, setPicking] = useState(false)
  // The details panel (left) shows whichever track is selected on the map, as long as it is open.
  const [detailsOpen, setDetailsOpen] = useState(false)
  const [page, setPage] = useState<Page>(() => pageFromHash(window.location.hash))
  const regionsLoaded = useStore((s) => s.regions.length > 0)
  const dark = themeIsDark(theme)
  const mapDark = themeMapIsDark(theme)
  const stats = page === 'stats'

  useEffect(() => {
    document.documentElement.classList.toggle('dark', dark)
    document.documentElement.dataset.theme = theme
  }, [dark, theme])

  // Fade / ETA depend on wall time: re-render every 15 s.
  useEffect(() => {
    const id = window.setInterval(() => useStore.getState().tick(), TICK_MS)
    return () => window.clearInterval(id)
  }, [])

  // Hash routes: #/kyiv (the Kyiv page), #/stats… (the statistics page, its period in the query), anything else = the country map.
  // Back/forward and reloads keep working.
  useEffect(() => {
    const onHash = () => {
      setPage(pageFromHash(window.location.hash))
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  // Entering the Kyiv page scopes the feed to the city; leaving it clears the scope.
  useEffect(() => {
    const s = useStore.getState()
    const kyiv = s.regions.find((r) => r.level === 'City' && r.countryCode === 'UA' && r.name === 'Київ')
    s.selectRegion(page === 'kyiv' ? (kyiv?.id ?? null) : null)
    s.select(null)
  }, [page, regionsLoaded])
  // Replay is a mode over whichever map page is open; closing it returns to live (which reloads the live feed).
  const toggleReplay = () => {
    setReplay((r) => {
      if (r) useStore.getState().setMode('live')
      return !r
    })
  }
  const goPage = (p: Page) => {
    window.location.hash = p === 'kyiv' ? '#/kyiv' : p === 'stats' ? '#/stats' : ''
  }

  // Region polygons (alerts, region-level markers), the source list (per-source filter) and the live windows are loaded once.
  useEffect(() => {
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
  }, [])

  // Snapshot requests can overlap while the timeline slider moves; only the latest one may land in the store.
  const snapshotSeq = useRef(0)
  const loadSnapshot = useCallback(async () => {
    const s = useStore.getState()
    const seq = ++snapshotSeq.current
    s.setLoading(true)
    try {
      const [snap, feed] = await Promise.all([api.snapshot(s.mode === 'history' && s.at ? s.at : undefined, false), s.mode === 'live' ? api.targets(s.mapConfig.feedHours) : Promise.resolve(null)])
      if (seq !== snapshotSeq.current) return
      if (feed) useStore.getState().setTargets(feed)
      // The store applies "active only" itself so toggling the filter needs no round-trip.
      useStore.getState().setSnapshot(snap.tracks, snap.alerts, snap.events)
      s.setError(null)
    } catch (e) {
      if (seq === snapshotSeq.current) s.setError(`Не вдалося завантажити стан: ${(e as Error).message}`)
    } finally {
      if (seq === snapshotSeq.current) s.setLoading(false)
    }
  }, [])

  // Live: snapshot + realtime. History: snapshot at the selected instant, realtime ignored.
  useEffect(() => {
    void loadSnapshot()
  }, [loadSnapshot, mode, at])

  useEffect(() => {
    const store = useStore.getState()
    // Events are buffered and flushed together: a burst (a busy night, a reprocess) then costs one store update per
    // batch, not one per event. The last state of a track or alert within a batch wins.
    const pending = { tracks: new Map<number, TrackDto>(), alerts: new Map<number, AlertDto>(), events: [] as TargetDto[], targets: [] as TargetDto[] }
    let timer: number | null = null
    const flush = () => {
      timer = null
      const s = useStore.getState()
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
  }, [loadSnapshot])

  const pickHome = picking
    ? (lon: number, lat: number) => {
        setHome({ lon, lat })
        setPicking(false)
      }
    : null

  return (
    <div className="relative h-full w-full overflow-hidden bg-slate-100 dark:bg-slate-950" data-feed={feedOpen ? 'open' : 'closed'}>
      {page === 'kyiv' ? <KyivMapView dark={mapDark} theme={theme} onDetails={() => setDetailsOpen(true)} /> : <MapView dark={mapDark} theme={theme} onPickHome={pickHome} onDetails={() => setDetailsOpen(true)} />}
      <TopBar page={page} onPage={goPage} menuOpen={panelOpen} onToggleMenu={() => setPanelOpen(!panelOpen)} onReplay={toggleReplay} replay={replay} />
      {/* The statistics page covers the map (which stays mounted, state and SignalR intact); the map's panels are not drawn under it. */}
      {stats && <StatsPage />}
      {stats ? null : page === 'kyiv' ? (
        <KyivPanel open={panelOpen} onClose={() => setPanelOpen(false)} />
      ) : (
        <FilterPanel open={panelOpen} onClose={() => setPanelOpen(false)} picking={picking} onPickingChange={setPicking} onReplay={() => !replay && toggleReplay()} />
      )}
      {!stats && !panelOpen && !detailsOpen && (
        <button
          className="pointer-events-auto absolute left-3 top-14 z-10 hidden rounded-lg bg-white/95 px-3 py-1.5 text-sm shadow md:block dark:bg-slate-900/95 dark:text-slate-100"
          onClick={() => setPanelOpen(true)}
          title="Показати панель фільтрів"
        >
          ☰ Фільтри
        </button>
      )}
      {replay && !stats && <ReplayBar onClose={toggleReplay} />}
      {!stats && <FeedPanel open={feedOpen} onToggle={() => setFeedOpen((o) => !o)} />}
      {detailsOpen && !stats && <TrackDetailsDrawer onClose={() => setDetailsOpen(false)} />}
      {!stats && selectedTrackId && !selectedTrack && !detailsOpen && (
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

function pageFromHash(hash: string): Page {
  if (hash === '#/kyiv') return 'kyiv'
  if (hash.startsWith('#/stats')) return 'stats'
  return 'ukraine'
}
