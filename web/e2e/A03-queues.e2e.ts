import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * A03/A04 (§9, §8.7, P13): the queues panel and the message explorer over a mocked /api/admin/* — alarms are visible with their
 * severity as words, a stuck worker is marked, every control needs actor + reason and its confirmation names the exact scope
 * ("history lane of parser"), a queue with in-flight work is never called empty; the lifecycle card shows a delivery error in full
 * and renders source text as text.
 */
const ADMIN = 'http://localhost:5184'

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const lane = (subscription: string, lane: string, over: Partial<Record<string, unknown>> = {}) => ({
  subscription, lane, required: true, registryStatus: 'active', laneState: { state: 'active' },
  pending: 0, inFlight: 0, retryHour: 0, adminRetryHour: 0, quarantined: 0, oldestPendingAgeSeconds: null, eventTimeLagSeconds: null, oldestRunningAttemptAgeSeconds: null,
  waitP50Ms: 12, waitP95Ms: 40, waitP99Ms: 90, processingP50Ms: 5, processingP95Ms: 20, processingP99Ms: 50,
  expectedHour: 120, completedHour: 118, noopHour: 2, failedHour: 0, expected5m: 10, completed5m: 10, consumers: 1, ready: null, unacked: null, brokerConsumers: null, source: 'db',
  ...over,
})

const snapshot = {
  at: '2026-09-16T12:00:00Z',
  topologyVersion: 8,
  subscriptions: [
    lane('raw-writer', 'live'),
    lane('parser', 'live', { pending: 1, inFlight: 1, oldestRunningAttemptAgeSeconds: 900, oldestPendingAgeSeconds: 900, completed5m: 0, expected5m: 0 }),
    lane('parser', 'history', { pending: 1234, oldestPendingAgeSeconds: 7200, laneState: { state: 'paused', actor: 'ops', reason: 'backfill window', changedAt: '2026-09-16T11:00:00Z' }, consumers: 0 }),
    lane('normalizer', 'live', { quarantined: 2 }),
  ],
  roots: { receivedHour: 300, completedHour: 290, pending: 8, needsAttention: 2 },
  broker: { connected: true, connectedWorkers: ['messaging-1'], disconnectedWorkers: [], management: null },
  outbox: { unconfirmed: 0, oldestAgeSeconds: null, relayRetries: 0, unroutable: 0, confirmP50Ms: 3, confirmP95Ms: 9, publishedHour: 500 },
  inbox: { rowsHour: 480, supersededHour: 3, processing: 1 },
  reconciliation: { at: '2026-09-16T11:59:30Z', worker: 'messaging-1', outboxUnconfirmed: 0, outboxOldestAgeSeconds: 0, overdueCount: 0, unknownSubscriptions: [], quarantineOpen: 2, declareFailed: [], outboxDeleted: 0, inboxDeleted: 0 },
  workers: [
    { name: 'messaging-1', heartbeatAt: '2026-09-16T11:59:50Z', statusAt: '2026-09-16T11:59:55Z', stale: false, stuck: false, runningAttempts: 1, completedHour: 400, lastSuccessAt: '2026-09-16T11:59:00Z', lastErrorAt: null, lastError: null, roles: ['relay', 'archive', 'parser'], broker: { connected: true, endpoint: 'rabbitmq:5672/' }, llm: null,
      consumers: [{ subscription: 'parser', lane: 'live', queue: 'puluj.parser.live', state: 'active', consuming: true, inFlight: 1, prefetch: 10, consumerTag: 'parser@messaging-1:live', delivered: 100, duplicates: 0, requeued: 1 }, { subscription: 'parser', lane: 'history', queue: 'puluj.parser.history', state: 'paused', consuming: false, inFlight: 0, prefetch: 10, consumerTag: 'parser@messaging-1:history', delivered: 0, duplicates: 0, requeued: 0 }] },
    { name: 'ghost', heartbeatAt: '2026-09-16T11:50:00Z', statusAt: null, stale: true, stuck: true, runningAttempts: 3, completedHour: 0, lastSuccessAt: null, lastErrorAt: '2026-09-16T11:49:00Z', lastError: 'Npgsql.NpgsqlException: connection reset', roles: [], broker: null, llm: null, consumers: [] },
  ],
  backfill: [{ source: 'tg_kpszsu', status: '12345', checkpoint: '{"offset":12345}', lastSuccessAt: '2026-09-16T11:59:00Z', consecutiveFailures: 0, lastError: null }],
  alarms: [
    { code: 'stale_heartbeat_with_jobs', severity: 'error', scope: 'worker:ghost', message: 'heartbeat застарів (10 хв), але 3 attempts ще running' },
    { code: 'inflight_stuck', severity: 'error', scope: 'parser/live', message: '1 in-flight, найстаріший running attempt 15 хв, завершень за 5 хв немає — черга не порожня' },
    { code: 'dlq', severity: 'warn', scope: 'normalizer/live', message: '2 у карантині (DLQ) — потрібне рішення оператора: retry або waive' },
    { code: 'lane_paused', severity: 'info', scope: 'parser/history', message: 'history lane підписки parser: paused (ops: backfill window); pending 1234' },
  ],
  slo: { oldestAgeSeconds: { live: 300, history: 3600, replay: 3600 }, outboxUnconfirmedSeconds: 60, outboxCriticalSeconds: 300, staleHeartbeatSeconds: 90, inflightStuckSeconds: 300, requiredConsumerMissingSeconds: 60 },
}

