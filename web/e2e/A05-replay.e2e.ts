import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * A05 (§11, P14): the replay panel over a mocked /api/admin/ops/runs — runs with their generation and checkpoint, the state machine's
 * commands gated by actor + reason, promote only offered for a verified run and its confirmation names the generation and the verify
 * counts, the verify report is shown, a failed run shows its error in full, a 409 from the server is shown as is.
 */
const ADMIN = 'http://localhost:5184'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const G_NEW = '0199a1b2-c3d4-7e5f-8a6b-1c2d3e4f5a6b'
const verification = { published: 1200, total: 1200, pending_deliveries: 0, quarantined: 0, unconfirmed_outbox: 0, terminal_deliveries: 4800, stages: { 'normalize:completed': 1200, 'parse:completed': 1200 }, extractions: 1200, observations: 340, unanalyzed: 0, incidents_in_generation: 57, active_incidents_in_window: 60, active_incidents_outside_scope: 0, active_incidents_missing_in_generation: 3, verified_at: '2026-09-16T12:00:00Z' }
const runs = [
  { runId: 'aaaaaaaa-0000-7000-8000-000000000001', lane: 'replay', kind: 'replay', state: 'verified', generationId: G_NEW, generationActive: false, verifiedBy: 'ops', scope: { from: '2026-09-15T00:00:00Z', to: '2026-09-16T00:00:00Z', source_ids: [1, 2], verification }, checkpoint: { published: 1200, total: 1200, lastRawMessageId: 99000, done: true }, createdBy: 'ops', createdAt: '2026-09-16T10:00:00Z', updatedAt: '2026-09-16T12:00:00Z' },
  { runId: 'bbbbbbbb-0000-7000-8000-000000000002', lane: 'replay', kind: 'replay', state: 'failed', generationId: '0199a1b2-c3d4-7e5f-8a6b-000000000002', generationActive: false, scope: { from: '2026-09-10T00:00:00Z', to: '2026-09-11T00:00:00Z' }, checkpoint: { published: 400, total: 900, lastRawMessageId: 88000, done: false, error: 'Npgsql.PostgresException: 57014: canceling statement due to statement timeout' }, createdBy: 'ops', createdAt: '2026-09-11T10:00:00Z', updatedAt: '2026-09-11T10:30:00Z' },
  { runId: 'cccccccc-0000-7000-8000-000000000003', lane: 'live', kind: 'live', state: 'running', generationId: null, generationActive: false, versions: { pipeline_version: '1.2.0+abc' }, checkpoint: null, createdBy: 'processor-1', createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-01T00:00:00Z' },
]

async function mockAdmin(page: Page, posts: { url: string; body: unknown }[]) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('**/api/admin/settings', (r) => json(r, []))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (r) => json(r, []))
  await page.route('**/api/admin/ops/runs**', (r) => {
    const path = new URL(r.request().url()).pathname
    if (r.request().method() === 'POST') {
      posts.push({ url: path + new URL(r.request().url()).search, body: r.request().postDataJSON() })
      if (path.endsWith('/cancel')) return json(r, { error: 'run bbbbbbbb-0000-7000-8000-000000000002 changed state concurrently' }, 409)
      if (path.endsWith('/verify')) return json(r, { ok: true, report: verification })
      if (path.endsWith('/promote')) return json(r, { ok: true, generation: G_NEW })
      return json(r, { ok: true, runId: 'dddddddd-0000-7000-8000-000000000004' })
    }
    return json(r, runs)
  })
}

test.describe('admin replay', () => {
  test('A05 replay panel: runs, gated commands, promote confirmation names the generation, verify report, failed run error, 409 shown', async ({ page }) => {
    const posts: { url: string; body: unknown }[] = []
    await mockAdmin(page, posts)
    const dialogs: string[] = []
    page.on('dialog', (d) => {
      dialogs.push(d.message())
      void d.accept()
    })
    await page.goto(`${ADMIN}/#/replay`)
    const verified = page.getByTestId('run-aaaaaaaa-0000-7000-8000-000000000001')
    await expect(verified).toContainText('перевірено')
    await expect(verified).toContainText(/1[\s  ]200\/1[\s  ]200 ✓/) // uk-UA digit grouping uses a narrow no-break space
    const failed = page.getByTestId('run-bbbbbbbb-0000-7000-8000-000000000002')
    await expect(failed).toContainText('ПОМИЛКА')
    await expect(failed).toContainText('57014: canceling statement due to statement timeout')
    await expect(failed.getByRole('button', { name: 'продовжити' })).toBeVisible()
    await expect(page.getByTestId('run-cccccccc-0000-7000-8000-000000000003')).toContainText('live/live')
    // Only a verified run offers promote; nothing is enabled without actor + reason.
    expect(await page.getByRole('button', { name: 'активувати' }).count()).toBe(1)
    const promote = verified.getByRole('button', { name: 'активувати' })
    await expect(promote).toBeDisabled()
    await page.getByLabel('actor').fill('ops')
    await page.getByLabel('reason').fill('catalog v3 rebuild')
    await expect(promote).toBeEnabled()
    // The stored verify report is one click away, before promoting.
    await verified.getByRole('button', { name: 'звіт' }).click()
    const report = page.getByTestId('verify-report')
    await expect(report).toContainText('active_incidents_missing_in_generation')
    await expect(report).toContainText('57')
    await promote.click()
    expect(dialogs).toHaveLength(1)
    expect(dialogs[0]).toContain(`Активувати generation ${G_NEW}`)
    expect(dialogs[0]).toContain('у generation 57 incidents')
    expect(dialogs[0]).toContain('відсутні в generation 3')
    expect(dialogs[0]).not.toContain('FORCE')
    await expect(page.getByRole('status')).toContainText('активувати aaaaaaaa: виконано (ops)')
    expect(posts).toEqual([{ url: '/api/admin/ops/runs/aaaaaaaa-0000-7000-8000-000000000001/promote', body: { actor: 'ops', reason: 'catalog v3 rebuild', watermark: null } }])
    // Force is an explicit choice: the checkbox changes the confirmation and the request.
    await page.getByLabel('force').check()
    await promote.click()
    expect(dialogs[1]).toContain('FORCE')
    expect(posts[1].url).toBe('/api/admin/ops/runs/aaaaaaaa-0000-7000-8000-000000000001/promote?force=true')
    await page.getByLabel('force').uncheck()
    // A refused transition (409) is shown with the server's reason, not swallowed.
    await failed.getByRole('button', { name: 'скасувати' }).click()
    expect(dialogs[2]).toContain('Скасувати run bbbbbbbb')
    await expect(page.getByRole('status')).toContainText('changed state concurrently')
    // Create: from/to/sources/actor/reason travel; the new run id comes back.
    await page.getByLabel('джерела').fill('1, 2')
    await page.getByRole('button', { name: 'Створити' }).click()
    await expect(page.getByRole('status')).toContainText('створити replay run: виконано')
    const created = posts.find((p) => p.url === '/api/admin/ops/runs/replay')!
    expect(created.body).toMatchObject({ sourceIds: [1, 2], actor: 'ops', reason: 'catalog v3 rebuild' })
  })
})
