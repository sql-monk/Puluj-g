import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * Scenarios of docs/audits/admin-ui-audit-2026-09-24.md over a mocked /api/admin/*: each test repeats the audit's steps and
 * checks the fixed behaviour.
 */
const ADMIN = process.env.ADMIN_E2E_BASE_URL ?? 'http://localhost:5184'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

const source = (id: number, code: string, name: string, over: Record<string, unknown> = {}) => ({
  id, code, name, type: 'Telegram', enabled: true, trustLevel: 0.6, priority: 50, channel: code.replace(/^tg_/, ''), hasToken: false, rawMessageCount: 10, consecutiveFailures: 0, status: 'ok', ...over,
})
const SOURCES = [source(86, 'tg_kpszsu', 'Повітряні сили'), source(87, 'tg_monitor', 'Монітор')]

async function base(page: Page) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => json(r, {}))
  await page.route('**/api/admin/settings', (r) => json(r, []))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: true, telegramConfigured: true, llmConfigured: false, adminTokenSet: false, workerAlive: true, telegramStatus: 'listening' }))
  await page.route('**/api/admin/sources', (r) => json(r, SOURCES))
}

const message = (id: number, sourceId: number, text: string) => ({
  id, sourceId, sourceCode: sourceId === 86 ? 'tg_kpszsu' : 'tg_monitor', sourceName: 'x', sourceMessageId: String(id), publishedAt: '2026-09-24T00:00:00Z', receivedAt: '2026-09-24T00:00:05Z', processingStatus: 'Processed', attempts: 1, text, textTruncated: false, targets: 1,
})

test('A01 "Дивитись повідомлення" opens the messages of the chosen source, for two sources', async ({ page }) => {
  await base(page)
  const requested: string[] = []
  await page.route('**/api/admin/ops/collectors', (r) => json(r, SOURCES.map((s) => ({ sourceId: s.id, code: s.code, name: s.name, type: 'Telegram', enabled: true, consecutiveFailures: 0, messages24h: 3, perHour: Array(24).fill(0), channel: s.channel, lastSuccessAt: '2026-09-24T00:00:00Z' }))))
  await page.route('**/api/admin/ops/messages?*', (r) => {
    const url = new URL(r.request().url())
    requested.push(url.search)
    const sourceId = Number(url.searchParams.get('sourceId'))
    if (url.searchParams.get('cursor')) return json(r, { messages: [message(sourceId * 10, sourceId, 'старіше')] })
    return json(r, { messages: [message(sourceId * 100, sourceId, `Пост джерела ${sourceId}`)], nextCursor: 'c1' })
  })

  await page.goto(`${ADMIN}/#/collectors`)
  const row = page.getByRole('row', { name: /Повітряні сили/ })
  await row.getByRole('link', { name: 'Дивитись повідомлення' }).click()
  await expect(page).toHaveURL(/#\/messages\?sourceId=86$/)
  await expect(page.getByRole('heading', { name: 'Повідомлення: Повітряні сили' })).toBeVisible()
  await expect(page.getByText('Пост джерела 86')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Стан компонентів' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Показати ще' }).click()
  await expect(page.getByTestId('admin-message')).toHaveCount(2)

  await page.goto(`${ADMIN}/#/sources`)
  await page.getByRole('row', { name: /Монітор/ }).getByRole('link', { name: 'Дивитись повідомлення' }).click()
  await expect(page.getByText('Пост джерела 87')).toBeVisible()
  await expect(page.getByLabel('Джерело повідомлень')).toHaveValue('87')
  expect(requested.some((q) => q.includes('sourceId=87'))).toBe(true)
})

