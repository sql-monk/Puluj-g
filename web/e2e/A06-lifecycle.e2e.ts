import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * A06 (§10, P15): the «Аналітика повідомлень» page over a mocked /api/admin/analytics/lifecycle* — every funnel step names its denominator,
 * no-text and failed messages are visible as their own numbers, unknown timings/completion say «unavailable» (never a number), the
 * reconciliation status shows raw vs projected counts, precision/recall is «unavailable» rather than a fake value, and backfill/reconcile
 * need actor + reason.
 */
const ADMIN = 'http://localhost:5184'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const report = {
  from: '2026-09-15T12:00:00Z', to: '2026-09-16T12:00:00Z', hours: 24, bucket: 'hour',
  funnel: { raw: 1200, posts: 1100, stored: 1200, analyzed: 1150, withFacts: 640, domainCompleted: 600, visible: 410, stuckAnalysis: 3, stuckDomain: 0, unavailableTimings: 250, unavailableCompletion: 250, storedToAnalyzedP50Seconds: 4.2, storedToAnalyzedP95Seconds: 18, analyzedToDomainP50Seconds: 1.1, analyzedToDomainP95Seconds: 9 },
  timeline: [{ at: '2026-09-16T10:00:00Z', raw: 50, analyzed: 48, withFacts: 20, domainCompleted: 19, failed: 2, noText: 5 }, { at: '2026-09-16T11:00:00Z', raw: 60, analyzed: 60, withFacts: 30, domainCompleted: 30, failed: 0, noText: 7 }],
  sources: [{ sourceId: 1, code: 'tg_kpszsu', raw: 700, posts: 650, edits: 50, noText: 12, withPayload: 12, facts: 400, textLengthP50: 180, collectDelayP50Seconds: 25, collectDelayP95Seconds: 90, live: 690, history: 10, maxGapSeconds: 5400 }, { sourceId: 2, code: 'alerts_in_ua', raw: 500, posts: 500, edits: 0, noText: 500, withPayload: 500, facts: 500, textLengthP50: null, collectDelayP50Seconds: 3, collectDelayP95Seconds: 8, live: 500, history: 0, maxGapSeconds: 600 }],
  parse: { outcomes: { completed: 640, no_facts: 480, unsupported: 12, failed: 18, pending: 50 }, methods: { rules: 1100, llm: 50 }, multiFact: 90, unlocated: 35, totalFacts: 900, ruleVersions: { v3: 1150 }, modelVersions: { 'claude-sonnet-5': 50 } },
  quality: { reviewOutcomes: { merged: 4, resolved: 9 }, precisionSamples: null, precisionRecall: 'unavailable: потрібна розмічена вибірка (P16)', rulesVsLlm: 'unavailable: shadow-порівняння rules vs LLM (P16)' },
  cost: { calls: 52, roots: 50, costUsd: 0.42, inputTokens: 90000, cacheTokens: 60000, outputTokens: 8000, cacheShare: 0.4, byModel: [{ model: 'claude-sonnet-5', calls: 52, inputTokens: 90000, cacheTokens: 60000, outputTokens: 8000, costUsd: 0.42, latencyP50Ms: 1800, latencyP95Ms: 4200, failures: 2, late: 1 }], bySource: [{ key: 'tg_kpszsu', value: 0.42 }] },
  results: { eventKinds: { 'impact.explosion.reported': 300, 'uav.reported': 340 }, incidentPrecision: { city: 200, region: 150, unknown: 60 }, incidents: 410, tracks: 120, alerts: 500, incidentsWithProvenance: 410, activeIncidents: 410 },
  history: { runs: [{ runId: '0199a1b2-c3d4-7e5f-8a6b-1c2d3e4f5a6b', kind: 'live', pipelineVersion: '1.3.0+abc', rows: 950, outcomes: { completed: 640, no_facts: 300, failed: 10 } }, { runId: '18f077f6-6906-56e9-b3ae-fd9141ab4292', kind: 'legacy', pipelineVersion: null, rows: 250, outcomes: { legacy: 240, failed: 8, unsupported: 2 } }], activeGenerationIncidents: 410, incidentsByGeneration: [{ key: '6bb9f959-42f5-5e7c-a384-a325e71fa89b (active)', value: 410 }] },
  reconciliation: null,
}
const status = { available: true, backfill: { cursor: 99000, maxRawMessageId: 99000, caughtUp: true, at: '2026-09-16T11:59:00Z' }, reconciliation: { at: '2026-09-16T11:59:30Z', windowHours: 48, rawRows: 2300, posts: 2100, edits: 200, projectedRaw: 2300, projectedPosts: 2100, missingRoots: 0, lateAnalyses: 3, lateCompletions: 12, pendingAnalysis: 1, pendingDomain: 4, unavailableTimings: 250, unavailableCompletion: 250 } }

