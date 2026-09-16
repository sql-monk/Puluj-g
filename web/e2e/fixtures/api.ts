import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Page, Route } from '@playwright/test'

/**
 * Deterministic API for the UI E2E (plan P12 D1): every request the map makes is answered from these fixtures, so the
 * scenarios prove the UI contract without a database. Times are relative to `NOW` so lifetimes/windows behave the same
 * on every run. The basemap style is replaced by an empty style (no tiles) with real glyphs served from a stored PBF.
 */

const here = dirname(fileURLToPath(import.meta.url))
// Real time: the app measures lifetimes against its own clock (the fixture ages are minutes before the test started).
export const NOW = new Date()
const minutesAgo = (m: number) => new Date(NOW.getTime() - m * 60_000).toISOString()

// Places: Kharkiv oblast (id 8) with the Izium raion (500) and Kharkiv city (3126, a City marker); Kyiv (25, city-region) with Pechersk district (2500).
const box = (x0: number, y0: number, x1: number, y1: number) => ({ type: 'Polygon', coordinates: [[[x0, y0], [x1, y0], [x1, y1], [x0, y1], [x0, y0]]] })
export const regions = [
  { id: 1, name: 'Україна', level: 'Country', countryCode: 'UA', geometry: box(22, 44, 40.5, 52.5) },
  { id: 8, name: 'Харківська область', level: 'Region', countryCode: 'UA', geometry: box(35.2, 48.7, 38.2, 50.5) },
  { id: 500, name: 'Ізюмський район', level: 'District', countryCode: 'UA', parentId: 8, geometry: box(36.9, 48.9, 37.9, 49.5) },
  { id: 25, name: 'Київ', level: 'City', countryCode: 'UA', geometry: box(30.3, 50.3, 30.8, 50.55) },
  { id: 2500, name: 'Печерський район', level: 'District', countryCode: 'UA', parentId: 25, geometry: box(30.52, 50.4, 30.6, 50.45) },
]