test('A02 Workers show "завантаження…" while the answer is pending, then the received state', async ({ page }) => {
  await base(page)
  await page.route('**/api/admin/ops/workers', async (r) => {
    await delay(1_500)
    return json(r, [{ name: 'processor', kind: 'processor', alive: true, heartbeatAt: new Date().toISOString(), processed24h: 5, inProgress: 0 }])
  })
  await page.route('**/api/admin/ops/containers', async (r) => {
    await delay(1_500)
    return json(r, { available: true, project: 'puluj-g', containers: [], processorReplicas: 1 })
  })
  await page.goto(`${ADMIN}/#/workers`)
  const block = page.locator('section', { hasText: 'Процесор повідомлень' }).first()
  await expect(block).toContainText('завантаження…')
  await expect(block).not.toContainText('не працює')
  await expect(block).not.toContainText('Docker недоступний')
  await expect(block).toContainText('працює', { timeout: 5_000 })
  await expect(block).not.toContainText('завантаження…')
})

test('A04 overview lists the Entity Extractor with the age of its failures', async ({ page }) => {
  await base(page)
  await page.route('**/api/admin/ops/overview', (r) => json(r, {
    generatedAt: new Date().toISOString(), processorCount: 1,
    db: { version: 'PostgreSQL 17', sizeBytes: 1, connections: 1, migrationCount: 42 },
    services: [{ name: 'entity-extractor', status: 'ok', detail: 'health 200 · черга 0 · невдалих: 0 за годину, 0 за 24 год, 19 137 усього (остання 2 дн тому) · LLM вимкнено навмисно' }],
  }))
  await page.goto(`${ADMIN}/#/overview`)
  const row = page.getByRole('row', { name: /Entity Extractor/ })
  await expect(row).toContainText('19 137 усього (остання 2 дн тому)')
  await expect(row).toContainText('ok')
})

test('A03 A05 EE queue: plain statuses, a failure filter, paging; settings show the applied values', async ({ page }) => {
  await base(page)
  const statuses: string[] = []
  const saved: unknown[] = []
  await page.route('**/api/admin/ee/overview', (r) => json(r, {
    queue: { queued: 0, inProgress: 0, failed: 19137, failedLastHour: 0, failedLast24h: 0, lastFailureAt: '2026-09-22T10:00:00Z', lastError: 'HTTP 500: LLM circuit breaker is paused', lastSuccessAt: new Date().toISOString() },
    extractors: 8, definitions: 8, activeRuns: 0, extractor: { available: true, statusCode: 200 }, llmEnabled: false,
    status: { name: 'entity-extractor', status: 'ok', detail: 'health 200' },
  }))
  await page.route('**/api/admin/ee/settings', async (r) => {
    if (r.request().method() === 'PUT') {
      saved.push(r.request().postDataJSON())
      return json(r, { saved: 1 })
    }
    return json(r, [
      { key: 'EntityExtractor:Url', label: 'Адреса Entity Extractor', source: 'config', effective: 'http://entity-extractor:8080', default: 'http://entity-extractor:8080', format: 'http(s)://хост:порт', hint: '' },
      { key: 'EntityExtractor:DeliveryTimeout', label: 'Таймаут доставки', source: 'default', effective: '00:00:30', default: '00:00:30', format: 'гг:хх:сс', hint: 'від 1 с до 10 хв' },
    ])
  })
  const delivery = (id: string, status: string, over: Record<string, unknown> = {}) => ({ delivery_id: id, raw_message_id: 501, source_id: 86, source_code: 'tg_kpszsu', origin: 'live', status, enqueued_at: '2026-09-24T00:00:00Z', attempts: 1, result: status === 'succeeded' ? 0 : null, ...over })
  await page.route('**/api/admin/ee/deliveries?*', (r) => {
    const url = new URL(r.request().url())
    const status = url.searchParams.get('status') ?? 'all'
    statuses.push(status)
    if (status === 'failed') {
      if (url.searchParams.get('cursor')) return json(r, { items: [delivery('f2', 'failed', { last_error: 'HTTP 500: older' })] })
      return json(r, { items: [delivery('f1', 'failed', { last_error: 'HTTP 500: LLM circuit breaker is paused' })], nextCursor: 'next' })
    }
    return json(r, { items: [delivery('s1', 'succeeded')] })
  })
  await page.route('**/api/admin/ee/runs*', (r) => json(r, { items: [] }))

  await page.goto(`${ADMIN}/#/ee-operations`)
  await expect(page.getByText('історична, нових помилок за останню годину немає')).toBeVisible()
  const queue = page.locator('section', { hasText: 'Черга доставок' })
  await expect(queue.getByTestId('ee-delivery')).toContainText(['доставлено'])
  await expect(queue.getByTestId('ee-delivery').first()).toContainText('без сутностей')
  await queue.getByRole('button', { name: 'помилки' }).click()
  await expect(queue.getByTestId('ee-delivery').first()).toContainText('LLM circuit breaker is paused')
  await queue.getByRole('button', { name: 'Показати ще' }).click()
  await expect(queue.getByTestId('ee-delivery')).toHaveCount(2)
  await expect(queue.getByRole('link', { name: '#501' }).first()).toHaveAttribute('href', '#/messages?rawMessageId=501')
  expect(statuses).toContain('failed')

  const settings = page.locator('section', { hasText: 'Налаштування доставки' })
  await expect(settings).toContainText('Таймаут доставки')
  await expect(settings).toContainText('Діє зараз: 00:00:30 · типове значення')
  await expect(settings.getByRole('button', { name: 'Зберегти налаштування' })).toBeDisabled()
  await settings.getByPlaceholder('00:00:30').fill('00:00:45')
  await settings.getByRole('button', { name: 'Зберегти налаштування' }).click()
  await expect(settings.getByRole('status')).toContainText('Збережено')
  expect(saved).toEqual([{ values: { 'EntityExtractor:DeliveryTimeout': '00:00:45' } }])
})