async function mockAdmin(page: Page, posts: { url: string; body: unknown }[]) {
  await page.route((u) => u.pathname.startsWith('/api/'), (r) => r.fulfill({ status: 404, body: '' }))
  await page.route('**/api/admin/settings', (r) => json(r, []))
  await page.route('**/api/admin/status', (r) => json(r, { alertsConfigured: false, telegramConfigured: false, llmConfigured: false, adminTokenSet: false, workerAlive: true }))
  await page.route('**/api/admin/sources', (r) => json(r, []))
  await page.route('**/api/admin/analytics/lifecycle**', (r) => {
    const path = new URL(r.request().url()).pathname
    if (r.request().method() === 'POST') {
      posts.push({ url: path + new URL(r.request().url()).search, body: r.request().postDataJSON() })
      return json(r, path.endsWith('/reconcile') ? status.reconciliation : { cursor: 99000, maxRawMessageId: 99000, processed: 0 })
    }
    return json(r, path.endsWith('/status') ? status : report)
  })
}

test.describe('admin lifecycle analytics', () => {
  test('A06 lifecycle page: denominators named, no-text/failed visible, unavailable as a word, counts reconciliation, backfill/reconcile gated', async ({ page }) => {
    const posts: { url: string; body: unknown }[] = []
    await mockAdmin(page, posts)
    await page.goto(`${ADMIN}/#/lifecycle`)
    const funnel = page.getByTestId('funnel')
    await expect(funnel).toContainText(/1[\s  ]200/)
    await expect(funnel).toContainText(/постів 1[\s  ]100/) // roots vs posts: edits are not extra roots
    await expect(funnel).toContainText(/% з 1[\s  ]150 розібраних/) // the denominator is named next to the share
    await expect(funnel).toContainText('unavailable: 250 з')
    await expect(page.getByTestId('transitions')).toContainText('timings unavailable для 250 повідомлень')
    // No-text and failed are their own numbers, not hidden in "other".
    await expect(page.getByTestId('source-alerts_in_ua')).toContainText('500')
    await expect(page.getByTestId('outcomes')).toContainText('ПОМИЛКА')
    await expect(page.getByTestId('outcomes')).toContainText('18')
    await expect(page.getByTestId('outcomes')).toContainText('ще без розбору')
    // Quality never fakes a number.
    await expect(page.getByTestId('quality')).toContainText('unavailable: потрібна розмічена вибірка')
    // Counts reconciliation: raw vs projected, posts vs projected posts, late results.
    const st = page.getByTestId('lifecycle-status')
    await expect(st).toContainText('лічильники збігаються')
    await expect(st).toContainText(/raw 2[\s  ]300 \(пости 2[\s  ]100, редакції 200\) vs проєкція 2[\s  ]300/)
    await expect(st).toContainText('15 дописано')
    await expect(st).toContainText('наздогнав')
    // Legacy run: pipeline version unavailable, outcome legacy.
    await expect(page.getByTestId('history')).toContainText('unavailable')
    await expect(page.getByTestId('history')).toContainText('legacy 240')
    await expect(page.getByTestId('cost')).toContainText('claude-sonnet-5')
    // Commands need actor + reason.
    const backfill = page.getByRole('button', { name: 'Продовжити backfill' })
    await expect(backfill).toBeDisabled()
    await page.getByLabel('actor').fill('ops')
    await page.getByLabel('reason').fill('after deploy')
    await expect(backfill).toBeEnabled()
    await page.getByRole('button', { name: 'Звірити лічильники' }).click()
    await expect(page.getByRole('status')).toContainText('звірка: виконано (ops)')
    expect(posts).toEqual([{ url: '/api/admin/analytics/lifecycle/reconcile?hours=48', body: { actor: 'ops', reason: 'after deploy' } }])
    // The copy analytics stays reachable as the auxiliary section.
    await expect(page.getByRole('link', { name: /Схожість повідомлень/ })).toHaveAttribute('href', '#/analytics')
  })
})
