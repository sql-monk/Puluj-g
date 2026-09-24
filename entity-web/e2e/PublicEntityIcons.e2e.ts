import { expect, test, type Page } from '@playwright/test'
import type { Map as MapLibreMap, GeoJSONSource } from 'maplibre-gl'
import type { FeatureCollection } from 'geojson'

const now = '2026-09-24T12:00:00Z'
async function fixture(page: Page) {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.clock.setFixedTime(new Date(now))
  const definitions = [
    { entityName: 'explosion', tableName: 'ee_explosions', map: { visible: true, renderer: 'icon' } },
    { entityName: 'custom', tableName: 'ee_custom', map: { visible: true, renderer: 'icon', svgIcon: 'not svg' } },
    { entityName: 'route', tableName: 'ee_routes', map: { visible: true, renderer: 'line' } },
    { entityName: 'track', tableName: 'ee_tracks', map: { visible: true, renderer: 'line' } },
  ].map(definition => ({ ...definition, enabled: true, fields: [] }))
  const items = definitions.map((definition, index) => ({ entity: definition.entityName, table: definition.tableName, id: String(index), occurredAt: now, values: { label: `Приклад ${index + 1}` }, geometry: definition.map.renderer === 'line' ? { type: 'LineString', coordinates: [[31, 49], [31.2, 49.1]] } : { type: 'Point', coordinates: [31 + index * 0.4, 49] } }))
  await page.route('https://tiles.openfreemap.org/**', route => route.fulfill({ json: { version: 8, sources: {}, layers: [] } }))
  await page.route(url => url.pathname.startsWith('/api/'), route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/ee/definitions' ? definitions : path === '/api/ee/snapshot' ? { generatedAt: now, items, truncated: false, limitPerEntity: 1000 } : path === '/api/map/config' ? { lifetimeOptionsMinutes: [15, 30, 60, 120], maxLifetimeMinutes: 120, feedHours: 24 } : []
    return route.fulfill({ json: body })
  })
  return errors
}
async function rendered(page: Page) {
  return page.evaluate(() => {
    const map = (window as unknown as { __map?: MapLibreMap }).__map
    const source = map?.getSource('ee-entities') as GeoJSONSource | undefined
    const data = source?.serialize().data as FeatureCollection | undefined
    return data?.features.map(feature => ({ entity: feature.properties?.entity, renderer: feature.properties?.renderer, icon: feature.properties?.icon })) ?? []
  })
}

test('default icon, invalid SVG fallback and custom lines survive a style reload; tracks stay absent', async ({ page }, testInfo) => {
  const errors = await fixture(page)
  await page.goto('/#/map/live')
  await expect.poll(() => rendered(page)).toEqual([
    { entity: 'explosion', renderer: 'icon', icon: expect.any(String) },
    { entity: 'custom', renderer: 'point' },
    { entity: 'route', renderer: 'line' },
  ])
  await expect(page.getByRole('button', { name: 'Записи карти (3)' })).toBeVisible()
  await expect.poll(() => page.evaluate(() => {
    const map = (window as unknown as { __map: MapLibreMap }).__map
    return [...new Set(map.queryRenderedFeatures({ layers: ['ee-entity-icons', 'ee-entity-points', 'ee-entity-lines'] }).map(feature => feature.properties.entity))].sort()
  })).toEqual(['custom', 'explosion', 'route'])
  await page.getByLabel('Кольорова тема').selectOption('light')
  await expect.poll(() => rendered(page).find(item => item.entity === 'explosion')?.renderer).toBe('icon')
  await page.getByLabel('Кольорова тема').selectOption('dark')
  await expect.poll(() => page.evaluate(() => (window as unknown as { __map: MapLibreMap }).__map.isStyleLoaded())).toBe(true)
  await page.evaluate(() => {
    const map = (window as unknown as { __map: MapLibreMap }).__map
    map.setStyle({ version: 8, sources: {}, layers: [{ id: 'background', type: 'background', paint: { 'background-color': '#162032' } }] }, { diff: false })
  })
  await expect.poll(() => rendered(page)).toEqual([
    { entity: 'explosion', renderer: 'icon', icon: expect.any(String) },
    { entity: 'custom', renderer: 'point' },
    { entity: 'route', renderer: 'line' },
  ])
  await page.screenshot({ path: testInfo.outputPath('entity-icons-style-reload.png') })
  await testInfo.attach('Entity icons after style reload', { path: testInfo.outputPath('entity-icons-style-reload.png'), contentType: 'image/png' })
  expect(errors).toEqual([])
})

test('old track detail link has an explicit unavailable state and a way back', async ({ page }) => {
  await fixture(page)
  await page.goto('/#/entities/track/4')
  await expect(page.getByRole('alert')).toContainText('Треки вимкнено')
  await expect(page.getByRole('link', { name: '← До каталогу' })).toBeVisible()
})