test('A06 A07 sources: empty search is explained, a hidden selection is named and can be reset', async ({ page }) => {
  await base(page)
  await page.goto(`${ADMIN}/#/sources`)
  const search = page.getByLabel('Пошук джерел за назвою')
  await search.fill('kpszsu')
  await expect(page.getByText('Знайдено 1 із 2')).toBeVisible()
  await page.getByLabel('Вибрати всі видимі джерела').check()
  await search.fill('zz_audit_no_such_source')
  await expect(page.getByText('Знайдено 0 із 2')).toBeVisible()
  await expect(page.getByText('Нічого не знайдено за «zz_audit_no_such_source».')).toBeVisible()
  await expect(page.getByTestId('selection-count')).toContainText('Вибрано: 1 · 1 поза поточним пошуком')
  let confirmText = ''
  page.once('dialog', (d) => {
    confirmText = d.message()
    void d.dismiss()
  })
  await page.getByRole('button', { name: 'Вимкнути' }).first().click()
  expect(confirmText).toContain('Повітряні сили')
  expect(confirmText).toContain('не видно в поточному пошуку')
  await page.getByRole('button', { name: 'Скинути вибір' }).click()
  await expect(page.getByTestId('selection-count')).toHaveCount(0)
  await page.getByRole('button', { name: 'Очистити пошук' }).first().click()
  await expect(search).toHaveValue('')
})

test('A08 map editing is its own mode: schema read-only, can be cancelled', async ({ page }) => {
  await base(page)
  const definition = { entity_definition_id: 3, entity_name: 'airDefenseAction', table_name: 'ee_air_defense_actions', fields: [{ name: 'occurredAt', type: 'datetime', required: true }, { name: 'place', type: 'point', required: false }], map_settings: { enabled: true, renderer: 'point', geometryField: 'place' }, enabled: true }
  await page.route('**/api/admin/ee/definitions', (r) => json(r, [definition]))
  await page.goto(`${ADMIN}/#/ee-definitions`)
  await page.getByRole('button', { name: 'Налаштувати мапу' }).click()
  await expect(page.getByRole('heading', { name: 'Мапа: airDefenseAction' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Нова конкретна сутність' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Видалити поле' })).toHaveCount(0)
  await expect(page.getByLabel('Поля сутності')).toContainText('place: point')
  await expect(page.getByLabel('Поле geometry')).toHaveValue('place')
  await page.getByRole('button', { name: 'Скасувати' }).first().click()
  await expect(page.getByRole('heading', { name: 'Нова конкретна сутність' })).toBeVisible()
})

