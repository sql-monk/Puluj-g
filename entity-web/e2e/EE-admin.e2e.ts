import { expect, test, type Route } from '@playwright/test'

const ADMIN = process.env.ADMIN_E2E_BASE_URL ?? 'http://localhost:5184'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

test('EE Python editor highlights code, validates it, and saves without rebuilding', async ({ page }) => {
  const saves: unknown[] = []
  await page.route((url) => url.pathname.startsWith('/api/'), (route) => json(route, {}))
  await page.route('**/api/admin/settings', (route) => json(route, []))
  await page.route('**/api/admin/status', (route) => json(route, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (route) => json(route, []))
  await page.route('**/api/admin/ee/extractors', async (route) => {
    if (route.request().method() === 'PUT') {
      saves.push(route.request().postDataJSON())
      return json(route, { extractorId: 7 })
    }
    return json(route, [])
  })
  await page.route('**/api/admin/ee/extractors/validate', (route) => json(route, { valid: true, diagnostics: [] }))

  await page.goto(`${ADMIN}/#/ee-extractors`)
  await expect(page.getByRole('heading', { name: 'Python-екстрактори' })).toBeVisible()
  await expect(page.locator('.cm-editor')).toBeVisible()
  const keyword = page.locator('.cm-content span').filter({ hasText: 'def' }).first()
  await expect(keyword).toBeVisible()
  await expect(keyword).not.toHaveCSS('color', 'rgb(0, 0, 0)')

  await page.getByLabel('Назва').fill('explosion parser')
  await page.getByRole('button', { name: 'Перевірити синтаксис' }).click()
  await expect(page.getByText('"valid": true')).toBeVisible()
  await page.getByRole('button', { name: 'Зберегти' }).click()
  await expect(page.getByText('Екстрактор збережено й буде використаний без перебудови контейнера.')).toBeVisible()
  expect(saves).toHaveLength(1)
  expect(saves[0]).toMatchObject({ name: 'explosion parser', enabled: true })
})

test('EE entity editor creates a concrete table definition with map fields', async ({ page }) => {
  const creates: Record<string, unknown>[] = []
  let definitions: Record<string, unknown>[] = []
  await page.route((url) => url.pathname.startsWith('/api/'), (route) => json(route, {}))
  await page.route('**/api/admin/settings', (route) => json(route, []))
  await page.route('**/api/admin/status', (route) => json(route, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (route) => json(route, []))
  await page.route('**/api/admin/ee/definitions', async (route) => {
    if (route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>
      creates.push(body)
      definitions = [{ entity_definition_id: 9, entity_name: body.entityName, table_name: 'ee_explosions', fields: body.fields, map_settings: body.map, enabled: true }]
      return json(route, definitions[0])
    }
    return json(route, definitions)
  })

  await page.goto(`${ADMIN}/#/ee-definitions`)
  await expect(page.getByRole('heading', { name: 'Нова конкретна сутність' })).toBeVisible()
  await page.getByLabel('Назва, однина').fill('explosion')
  await page.getByRole('button', { name: '＋ поле' }).click()
  await page.getByLabel('Назва поля').nth(1).fill('latitude')
  await page.getByLabel('Тип поля').nth(1).selectOption('decimal')
  await page.getByRole('button', { name: '＋ поле' }).click()
  await page.getByLabel('Назва поля').nth(2).fill('longitude')
  await page.getByLabel('Тип поля').nth(2).selectOption('decimal')
  await page.getByLabel('показувати').check()
  await page.getByLabel('Latitude').fill('latitude')
  await page.getByLabel('Longitude').fill('longitude')
  await page.getByLabel('Час', { exact: true }).fill('occurredAt')
  await page.getByRole('button', { name: 'Створити таблицю' }).click()

  await expect(page.getByText('explosion', { exact: true })).toBeVisible()
  expect(creates).toHaveLength(1)
  expect(creates[0]).toMatchObject({ entityName: 'explosion', enabled: true, map: { enabled: true, renderer: 'point', latitudeField: 'latitude', longitudeField: 'longitude', timeField: 'occurredAt' } })
})

test('EE icon picker previews defaults and preserves a custom SVG on save', async ({ page }, testInfo) => {
  let map: Record<string, unknown> = { enabled: true, renderer: 'icon', svg: '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48"><circle cx="24" cy="24" r="20" fill="teal"/></svg>' }
  const saves: Record<string, unknown>[] = []
  await page.route(url => url.pathname.startsWith('/api/'), route => json(route, {}))
  await page.route('**/api/admin/settings', route => json(route, []))
  await page.route('**/api/admin/status', route => json(route, { adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', route => json(route, []))
  await page.route('**/api/admin/ee/definitions', route => json(route, [{ entity_definition_id: 9, entity_name: 'explosion', table_name: 'ee_explosions', fields: [], map_settings: map, enabled: true }]))
  await page.route('**/api/admin/ee/definitions/9/map', async route => {
    map = route.request().postDataJSON() as Record<string, unknown>
    saves.push(map)
    return json(route, {})
  })
  await page.goto(`${ADMIN}/#/ee-definitions`)
  await page.getByRole('button', { name: 'Налаштувати мапу' }).click()
  const original = map.svg
  await expect(page.getByLabel('SVG-піктограма', { exact: true })).toHaveValue(String(original))
  await page.getByRole('button', { name: 'Зберегти мапу' }).click()
  await expect.poll(() => saves.length).toBe(1)
  expect(saves[0].svg).toBe(original)
  await page.getByRole('button', { name: 'Налаштувати мапу' }).click()
  await page.getByRole('button', { name: 'Піктограма: Вибух', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Піктограма: Вибух', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.screenshot({ path: testInfo.outputPath('entity-icon-picker.png'), fullPage: true })
  await page.getByRole('button', { name: 'Типова піктограма', exact: true }).click()
  await expect(page.getByLabel('SVG-піктограма', { exact: true })).toHaveValue('')
  await page.getByRole('button', { name: 'Зберегти мапу' }).click()
  await expect.poll(() => saves.length).toBe(2)
  expect(saves[1]).toMatchObject({ renderer: 'icon', enabled: true })
  expect(saves[1].svg).toBeUndefined()
})
