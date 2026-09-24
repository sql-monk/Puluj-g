import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * Scenarios of docs/audits/admin-ui-reaudit-2026-09-24.md over a mocked /api/admin/*: each test repeats the re-audit's
 * steps and checks the fixed behaviour.
 */
const ADMIN = process.env.ADMIN_E2E_BASE_URL ?? 'http://localhost:5184'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const SOURCES = [
  { id: 86, code: 'tg_kpszsu', name: 'Повітряні сили', type: 'Telegram', enabled: true, trustLevel: 0.6, priority: 50, channel: 'kpszsu', hasToken: false, rawMessageCount: 10, consecutiveFailures: 0, status: 'ok', lastMessageAt: new Date().toISOString() },
  { id: 5, code: 'alerts_in_ua', name: 'alerts.in.ua', type: 'RestApi', enabled: true, trustLevel: 1, priority: 90, hasToken: true, rawMessageCount: 10, consecutiveFailures: 0, status: 'ok', lastMessageAt: '2026-09-07T06:09:00Z' },
]
const SETTINGS = [
  { key: 'Admin:Token', isSecret: true, hasValue: false, source: 'none' },
  { key: 'Correlation:SlackKm', value: '8', isSecret: false, hasValue: true, source: 'db' },
]

async function base(page: Page) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => json(r, {}))
  await page.route('**/api/admin/settings', (r) => json(r, SETTINGS))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: true, telegramConfigured: true, llmConfigured: false, adminTokenSet: false, workerAlive: true, telegramStatus: 'listening' }))
  await page.route('**/api/admin/sources', (r) => json(r, SOURCES))
}

test('R01 "Система" is clean without edits and after an edit is reverted', async ({ page }) => {
  await base(page)
  await page.goto(`${ADMIN}/#/system`)
  const slack = page.getByLabel('Просторовий запас (км)')
  await expect(slack).toHaveValue('8')
  await expect(page.getByText(/Є незбережені зміни/)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Зберегти' })).toBeDisabled()

  await slack.fill('10')
  await expect(page.getByText('Є незбережені зміни: Correlation:SlackKm')).toBeVisible()
  await slack.fill('8')
  await expect(page.getByText(/Є незбережені зміни/)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Зберегти' })).toBeDisabled()
})

test('R02 an edit of a Telegram post is marked and linked to its original', async ({ page }) => {
  await base(page)
  const row = (id: number, revision: string, publishedAt: string) => ({
    id, sourceId: 86, sourceCode: 'tg_kpszsu', sourceName: 'x', sourceMessageId: revision === '0' ? '79815' : `79815:${revision}`, publishedAt, receivedAt: '2026-09-24T00:08:34Z',
    processingStatus: 'Processed', attempts: 1, text: 'КАБи на Дніпропетровщину', textTruncated: false, url: 'https://t.me/kpszsu/79815', targets: 2,
    sourceMessageKey: '79815', sourceRevision: revision, revisions: 2,
  })
  await page.route('**/api/admin/ops/messages?*', (r) => json(r, { messages: [row(9663482, 'e1790208504', '2026-09-24T00:08:24Z'), row(9663471, '0', '2026-09-24T00:08:13Z')] }))

  await page.goto(`${ADMIN}/#/messages?sourceId=86`)
  const badges = page.getByTestId('message-revision')
  await expect(badges).toHaveText(['редакція', 'версій: 2'])
  await expect(badges.first()).toHaveAttribute('title', /час редагування/)
})

test('R06/R07 alerts.in.ua has the messages link too, and a quiet source says so beside a healthy poll', async ({ page }) => {
  await base(page)
  await page.goto(`${ADMIN}/#/sources`)
  const alerts = page.getByRole('row', { name: /alerts\.in\.ua/ })
  await expect(alerts.getByRole('link', { name: 'Дивитись повідомлення' })).toHaveAttribute('href', '#/messages?sourceId=5')
  await expect(alerts).toContainText('опитування працює')
  await expect(alerts).toContainText(/тиша \d+ дн/)
  await expect(page.getByRole('row', { name: /Повітряні сили/ })).not.toContainText('тиша')
})

test('R08 the log selects have accessible names', async ({ page }) => {
  await base(page)
  await page.route('**/api/admin/logs/files', (r) => json(r, [{ name: 'processor-20260924.log', service: 'processor', bytes: 10, modifiedAt: '2026-09-24T00:00:00Z' }]))
  await page.route('**/api/admin/logs?*', (r) => json(r, { file: 'processor-20260924.log', lines: ['[INF] ok'], truncated: false, bytes: 10 }))
  await page.goto(`${ADMIN}/#/logs`)
  await expect(page.getByRole('combobox', { name: 'Файл журналу' })).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Рівень' })).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Кількість рядків' })).toBeVisible()
})