export const eventKinds = [
  { id: 1, code: 'target.observed', nameUk: 'Спостереження цілі', category: 'target', requiresLocationForMap: true, renderMode: 'marker', createsIncident: false, mapVisible: true, sortOrder: 10, legacyEventType: 'TargetObserved', policyVersion: 2 },
  { id: 5, code: 'impact.explosion.reported', nameUk: 'Повідомлення про вибух', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#fb8c00', mapIcon: 'explosion', mapLifetime: '02:00:00', createsIncident: true, mapVisible: true, sortOrder: 40, legacyEventType: 'ExplosionReport', policyVersion: 2 },
  { id: 6, code: 'impact.confirmed', nameUk: 'Підтверджене влучання', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#c62828', mapIcon: 'impact', mapLifetime: '06:00:00', createsIncident: true, mapVisible: true, sortOrder: 41, policyVersion: 2 },
  { id: 7, code: 'fire.reported', nameUk: 'Пожежа', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#ef6c00', mapIcon: 'fire', mapLifetime: '06:00:00', createsIncident: true, mapVisible: true, sortOrder: 50, policyVersion: 2 },
  { id: 8, code: 'infrastructure.outage', nameUk: 'Відключення інфраструктури', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#546e7a', mapIcon: 'outage', mapLifetime: '12:00:00', createsIncident: true, mapVisible: false, sortOrder: 52, policyVersion: 2 },
  { id: 9, code: 'air_defence.activity', nameUk: 'Робота ППО', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#1e88e5', mapIcon: 'interception', mapLifetime: '01:00:00', createsIncident: true, mapVisible: true, sortOrder: 30, legacyEventType: 'AirDefenseActivity', policyVersion: 2 },
]

const source = { id: 1, code: 'tg_test', name: 'Тестовий канал', type: 'Telegram', trustLevel: 0.7, url: 'https://t.me/tg_test' }

export interface FixtureIncident {
  id: number
  kind: string
  state?: string
  revision?: number
  location?: { kind: string; placeId?: number; placeName?: string; regionId?: number; regionName?: string; point?: [number, number]; accuracyKm?: number; precision: string }
  minutesAgo?: number
  sourceCount?: number
}

export function incident(f: FixtureIncident) {
  const kind = eventKinds.find((k) => k.code === f.kind)!
  const age = f.minutesAgo ?? 10
  return {
    id: f.id,
    kind: f.kind,
    kindName: kind.nameUk,
    category: 'incident',
    state: f.state ?? 'reported',
    suppressed: false,
    eventAt: minutesAgo(age + 5),
    firstReportedAt: minutesAgo(age + 5),
    lastReportedAt: minutesAgo(age),
    location: f.location ? { ...f.location, point: f.location.point ? { type: 'Point', coordinates: f.location.point } : undefined } : undefined,
    confidence: 'medium',
    sourceCount: f.sourceCount ?? 1,
    revision: f.revision ?? 1,
    provenance: { canonicalObservationId: `0199-0000-${f.id}`, observationCount: f.sourceCount ?? 1, sourceIds: [1], policyVersion: 'incident-1/p2', generationId: 'live' },
  }
}

/** The five precision cases of E01 plus a Kyiv district incident (E04) and a legacy explosion target (E04/E05). */
export const incidents = [
  incident({ id: 1, kind: 'impact.explosion.reported', location: { kind: 'point', placeId: 3126, placeName: 'Харків', regionId: 8, regionName: 'Харківська область', point: [36.25, 49.98], accuracyKm: 0.5, precision: 'point' } }),
  incident({ id: 2, kind: 'impact.explosion.reported', revision: 3, sourceCount: 2, location: { kind: 'city', placeId: 3126, placeName: 'Харків', regionId: 8, regionName: 'Харківська область', point: [36.23, 49.99], accuracyKm: 15, precision: 'city' } }),
  incident({ id: 3, kind: 'fire.reported', location: { kind: 'district', placeId: 501, placeName: 'Куп’янський район', regionId: 8, regionName: 'Харківська область', point: [37.6, 49.7], accuracyKm: 30, precision: 'district' } }),
  incident({ id: 4, kind: 'impact.confirmed', state: 'confirmed', revision: 2, sourceCount: 2, location: { kind: 'region', placeId: 8, placeName: 'Харківська область', point: [36.7, 49.6], accuracyKm: 127.5, precision: 'region' } }),
  incident({ id: 5, kind: 'fire.reported', location: undefined }),
  incident({ id: 6, kind: 'infrastructure.outage', location: { kind: 'city', placeId: 3126, placeName: 'Харків', regionId: 8, point: [36.2, 50.0], accuracyKm: 15, precision: 'city' } }), // catalog-hidden kind
  incident({ id: 7, kind: 'impact.explosion.reported', location: { kind: 'district', placeId: 2500, placeName: 'Печерський район', regionId: 25, regionName: 'Київ', point: [30.56, 50.425], accuracyKm: 3, precision: 'district' } }),
]

export function details(id: number) {
  const dto = incidents.find((i) => i.id === id)!
  return {
    incident: dto,
    observations: [
      { observationId: dto.provenance.canonicalObservationId, targetId: 100 + id, sourceId: 1, sourceCode: 'tg_test', relation: 'canonical', score: 1, effectiveAt: dto.eventAt, linkedAt: dto.firstReportedAt, segmentText: `Повідомлення #${id}`, rawMessage: { id: 900 + id, sourceMessageId: `m${id}`, publishedAt: dto.eventAt, receivedAt: dto.eventAt, text: `Текст повідомлення про подію #${id}`, url: `https://t.me/tg_test/${900 + id}` } },
    ],
    revisions: [
      { revision: 1, change: 'created', effectiveAt: dto.eventAt, recordedAt: dto.firstReportedAt, actor: 'system' },
      ...(dto.revision > 1 ? [{ revision: 2, change: 'updated', effectiveAt: dto.lastReportedAt, recordedAt: dto.lastReportedAt, actor: 'operator', reason: 'підтверджено оператором' }] : []),
    ],
  }
}

/** A legacy explosion target (still in snapshot.events) at the same place as incident 2: hidden while the incident layer is on. */
export const legacyExplosion = {
  id: 1002,
  observedAt: minutesAgo(9),
  eventType: 'ExplosionReport',
  modelConfidence: 'Unknown',
  classificationConfidence: 'Unknown',
  confidence: 'Medium',
  location: { kind: 'City', placeId: 3126, placeName: 'Харків', regionId: 8, regionName: 'Харківська область', point: { type: 'Point', coordinates: [36.23, 49.99] }, accuracyKm: 15 },
  objectCountIsApproximate: false,
  identificationMethod: 'Rule',
  source,
  rawMessage: { id: 1902, sourceMessageId: 'm1002', publishedAt: minutesAgo(9), receivedAt: minutesAgo(9), text: 'Вибухи у Харкові.', url: 'https://t.me/tg_test/1902' },
  links: [],
  eventKindCode: 'impact.explosion.reported',
}

/** A legacy cancellation marker (kind without an incident): it stays on the events layer and its popup must carry the source permalink. */
export const legacyCancel = {
  ...legacyExplosion,
  id: 1003,
  eventType: 'TargetCancelled',
  location: { kind: 'Region', placeId: 8, placeName: 'Харківська область', point: { type: 'Point', coordinates: [35.5, 49.0] }, accuracyKm: 127.5 },
  rawMessage: { id: 1903, sourceMessageId: 'm1003', publishedAt: minutesAgo(8), receivedAt: minutesAgo(8), text: 'Загроза для Харківщини минула.', url: 'https://t.me/tg_test/1903' },
  eventKindCode: 'target.cancelled',
}

export const emptyStyle = {
  version: 8,
  name: 'e2e',
  glyphs: 'https://tiles.openfreemap.org/fonts/{fontstack}/{range}.pbf',
  sources: {},
  layers: [{ id: 'background', type: 'background', paint: { 'background-color': '#e8ecef' } }],
}

/** 10 000 incidents over 90 minutes in pages of 500 (E07): every 3rd a district area, the rest city markers across 5 spots. */
export function bulkIncidents(count = 10_000) {
  const spots: [number, number][] = [[36.23, 49.99], [35.0, 48.5], [30.5, 50.45], [32.0, 49.4], [37.5, 47.1]]
  const rows = []
  for (let i = 0; i < count; i++) {
    const spot = spots[i % 5]
    rows.push(
      incident({
        id: 10_000 + i,
        kind: i % 3 === 0 ? 'fire.reported' : 'impact.explosion.reported',
        minutesAgo: (i * 90) / count, // all inside the shortest kind lifetime (2 h): every row is drawn
        location: { kind: i % 3 === 0 ? 'district' : 'city', placeId: 9000 + (i % 5), placeName: `Місце ${i % 5}`, point: [spot[0] + ((i % 50) - 25) * 0.01, spot[1] + ((i % 40) - 20) * 0.01], accuracyKm: i % 3 === 0 ? 20 : 10, precision: i % 3 === 0 ? 'district' : 'city' },
      }),
    )
  }
  return rows
}

export interface MockOptions {
  incidents?: ReturnType<typeof incident>[]
  /** Records every /api/incidents request URL (assertions on history mode, throttle, resync). */
  requests?: string[]
  /** Records API URLs no fixture answers (they get a 404): a drifted route shows up in the assertion, not as proxy noise. */
  unexpected?: string[]
}

const glyphs = readFileSync(join(here, 'glyphs-0-255.pbf'))

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

/** Installs every route the map app calls. Call before `page.goto`. */
export async function mockApi(page: Page, opts: MockOptions = {}) {
  const all = opts.incidents ?? incidents
  // Playwright runs the most recently registered matching route first: the catch-alls go first, the specific ones after them.
  await page.route((url) => url.pathname.startsWith('/api/'), (r) => {
    opts.unexpected?.push(new URL(r.request().url()).pathname)
    return r.fulfill({ status: 404, body: '' })
  })
  await page.route('https://tiles.openfreemap.org/**', (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('https://tiles.openfreemap.org/styles/**', (r) => json(r, emptyStyle))
  await page.route('https://tiles.openfreemap.org/fonts/**', (r) => r.fulfill({ status: 200, contentType: 'application/x-protobuf', body: glyphs }))
  await page.route('**/hubs/map/negotiate**', (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('**/api/version', (r) => json(r, { version: 'e2e' }))
  await page.route('**/api/map/config', (r) => json(r, { lifetimeOptionsMinutes: [5, 10, 15, 20, 30, 45, 60, 120], maxLifetimeMinutes: 120, feedHours: 6, incidentHours: 24 }))
  await page.route('**/api/places/regions', (r) => json(r, regions))
  await page.route('**/api/places/*/geometry', (r) => {
    const id = Number(new URL(r.request().url()).pathname.split('/')[3])
    const region = regions.find((x) => x.id === id)
    return region ? json(r, region.geometry) : r.fulfill({ status: 404, body: '' })
  })
  await page.route('**/api/sources', (r) => json(r, [source]))
  await page.route('**/api/taxonomy', (r) => json(r, { categories: [] }))
  await page.route('**/api/event-kinds', (r) => json(r, eventKinds))
  await page.route('**/api/timeline**', (r) => json(r, []))
  await page.route('**/api/alerts/history**', (r) => json(r, []))
  await page.route('**/api/targets?**', (r) => json(r, [legacyExplosion, legacyCancel]))
  await page.route('**/api/snapshot**', (r) => {
    const url = new URL(r.request().url())
    const at = url.searchParams.get('at')
    return json(r, { at: at ?? NOW.toISOString(), historical: at !== null, tracks: [], alerts: [], events: [legacyExplosion, legacyCancel], incidents: [] })
  })
  await page.route('**/api/incidents/*', (r) => {
    const id = Number(new URL(r.request().url()).pathname.split('/')[3])
    return all.some((i) => i.id === id) ? json(r, details(id)) : r.fulfill({ status: 404, body: '' })
  })
  await page.route('**/api/incidents?**', (r) => {
    const url = new URL(r.request().url())
    opts.requests?.push(url.pathname + url.search)
    const limit = Number(url.searchParams.get('limit') ?? 200)
    const cursor = Number(url.searchParams.get('cursor') ?? 0)
    const page = all.slice(cursor, cursor + limit)
    const next = cursor + limit < all.length ? String(cursor + limit) : undefined
    return json(r, { from: url.searchParams.get('from') ?? '', to: url.searchParams.get('to') ?? NOW.toISOString(), mode: url.searchParams.get('mode') ?? 'effective', items: page, nextCursor: next, truncated: false })
  })
}
