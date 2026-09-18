import type { Confidence, DirectionDto, LocationKind, TrackDto } from '../api/types'
import type { EtaResult } from '../eta/computeEta'

export function timeAgo(iso: string, now: Date): string {
  const min = Math.round((now.getTime() - new Date(iso).getTime()) / 60000)
  if (min < 1) return 'щойно'
  if (min < 60) return `~${min} хв тому`
  const h = Math.floor(min / 60)
  return `~${h} год ${min % 60} хв тому`
}

export function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
}

export function dateTime(iso: string): string {
  return new Date(iso).toLocaleString('uk-UA', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
}

export const confidenceLabel: Record<Confidence, string> = {
  Unknown: 'невідомо',
  Low: 'низька',
  Medium: 'середня',
  High: 'висока',
  Confirmed: 'підтверджено',
}

export const locationKindLabel: Record<LocationKind, string> = {
  Unknown: 'локація невідома',
  DirectionOnly: 'на підході, позиція приблизна',
  Region: 'область',
  District: 'район',
  City: 'населений пункт',
  Area: 'акваторія/зона',
  Point: 'точка',
}

export function compass(deg: number): string {
  const names = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW']
  return names[Math.round(((deg % 360) + 360) % 360 / 45) % 8]
}

export function directionText(d?: DirectionDto): string {
  if (!d) return 'напрямок невідомий'
  const src = d.kind === 'Compass' ? 'за повідомленням' : 'за пунктом призначення'
  return `${compass(d.degrees)} (${Math.round(d.degrees)}°, ${src})`
}

export function etaText(eta: EtaResult | null): string {
  if (!eta) return 'вкажіть свою точку'
  switch (eta.kind) {
    case 'range':
      return `~${eta.minMinutes}–${eta.maxMinutes} хв`
    case 'imminent':
      return `< 5 хв (до ~${eta.maxMinutes})`
    case 'notTowards':
      return 'не у вашому напрямку'
    case 'unknown':
      switch (eta.reason) {
        case 'disabled':
          return 'ETA не розраховується'
        case 'stale':
          return 'дані застарілі'
        case 'passed':
          return 'ймовірно вже минув'
        default:
          return 'ETA невідомий'
      }
  }
}

export function etaConfidence(eta: EtaResult | null): string {
  return eta && (eta.kind === 'range' || eta.kind === 'imminent') ? confidenceLabel[eta.confidence] : ''
}

/** "Ромни 21:40 → на Конотоп 21:52 → зараз: Кролевець": the earlier reported positions, then the current one. */
export function fixChain(track: TrackDto): string {
  const fixes = track.fixes
  if (fixes.length < 2) return ''
  const earlier = fixes.slice(0, -1).map((f) => `${f.approach ? 'на підході до ' : ''}${f.placeName ?? '?'} ${clock(f.at)} (${Math.round(f.probability * 100)}%)`)
  const now = track.lastLocation?.placeName ?? fixes[fixes.length - 1].placeName ?? '?'
  return `${earlier.join(' → ')} → зараз: ${now}`
}
