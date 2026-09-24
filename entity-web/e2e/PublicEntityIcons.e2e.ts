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
  await expect.poll(async () => (await rendered(page)).find(item => item.entity === 'explosion')?.renderer).toBe('icon')
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
  await expect(page.getByRole('main').getByRole('alert')).toContainText('Треки вимкнено')
  await expect(page.getByRole('link', { name: '← До каталогу' })).toBeVisible()
})


test('weapon silhouettes remain distinct on the map and in both theme previews', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await fixture(page)
  const types = ['shahed_drone', 'geran_drone', 'jet_drone', 'cruise_missile', 'ballistic_missile']
  await page.route('**/api/ee/definitions', route => route.fulfill({ json: [{ entityName: 'target', tableName: 'ee_targets', enabled: true, fields: [], map: { visible: true, renderer: 'icon' } }] }))
  await page.route('**/api/ee/snapshot', route => route.fulfill({ json: { generatedAt: now, items: types.map((targetType, i) => ({ entity: 'target', table: 'ee_targets', id: String(i), occurredAt: now, values: { targetType }, geometry: { type: 'Point', coordinates: [30 + i * .5, 49] } })), truncated: false, limitPerEntity: 1000 } }))
  await page.goto('/#/map/live')
  await expect.poll(async () => (await rendered(page)).filter(item => item.renderer === 'icon').length).toBe(5)
  expect(new Set((await rendered(page)).map(item => item.icon)).size).toBe(5)
  await page.evaluate(async () => {
    const path = '/src/entities/presentation.ts'
    const { entityIconChoices, entityIconSvg, entityLabel } = await import(/* @vite-ignore */ path)
    ;(window as unknown as { __map?: MapLibreMap }).__map?.remove()
    document.body.innerHTML = ''
    for (const dark of [false, true]) {
      const section = document.createElement('section')
      section.style.cssText = `display:flex;flex-wrap:wrap;gap:16px;padding:24px;background:${dark ? '#162032' : '#f1f5f9'};color:${dark ? '#fff' : '#172033'}`
      for (const name of entityIconChoices) {
        const figure = document.createElement('div')
        figure.style.cssText = 'width:110px;text-align:center;font:13px system-ui'
        const img = new Image(48, 48)
        img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(entityIconSvg(name))
        figure.append(img, document.createElement('br'), entityLabel(name))
        section.append(figure)
      }
      document.body.append(section)
    }
    await Promise.all(Array.from(document.images).map(image => image.decode()))
  })
  await page.screenshot({ path: testInfo.outputPath('weapon-silhouettes.png'), fullPage: true })
})