const lifecycle = {
  rawMessageId: 777, sourceId: 1, sourceCode: 'tg_kpszsu', sourceMessageId: '4242', publishedAt: '2026-09-16T11:00:00Z', receivedAt: '2026-09-16T11:00:05Z', status: 'processed',
  text: '<b>Шахеди</b> на Сумщині <script>alert(1)</script>', url: 'javascript:alert(1)',
  events: [
    { eventId: 'e1', eventType: 'raw.stored', lane: 'live', occurredAt: '2026-09-16T11:00:05Z', publishedAt: '2026-09-16T11:00:05Z', confirmedAt: '2026-09-16T11:00:05Z', causationId: null, producer: 'raw-writer@w1',
      deliveries: [
        { subscription: 'archive', lane: 'live', expectedAt: '2026-09-16T11:00:05Z', outcome: 'completed', completedAt: '2026-09-16T11:00:06Z', reason: null, actor: null, attempts: [{ attemptId: 1, worker: 'archive@w1', state: 'succeeded', startedAt: '2026-09-16T11:00:05Z', finishedAt: '2026-09-16T11:00:06Z', error: null, retryOfAttemptId: null, retryReason: null }], attemptsTruncated: false },
        { subscription: 'normalizer', lane: 'live', expectedAt: '2026-09-16T11:00:05Z', outcome: 'quarantined', completedAt: '2026-09-16T11:00:09Z', reason: 'attempts_exhausted', actor: null,
          attempts: [{ attemptId: 2, worker: 'normalizer@w1', state: 'failed', startedAt: '2026-09-16T11:00:06Z', finishedAt: '2026-09-16T11:00:07Z', error: 'System.NullReferenceException: Object reference not set to an instance of an object.\n   at Puluj.Processing.Stages.NormalizerHandler.ApplyAsync(...) line 42', retryOfAttemptId: null, retryReason: null }], attemptsTruncated: false },
        { subscription: 'parser', lane: 'live', expectedAt: '2026-09-16T11:00:05Z', outcome: null, completedAt: null, reason: null, actor: null, attempts: [], attemptsTruncated: false },
      ] },
  ],
  eventsTruncated: false,
  extractions: [],
  observations: [],
  derived: [{ kind: 'track', id: 9, label: 'track #9 (target #55)' }],
  quarantine: [{ quarantineId: 5, subscriptionId: 'normalizer', lane: 'live', reason: 'attempts_exhausted', error: 'System.NullReferenceException: Object reference not set to an instance of an object.', quarantinedAt: '2026-09-16T11:00:09Z', resolvedAt: null, resolution: null, envelopePreview: '{"event_id":"e1"}', envelopeTruncated: false }],
  summary: { completion: 'needs_attention', waiting: ['parser/live'], completed: ['archive/live'], failed: ['normalizer/live'] },
}

