import type { AlertDto, AlertLevel, RegionDto } from '../api/types'

/**
 * One reading of "which alerts concern a place, and at what level" for the region window, the feed chip, the Kyiv
 * panel and the alert fill. Alerts sit on places of any level (oblast, raion, hromada, city, Kyiv district); a place is
 * under alert when the alert is on it, on one of its ancestors (a city-wide alert covers every district) or — for the
 * window's "also inside" line and the 24 h statistics — on one of its descendants.
 */

/** Yellow = target level (drones), Red = missiles, Unknown = an alert without a published level; null = none. */
export type EffectiveLevel = AlertLevel | null

const RANK: Record<AlertLevel, number> = { Unknown: 1, Yellow: 2, Red: 3 }

/**
 * The level that stands when several alerts apply: red beats yellow, and a published level beats an alert with none
 * (a text channel's plain "тривога" does not contradict the administration's yellow). Unknown alone reads as a full alert.
 */
export function effectiveLevel(alerts: readonly AlertDto[]): EffectiveLevel {
  let best: EffectiveLevel = null
  for (const a of alerts) {
    const level = a.level ?? 'Unknown'
    if (best === null || RANK[level] > RANK[best]) best = level
  }
  return best
}

/** Colour of a level: yellow for the target level, red for a full alert (published red or no level at all). */
export function levelTone(level: EffectiveLevel): 'red' | 'yellow' | null {
  return level === null ? null : level === 'Yellow' ? 'yellow' : 'red'
}

/**
 * The place's parents, nearest first. Regions the map carries (oblasts, raions, Kyiv and its districts) chain through
 * `parentId`; a place the payload lacks (a hromada, reached by clicking its alert fill) borrows the chain the server
 * sent with any alert on it.
 */
export function ancestorsOf(placeId: number, regionsById: Map<number, RegionDto>, alerts: readonly AlertDto[] = []): number[] {
  const chain: number[] = []
  let region = regionsById.get(placeId)
  if (region) {
    for (let i = 0; i < 8 && region?.parentId !== undefined && !chain.includes(region.parentId); i++) {
      chain.push(region.parentId)
      region = regionsById.get(region.parentId)
    }
    return chain
  }
  const own = alerts.find((a) => a.placeId === placeId)
  return own ? [...own.ancestorIds] : []
}

/** Is the alert on the place or on one of the place's ancestors (so the place is under it)? */
export function coversPlace(a: AlertDto, placeId: number, ancestors: readonly number[]): boolean {
  return a.placeId === placeId || ancestors.includes(a.placeId)
}

/** Is the alert on a place inside this one? */
export function insidePlace(a: AlertDto, placeId: number): boolean {
  return a.ancestorIds.includes(placeId)
}

/**
 * Alerts concerning the place, ordered: the place's own first, then those covering it (nearest ancestor first), then
 * those inside it. `alerts[0]` is therefore the one to name in the window's headline.
 */
export function alertsFor(alerts: readonly AlertDto[], placeId: number, ancestors: readonly number[]): AlertDto[] {
  const rank = (a: AlertDto) => (a.placeId === placeId ? 0 : ancestors.includes(a.placeId) ? 1 + ancestors.indexOf(a.placeId) : 1000 + a.ancestorIds.length)
  return alerts
    .filter((a) => coversPlace(a, placeId, ancestors) || insidePlace(a, placeId))
    .map((a, i) => ({ a, i, r: rank(a) }))
    .sort((x, y) => x.r - y.r || x.i - y.i)
    .map((x) => x.a)
}