test('A09 A13 SQL error shows the reason and position; the table browser pages past 50 rows', async ({ page }) => {
  await base(page)
  await page.route('**/api/admin/ops/db', (r) => json(r, { version: 'PostgreSQL 17', sizeBytes: 1, tables: [{ name: 'places', rows: 120, bytes: 1, inserts: 0, updates: 0, deletes: 0, deadRows: 0 }], migrations: [], connections: [], monitoring: { activeConnections: 1, idleConnections: 0, transactionsCommitted: 0, transactionsRolledBack: 0, cacheHitRatio: 1, deadRows: 0 } }))
  await page.route('**/api/admin/ops/db/query', (r) => json(r, { error: 'Помилка SQL: syntax error at end of input', sqlState: '42601', position: 11 }, 400))
  const offsets: string[] = []
  await page.route('**/api/admin/ops/db/tables/places/rows?*', (r) => {
    const offset = Number(new URL(r.request().url()).searchParams.get('offset'))
    offsets.push(String(offset))
    return json(r, { columns: ['place_id'], rows: Array.from({ length: 50 }, (_, i) => [String(offset + i + 1)]), truncated: offset < 100, elapsedMs: 1, offset, orderBy: ['place_id'] })
  })
  await page.goto(`${ADMIN}/#/db`)
  await page.getByLabel('SQL-запит').fill('SELECT 1 +')
  await page.getByRole('button', { name: 'Виконати SELECT' }).click()
  const alert = page.getByRole('alert')
  await expect(alert).toContainText('syntax error at end of input')
  await expect(alert).toContainText('Рядок 1, позиція 11')

  await page.getByRole('button', { name: 'Дані', exact: true }).click()
  await expect(page.getByText('рядки 1–50')).toBeVisible()
  await page.getByRole('button', { name: 'Наступні →' }).click()
  await expect(page.getByText('рядки 51–100')).toBeVisible()
  await expect(page.getByRole('cell', { name: '51', exact: true })).toBeVisible()
  expect(offsets).toEqual(['0', '50'])
})

test('A10 LLM history: "Показано N із M", older pages, failure filter', async ({ page }) => {
  await base(page)
  await page.route('**/api/admin/ops/llm?*', (r) => json(r, { from: '', to: '', calls: 4226, withFacts: 0, empty: 0, refusals: 0, failures: 260, inputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, outputTokens: 0, estimatedCostUsd: 0, timeline: [], recent: [] }))
  const req = (id: number, outcome: string) => ({ id, occurredAt: '2026-09-23T10:00:00Z', rawMessageId: id + 1000, sourceId: 86, sourceCode: 'tg_kpszsu', worker: 'ee', model: 'm', promptVersion: 'v', outcome, statusCode: outcome === 'success' ? 200 : 500, durationMs: 1, factsCount: 0 })
  const queries: URLSearchParams[] = []
  await page.route('**/api/admin/ops/llm/requests?*', (r) => {
    const q = new URL(r.request().url()).searchParams
    queries.push(q)
    if (q.get('outcome') === 'failures') return json(r, { requests: [req(7, 'invalid_response')], total: 260 })
    const before = Number(q.get('beforeId') ?? 5000)
    return json(r, { requests: Array.from({ length: 100 }, (_, i) => req(before - 1 - i, 'success')), total: 4226, nextBeforeId: before - 100 })
  })
  await page.goto(`${ADMIN}/#/llm`)
  await expect(page.getByText('показано 100 із 4 226')).toBeVisible()
  await page.getByRole('button', { name: 'Показати ще' }).click()
  await expect(page.getByText('показано 200 із 4 226')).toBeVisible()
  expect(queries.at(-1)?.get('beforeId')).toBe('4900')
  await page.getByLabel('Результат виклику').selectOption('failures')
  await expect(page.getByText('показано 1 із 260')).toBeVisible()
  await expect(page.getByTestId('llm-request')).toContainText('HTTP 500')
})

