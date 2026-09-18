import bearing from '@turf/bearing'
import booleanPointInPolygon from '@turf/boolean-point-in-polygon'
import distance from '@turf/distance'
import { lineString, point } from '@turf/helpers'
import pointToLineDistance from '@turf/point-to-line-distance'
import type { Geometry, MultiPolygon, Polygon } from 'geojson'
import type { Confidence, RegionDto, TrackDto } from '../api/types'

// Specification of this algorithm: docs/README.md#карта-і-eta. Kept as a pure function so it is unit-tested and can be
// mirrored server-side later (pre-computation to city centroids) without divergence.

export interface Home {
  lon: number
  lat: number
}

export type EtaResult =
  | { kind: 'range'; minMinutes: number; maxMinutes: number; confidence: Confidence; distanceKm: number }
  | { kind: 'imminent'; maxMinutes: number; confidence: Confidence; distanceKm: number }
  | { kind: 'notTowards'; distanceKm: number; bearingDiffDeg: number }
  | { kind: 'unknown'; reason: 'disabled' | 'noLocation' | 'noSpeed' | 'stale' | 'passed'; distanceKm?: number }

const TOWARDS_TOLERANCE_DEG = 45
const ROUND_TO_MINUTES = 5
const IMMINENT_MINUTES = 5

const order: Confidence[] = ['Unknown', 'Low', 'Medium', 'High', 'Confirmed']

function lower(c: Confidence, steps = 1): Confidence {
  const i = Math.max(1, order.indexOf(c) - steps)
  return order[i]
}

export function angleDiffDeg(a: number, b: number): number {
  const d = Math.abs(a - b) % 360
  return d > 180 ? 360 - d : d
}

/**
 * Distance (km) from a point to the edge of a region: 0 when the point is inside. With a real polygon the
 * "how close can it be" bound no longer collapses to zero for anyone within the region's covering radius.
 */
export function distanceToRegionKm(home: Home, geometry: Geometry): number | null {
  if (geometry.type !== 'Polygon' && geometry.type !== 'MultiPolygon') return null
  const me = point([home.lon, home.lat])
  if (booleanPointInPolygon(me, geometry as Polygon | MultiPolygon)) return 0
  const rings = geometry.type === 'Polygon' ? [geometry.coordinates[0]] : geometry.coordinates.map((p) => p[0])
  return Math.min(...rings.map((ring) => pointToLineDistance(me, lineString(ring), { units: 'kilometers' })))
}

/** @param regionsById Region polygons, when the caller has them: sharpens the bounds for region-level reports. */
export function computeEta(track: TrackDto, home: Home, now: Date, regionsById?: Map<number, RegionDto>): EtaResult {
  const profile = track.type.speedProfile
  if (!profile.etaEnabled) {
    return { kind: 'unknown', reason: 'disabled' }
  }
  const loc = track.lastLocation
  if (!loc?.point || loc.kind === 'Unknown' || loc.kind === 'DirectionOnly') {
    return { kind: 'unknown', reason: 'noLocation' }
  }
  if (!profile.minKmh || !profile.maxKmh) {
    return { kind: 'unknown', reason: 'noSpeed' }
  }

  const from = point(loc.point.coordinates)
  const to = point([home.lon, home.lat])
  const distanceKm = distance(from, to, { units: 'kilometers' })
  const accuracy = loc.accuracyKm ?? 0
  // Region-level report with a known polygon: the nearest the object can be is the region's edge (or here, if inside).
  const region = (loc.kind === 'Region' || loc.kind === 'Area') && loc.placeId ? regionsById?.get(loc.placeId) : undefined
  const edgeKm = region ? distanceToRegionKm(home, region.geometry) : null
  const elapsedMin = (now.getTime() - new Date(track.lastSeenAt).getTime()) / 60000

  if (elapsedMin > track.type.fadeMinutes * 2) {
    return { kind: 'unknown', reason: 'stale', distanceKm }
  }

  // Direction check only when the user is clearly outside the reported area.
  const insideArea = edgeKm !== null ? edgeKm === 0 : distanceKm <= accuracy
  if (track.direction && !insideArea) {
    const toHome = (bearing(from, to) + 360) % 360
    const diff = angleDiffDeg(toHome, track.direction.degrees)
    if (diff > TOWARDS_TOLERANCE_DEG) {
      return { kind: 'notTowards', distanceKm, bearingDiffDeg: diff }
    }
  }

  const dMin = edgeKm ?? Math.max(0, distanceKm - accuracy)
  const dMax = distanceKm + accuracy
  const etaMinRaw = (dMin / profile.maxKmh) * 60 - elapsedMin
  const etaMaxRaw = (dMax / profile.minKmh) * 60 - elapsedMin
  if (etaMaxRaw <= 0) {
    return { kind: 'unknown', reason: 'passed', distanceKm }
  }

  let confidence: Confidence = track.direction ? track.direction.confidence : 'Low'
  if (confidence === 'Unknown') {
    confidence = 'Low'
  }
  if (loc.kind === 'Region' || loc.kind === 'Area') {
    confidence = lower(confidence)
  }
  if (elapsedMin > track.type.fadeMinutes) {
    confidence = lower(confidence)
  }

  const maxMinutes = Math.max(ROUND_TO_MINUTES, Math.ceil(etaMaxRaw / ROUND_TO_MINUTES) * ROUND_TO_MINUTES)
  if (etaMinRaw <= IMMINENT_MINUTES) {
    return { kind: 'imminent', maxMinutes, confidence, distanceKm }
  }
  const minMinutes = Math.floor(etaMinRaw / ROUND_TO_MINUTES) * ROUND_TO_MINUTES
  return { kind: 'range', minMinutes, maxMinutes: Math.max(maxMinutes, minMinutes + ROUND_TO_MINUTES), confidence, distanceKm }
}

/** Marker opacity for a track given its age (spec §12): 1.0 fresh, ~0.2 at fadeMinutes, gone at 2x. */
export function fadeOpacity(lastSeenAt: string, fadeMinutes: number, now: Date): number {
  const ageMin = (now.getTime() - new Date(lastSeenAt).getTime()) / 60000
  if (ageMin <= 0) {
    return 1
  }
  if (ageMin >= fadeMinutes * 2) {
    return 0
  }
  if (ageMin <= fadeMinutes) {
    return 1 - 0.8 * (ageMin / fadeMinutes)
  }
  return 0.2 * (1 - (ageMin - fadeMinutes) / fadeMinutes)
}
