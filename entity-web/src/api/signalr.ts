import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { AlertDto, TargetDto, TrackDto } from './types'

export interface MapEvents {
  trackUpserted: (t: TrackDto) => void
  trackClosed: (t: TrackDto) => void
  alertChanged: (a: AlertDto) => void
  targetCreated: (o: TargetDto) => void
  connectionChanged: (state: 'connected' | 'reconnecting' | 'disconnected') => void
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