test('A12 changing the pipeline period marks the old numbers until the new ones arrive', async ({ page }) => {
  await base(page)
  const report = (received: number, hours: number) => ({
    from: new Date(Date.now() - hours * 3_600_000).toISOString(), to: new Date().toISOString(), bucket: 'hour', bucketStarts: [],
    totals: { received, processed: 0, skipped: 0, failed: 0, pending: 0, inProgress: 0, targets: 0, duplicates: 0, tracks: 0, errors: 0 },
    sources: [], timeline: [], instances: [], processingStatuses: {}, errorsByStage: {}, recentErrors: [],
  })
  await page.route('**/api/admin/ops/pipeline?*', async (r) => {
    const hours = Number(new URL(r.request().url()).searchParams.get('hours'))
    if (hours === 168) await delay(2_000)
    return json(r, report(hours === 24 ? 11_601 : 7_047_943, hours))
  })
  await page.goto(`${ADMIN}/#/pipeline`)
  await expect(page.getByText('11 601')).toBeVisible()
  await expect(page.getByText(/період 24 год · знімок/)).toBeVisible()
  await page.getByRole('button', { name: '7 д' }).click()
  await expect(page.getByText('Оновлюється… показано попередні дані')).toBeVisible()
  await expect(page.getByText('7 047 943')).toBeVisible({ timeout: 5_000 })
  await expect(page.getByText('Оновлюється… показано попередні дані')).toHaveCount(0)
  await expect(page.getByText(/період 7 д · знімок/)).toBeVisible()
})

test('A11 at 415 px the menu collapses and worker numbers are not cut', async ({ page }) => {
  await page.setViewportSize({ width: 415, height: 800 })
  await base(page)
  await page.route('**/api/admin/ops/workers', (r) => json(r, [{
    name: 'processor', kind: 'processor', alive: true, heartbeatAt: new Date().toISOString(), processed24h: 1_234_567, inProgress: 0,
    status: { instance: 'processor', host: 'h', roles: ['processor'], version: '1', builtAt: '2026-09-23T20:47:20Z', startedAt: '2026-09-23T20:47:20Z', at: new Date().toISOString(), pid: 1, workingSetBytes: 1, cpuPercent: 1, threads: 1,
      processing: { concurrency: 2, processed: 9_876_543, skipped: 12_345, failed: 6_789, retried: 0, retriedTransient: 0, perMinute1: 123.4, perMinute5: 234.5, parse: { samples: 1, meanMs: 1, p50Ms: 1, p90Ms: 1, maxMs: 1 }, lock: { samples: 1, meanMs: 1, p50Ms: 1, p90Ms: 1, maxMs: 1 }, store: { samples: 1, meanMs: 1, p50Ms: 1, p90Ms: 1, maxMs: 1 }, total: { samples: 1, meanMs: 1, p50Ms: 1, p90Ms: 1, maxMs: 1 }, claims: [] } },
  }]))
  await page.route('**/api/admin/ops/containers', (r) => json(r, { available: false, unavailable: 'Docker CLI недоступний', project: 'p', containers: [], processorReplicas: 0 }))
  await page.goto(`${ADMIN}/#/workers`)
  await expect(page.getByRole('navigation')).toBeHidden()
  await expect(page.getByLabel('Розділ')).toHaveValue('workers')
  const value = page.getByText('12 345 / 6 789')
  await expect(value).toBeVisible()
  const fits = await value.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)
  expect(fits).toBe(true)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
  await page.getByLabel('Розділ').selectOption('pipeline')
  await expect(page).toHaveURL(/#\/pipeline$/)
})
