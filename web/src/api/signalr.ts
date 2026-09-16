import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { IncidentDto } from './incidents'
import { bindIncidentRealtime, createPushBatcher, useIncidentStore } from '../store/useIncidentStore'
import type { AlertDto, TargetDto, TrackDto } from './types'

export interface MapEvents {
  trackUpserted: (t: TrackDto) => void
  trackClosed: (t: TrackDto) => void
  alertChanged: (a: AlertDto) => void
  targetCreated: (o: TargetDto) => void
  connectionChanged: (state: 'connected' | 'reconnecting' | 'disconnected') => void
  /** P11 (optional — the incident store handles them by default): a new incident / a later revision / "reload your window". */
  incidentUpserted?: (i: IncidentDto) => void
  incidentRevised?: (i: IncidentDto) => void
  resync?: (at: string) => void
}

/** Realtime channel (spec §19). Reconnects automatically; the caller re-fetches a snapshot on reconnect. */
export function connectMapHub(events: MapEvents): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/map')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('TrackUpserted', events.trackUpserted)
  connection.on('TrackClosed', events.trackClosed)
  connection.on('AlertChanged', events.alertChanged)
  connection.on('TargetCreated', events.targetCreated)
  // P11 (ADR-0011): incident pushes go to the incident store in batches; a stale revision is ignored there. The server's
  // Resync (its NOTIFY listener reconnected — pushes may have been lost) and our own reconnect reload the window.
  const batch = createPushBatcher()
  connection.on('IncidentUpserted', (i: IncidentDto) => (events.incidentUpserted ?? batch.push)(i))
  connection.on('IncidentRevised', (i: IncidentDto) => (events.incidentRevised ?? batch.push)(i))
  connection.on('Resync', (at: string) => (events.resync ?? (() => useIncidentStore.getState().resync()))(at))
  bindIncidentRealtime()
  connection.onreconnecting(() => events.connectionChanged('reconnecting'))
  connection.onreconnected(() => events.connectionChanged('connected'))
  connection.onclose(() => events.connectionChanged('disconnected'))

  connection
    .start()
    .then(() => events.connectionChanged('connected'))
    .catch(() => events.connectionChanged('disconnected'))
  return connection
}

export function isConnected(c: HubConnection | null): boolean {
  return c?.state === HubConnectionState.Connected
}