async function mockAdmin(page: Page, posts: { url: string; body: unknown }[]) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('**/api/admin/settings', (r) => json(r, []))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (r) => json(r, []))
  await page.route('**/api/admin/ops/messaging**', (r) => {
    if (r.request().method() === 'POST') {
      posts.push({ url: new URL(r.request().url()).pathname, body: r.request().postDataJSON() })
      return json(r, { ok: true, scope: 'parser/history', state: 'active' })
    }
    const path = new URL(r.request().url()).pathname
    if (path.endsWith('/quarantine')) return json(r, [{ quarantineId: 5, subscriptionId: 'normalizer', eventId: 'e1', lane: 'live', reason: 'attempts_exhausted', error: 'NullReferenceException: boom', quarantinedAt: '2026-09-16T11:00:09Z', resolvedAt: null, resolvedBy: null, resolution: null, eventType: 'raw.stored', rawMessageId: 777 }])
    if (path.endsWith('/audit')) return json(r, [{ auditId: 1, action: 'pause', subscriptionId: 'parser', lane: 'history', actor: 'ops', reason: 'backfill window', at: '2026-09-16T11:00:00Z' }])
    return json(r, snapshot)
  })
  await page.route('**/api/admin/messages?**', (r) => json(r, [{ rawMessageId: 777, sourceId: 1, sourceCode: 'tg_kpszsu', sourceMessageId: '4242', publishedAt: '2026-09-16T11:00:00Z', receivedAt: '2026-09-16T11:00:05Z', status: 'processed', extractions: 1, observations: 2, lastOutcome: 'quarantined', textPreview: 'Шахеди на Сумщині' }]))
  await page.route('**/api/admin/messages/777/lifecycle', (r) => json(r, lifecycle))
}

