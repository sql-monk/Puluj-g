import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * The admin catalog editor over a mocked /api/admin/*: every change needs actor + reason, validation problems are shown,
 * and a legacy-mapped kind refuses `enabled=false` without force.
 */
const ADMIN = process.env.ADMIN_E2E_BASE_URL ?? 'http://localhost:5184'

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const kinds = [
  { eventKindId: 5, code: 'impact.explosion.reported', nameUk: 'Повідомлення про вибух', category: 'event', requiresLocationForMap: true, renderMode: 'area', mapColor: '#fb8c00', mapIcon: 'explosion', mapLifetime: '02:00:00', enabled: true, mapVisible: true, sortOrder: 40, policyVersion: 2, legacyEventType: 'ExplosionReport' },
  { eventKindId: 7, code: 'fire.reported', nameUk: 'Пожежа', category: 'event', requiresLocationForMap: true, renderMode: 'area', mapColor: '#ef6c00', mapIcon: 'fire', mapLifetime: '06:00:00', enabled: true, mapVisible: true, sortOrder: 50, policyVersion: 2 },
]

async function mockAdmin(page: Page, puts: unknown[]) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('**/api/admin/settings', (r) => json(r, []))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (r) => json(r, []))
  await page.route('**/api/admin/event-kinds', (r) => json(r, { iconVocabulary: ['explosion', 'fire', 'unknown'], renderModes: ['marker', 'area', 'feed'], kinds }))
  await page.route('**/api/admin/event-kinds/*/audit', (r) => json(r, [{ auditId: 1, action: 'updated', actor: 'ops', reason: 'колір', at: '2026-09-16T10:00:00Z', before: { mapColor: '#ef6c00' }, after: { mapColor: '#123456' } }]))
  await page.route('**/api/admin/event-kinds/*', async (r) => {
    if (r.request().method() !== 'PUT') return r.fallback()
    const body = r.request().postDataJSON() as Record<string, unknown>
    puts.push(body)
    if (!body.actor || !body.reason) return json(r, { error: 'actor and reason are required' }, 400)
    if (typeof body.mapColor === 'string' && !/^#[0-9a-f]{6}$/i.test(body.mapColor)) return json(r, { error: 'invalid presentation', problems: ['mapColor must be #rrggbb'] }, 422)
    if (body.enabled === false && !body.force) return json(r, { error: 'maps a legacy EventType: disabling it silences its rules — pass force=true' }, 409)
    return json(r, { ...kinds[0], ...body, presentationOverriddenAt: '2026-09-16T12:00:00Z' })
  })
}

test.describe('admin', () => {
  test('A01 catalog editor: actor/reason required, 422 shown, legacy kind refuses disable without force, audit history', async ({ page }) => {
    const puts: Record<string, unknown>[] = []
    await mockAdmin(page, puts)
    await page.goto(`${ADMIN}/#/catalog`)
    const row = page.getByTestId('kind-fire.reported')
    await expect(row).toContainText('Пожежа')
    await row.getByRole('button', { name: 'Редагувати' }).click()
    // Without actor/reason the save is disabled.
    await expect(row.getByRole('button', { name: 'Зберегти' })).toBeDisabled()
    await page.getByLabel('Actor').fill('ops')
    await page.getByLabel('Reason').fill('новий колір')
    await row.getByLabel('Колір').fill('orange')
    await row.getByRole('button', { name: 'Зберегти' }).click()
    await expect(page.getByRole('status')).toContainText('mapColor must be #rrggbb') // the 422 problems are shown, nothing saved
    await row.getByLabel('Колір').fill('#123456')
    await row.getByRole('button', { name: 'Зберегти' }).click()
    await expect(page.getByRole('status')).toContainText('збережено')
    const saved = puts.find((p) => p.mapColor === '#123456')!
    expect(saved).toMatchObject({ actor: 'ops', reason: 'новий колір', mapColor: '#123456' })
    expect(saved).not.toHaveProperty('mapIcon') // only the changed fields travel
    await expect(page.getByRole('status')).toContainText('до 10 хв') // the cache lag is said, not hidden

    // Disabling a legacy-mapped kind: 409 → confirm → force.
    page.on('dialog', (d) => void d.accept())
    const explosion = page.getByTestId('kind-impact.explosion.reported')
    await explosion.getByRole('button', { name: 'Редагувати' }).click()
    await page.getByLabel('Reason').fill('вимкнути')
    await explosion.getByLabel('Увімкнено').uncheck()
    await explosion.getByRole('button', { name: 'Зберегти' }).click()
    await expect(page.getByRole('status')).toContainText('збережено')
    expect(puts.filter((p) => p.enabled === false).map((p) => p.force ?? false)).toEqual([false, true]) // first refused (409), then confirmed with force
    await page.getByTestId('kind-fire.reported').getByRole('button', { name: 'Аудит' }).click()
    await expect(page.getByText('Аудит: fire.reported')).toBeVisible()
    await expect(page.getByText('mapColor: #ef6c00 → #123456')).toBeVisible()
  })
})
