import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * A01/A02 (§8.7, P12): the admin catalog editor and the incident review queue over a mocked /api/admin/* — every change
 * needs actor + reason, validation problems are shown, a legacy-mapped kind refuses `enabled=false` without force, the
 * merge goes through a preview and refuses a stale confirm; source text is rendered as text (never HTML).
 */
const ADMIN = 'http://localhost:5184'

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const kinds = [
  { eventKindId: 5, code: 'impact.explosion.reported', nameUk: 'Повідомлення про вибух', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#fb8c00', mapIcon: 'explosion', mapLifetime: '02:00:00', createsIncident: true, enabled: true, mapVisible: true, sortOrder: 40, policyVersion: 2, legacyEventType: 'ExplosionReport' },
  { eventKindId: 7, code: 'fire.reported', nameUk: 'Пожежа', category: 'incident', requiresLocationForMap: true, renderMode: 'area', mapColor: '#ef6c00', mapIcon: 'fire', mapLifetime: '06:00:00', createsIncident: true, enabled: true, mapVisible: true, sortOrder: 50, policyVersion: 2 },
]

async function mockAdmin(page: Page, puts: unknown[], merges: unknown[]) {
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
  // The target's revision moves once after the first preview (someone else linked a fact), then stays: the first confirm is stale, the second is fresh.
  let previews = 0
  await page.route('**/api/admin/incidents/review**', (r) =>
    json(r, {
      since: '',
      count: 1,
      items: [{ incidentId: 3, kind: 'impact.explosion.reported', state: 'reported', suppressed: false, eventAt: '2026-09-16T11:00:00Z', lastReportedAt: '2026-09-16T11:05:00Z', sourceCount: 1, revision: 1, flags: [{ observationId: 'o3', relation: 'ambiguous', ambiguous: [1, 2], near: [] }], candidates: [{ incidentId: 1, kind: 'impact.explosion.reported', state: 'reported', suppressed: false, revision: 2, sameKind: true, mergeable: true }, { incidentId: 2, kind: 'impact.explosion.reported', state: 'retracted', suppressed: false, revision: 3, sameKind: true, mergeable: false }] }],
    }),
  )
  await page.route('**/api/admin/incidents/3/merge-preview**', (r) => json(r, { sourceId: 3, targetId: 1, allowed: true, refusal: null, movedObservationIds: ['o3'], sourceCountAfter: 2, firstReportedAtAfter: '2026-09-16T10:00:00Z', lastReportedAtAfter: '2026-09-16T11:05:00Z', stateAfter: 'reported', locationFrom: 'target', accuracyKmAfter: 15, confidenceAfter: 'medium', targetRevision: ++previews === 1 ? 2 : 3, sourceRevision: 1 }))
  await page.route('**/api/admin/incidents/merge', (r) => {
    merges.push(r.request().postDataJSON())
    return json(r, [{ incidentId: 3, change: 'merged', state: 'retracted', suppressed: false, revision: 2 }, { incidentId: 1, change: 'updated', state: 'reported', suppressed: false, revision: 3 }])
  })
  await page.route('**/api/admin/incidents/3', (r) =>
    json(r, {
      incidentId: 3, kind: 'impact.explosion.reported', state: 'reported', suppressed: false, eventAt: '2026-09-16T11:00:00Z', firstReportedAt: '2026-09-16T11:00:00Z', lastReportedAt: '2026-09-16T11:05:00Z', sourceCount: 1, revision: 1,
      observations: [{ observationId: 'o3', legacyTargetId: 103, sourceId: 1, sourceCode: 'tg_test', relation: 'ambiguous', score: 0.7, effectiveAt: '2026-09-16T11:00:00Z', segmentText: '<b>Вибухи</b> у Харкові <script>alert(1)</script>', rawText: 'raw', rawUrl: 'javascript:alert(1)' }],
      revisions: [{ revision: 1, change: 'created', effectiveAt: '2026-09-16T11:00:00Z', recordedAt: '2026-09-16T11:01:00Z', actor: 'incident-worker@w1' }],
    }),
  )
  await page.route('**/api/admin/incidents/1', (r) => json(r, { incidentId: 1, kind: 'impact.explosion.reported', state: 'reported', suppressed: false, eventAt: '2026-09-16T10:00:00Z', firstReportedAt: '2026-09-16T10:00:00Z', lastReportedAt: '2026-09-16T11:05:00Z', sourceCount: 2, revision: 3, observations: [], revisions: [] }))
}

test.describe('admin', () => {
  test('A01 catalog editor: actor/reason required, 422 shown, legacy kind refuses disable without force, audit history', async ({ page }) => {
    const puts: Record<string, unknown>[] = []
    await mockAdmin(page, puts, [])
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

  test('A02 review queue: candidates, merge via preview with a stale-revision guard, escaped source text, no javascript: links', async ({ page }) => {
    const merges: Record<string, unknown>[] = []
    await mockAdmin(page, [], merges)
    await page.goto(`${ADMIN}/#/incidents`)
    const row = page.getByTestId('review-3')
    await expect(row).toContainText('ambiguous 1/2')
    await expect(row.locator('span', { hasText: '#2' })).toHaveClass(/line-through/) // a retracted candidate is offered as not mergeable
    await row.getByRole('button', { name: '3' }).click()
    const card = page.locator('section', { hasText: 'Інцидент #3' })
    await expect(card).toBeVisible()
    // Source text is text: the markup shows literally, the script never runs, the javascript: url is not a link.
    await expect(card).toContainText('<b>Вибухи</b> у Харкові <script>alert(1)</script>')
    expect(await card.locator('script').count()).toBe(0)
    expect(await card.getByRole('link', { name: 'оригінал' }).count()).toBe(0)
    // Commands are disabled until actor + reason are given.
    await expect(card.getByRole('button', { name: 'confirm' })).toBeDisabled()
    await page.getByLabel('Actor').fill('ops')
    await page.getByLabel('Reason').fill('один вибух')
    await card.getByLabel('Target incident id').fill('1')
    await card.getByRole('button', { name: 'preview' }).click()
    const preview = page.getByTestId('merge-preview')
    await expect(preview).toContainText('перенесеться 1 observation')
    await expect(preview).toContainText('джерела 2')
    await expect(preview).toContainText('не змінюється')
    // The target moved on since the preview: the confirm is refused, nothing merged.
    await preview.getByRole('button', { name: 'Підтвердити злиття' }).click()
    await expect(page.getByRole('status')).toContainText('змінився після preview')
    expect(merges).toHaveLength(0)
    // A fresh preview, then confirm: the merge command carries actor/reason and the pair.
    await card.getByRole('button', { name: 'preview' }).click()
    await expect(preview).toContainText('target rev 3')
    await preview.getByRole('button', { name: 'Підтвердити злиття' }).click()
    await expect(page.getByRole('status')).toContainText('merge #3 → #1: виконано')
    expect(merges).toEqual([{ sourceId: 3, targetId: 1, actor: 'ops', reason: 'один вибух' }])
  })
})