test.describe('admin ops', () => {
  test('A03 queues: alarms with severity words, stuck worker marked, scope in the confirmation, controls disabled without actor/reason, in-flight never "empty"', async ({ page }) => {
    const posts: { url: string; body: unknown }[] = []
    await mockAdmin(page, posts)
    const dialogs: string[] = []
    page.on('dialog', (d) => {
      dialogs.push(d.message())
      void d.accept()
    })
    await page.goto(`${ADMIN}/#/queues`)
    const alarms = page.getByTestId('alarms')
    await expect(alarms).toContainText('ПОМИЛКА')
    await expect(alarms.locator('[data-code="stale_heartbeat_with_jobs"]')).toContainText('worker:ghost')
    await expect(alarms.locator('[data-code="lane_paused"]')).toContainText('ІНФО')
    await expect(alarms.locator('[data-code="lane_paused"]')).toContainText('ops: backfill window')
    // The stuck worker is marked with a word, and its last error is shown in full.
    const ghost = page.getByTestId('worker-ghost')
    await expect(ghost).toContainText('ЗАВИС')
    await expect(ghost).toContainText('Npgsql.NpgsqlException: connection reset')
    await expect(page.getByTestId('worker-messaging-1')).toContainText('parser/history: пауза')
    // A queue with in-flight work is never shown as empty; a paused lane says who and why.
    const parserLive = page.getByTestId('lane-parser-live')
    await expect(parserLive).not.toContainText('порожня')
    await expect(parserLive).toContainText('15 хв')
    await expect(page.getByTestId('lane-raw-writer-live')).toContainText('порожня')
    const history = page.getByTestId('lane-parser-history')
    await expect(history).toContainText('пауза (ops: backfill window)')
    await expect(history).toHaveAttribute('data-state', 'paused')
    // Controls are disabled until actor + reason are given.
    const resume = history.getByRole('button', { name: 'відновити' })
    await expect(resume).toBeDisabled()
    await page.getByLabel('actor').fill('ops')
    await page.getByLabel('reason').fill('backfill done')
    await expect(resume).toBeEnabled()
    await resume.click()
    expect(dialogs).toHaveLength(1)
    expect(dialogs[0]).toContain('lane «history» підписки «parser»')
    expect(dialogs[0]).toContain('Інші lanes цієї підписки та інші підписки не змінюються')
    expect(dialogs[0]).toMatch(/pending 1[\s  ]234/) // uk-UA grouping uses a narrow no-break space
    await expect(page.getByRole('status')).toContainText('відновити parser/history: виконано')
    expect(posts).toEqual([{ url: '/api/admin/ops/messaging/lanes/parser/history', body: { state: 'active', actor: 'ops', reason: 'backfill done' } }])
    // Pausing the live lane of the parser names that lane, not the subscription.
    await parserLive.getByRole('button', { name: 'пауза' }).click()
    expect(dialogs[1]).toContain('Призупинити: lane «live» підписки «parser», pending 1, in-flight 1')
    expect(posts[1]).toEqual({ url: '/api/admin/ops/messaging/lanes/parser/live', body: { state: 'paused', actor: 'ops', reason: 'backfill done' } })
    // Quarantine: retry sends actor/reason for exactly one row.
    await page.getByRole('button', { name: 'показати' }).first().click()
    const q = page.getByTestId('quarantine-table')
    await expect(q).toContainText('NullReferenceException: boom')
    await q.getByRole('button', { name: 'retry' }).click()
    expect(dialogs[2]).toContain('лише ця доставка')
    expect(posts[2]).toEqual({ url: '/api/admin/ops/messaging/quarantine/5/retry', body: { actor: 'ops', reason: 'backfill done' } })
  })

  test('A04 message explorer: search → card, delivery error in full, source text as text, no javascript: link, who waits/completed/failed', async ({ page }) => {
    await mockAdmin(page, [])
    await page.goto(`${ADMIN}/#/messages`)
    await page.getByLabel('запит').fill('Шахеди')
    await page.getByRole('button', { name: 'Шукати' }).click()
    const results = page.getByTestId('search-results')
    await expect(results).toContainText('tg_kpszsu')
    await expect(results).toContainText('КАРАНТИН')
    await results.getByRole('button', { name: '#777' }).click()
    const card = page.getByTestId('lifecycle-card')
    await expect(card).toBeVisible()
    await expect(page.locator('section', { hasText: 'Повідомлення #777' })).toContainText('ПОТРЕБУЄ УВАГИ')
    // Source text is text: markup literal, no script, no javascript: link.
    await expect(page.getByTestId('raw-text')).toContainText('<b>Шахеди</b> на Сумщині <script>alert(1)</script>')
    expect(await card.locator('script').count()).toBe(0)
    expect(await card.getByRole('link', { name: 'оригінал' }).count()).toBe(0)
    // The failed delivery shows its error in full, the summary names every branch.
    const stored = page.getByTestId('event-raw.stored')
    await expect(stored).toContainText('normalizer/live')
    await expect(stored).toContainText('КАРАНТИН')
    await expect(stored).toContainText('at Puluj.Processing.Stages.NormalizerHandler.ApplyAsync(...) line 42')
    await expect(stored.locator('[data-outcome="pending"]')).toContainText('parser/live')
    const summary = page.getByTestId('summary')
    await expect(summary).toContainText('Чекають (1)')
    await expect(summary).toContainText('parser/live')
    await expect(summary).toContainText('Помилилися (1)')
    await expect(summary).toContainText('normalizer/live')
    await expect(page.getByTestId('quarantine')).toContainText('#5 · normalizer/live · attempts_exhausted')
    await expect(card).toContainText('track #9 (target #55)')
    // Deep link from the queues panel opens the card directly.
    await page.goto(`${ADMIN}/#/messages?raw=777`)
    await expect(page.getByTestId('lifecycle-card')).toBeVisible()
  })
})
