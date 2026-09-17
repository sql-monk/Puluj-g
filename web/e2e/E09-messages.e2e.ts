import { expect, test } from '@playwright/test'
import { mockApi } from './fixtures/api'

test('public raw-message catalogue and a concrete revision detail remain safe and navigable', async ({ page }) => {
  await mockApi(page)
  const requests: string[] = []
  const row = {
    id: '9007199254740993', sourceId: 1, sourceCode: 'tg_test', publishedAt: '2026-09-16T12:00:00.000Z', receivedAt: '2026-09-16T12:01:00.000Z',
    sourceMessageKey: 'post-1', sourceRevision: 'r2', revisionGroup: '1:post-1', outcome: 'completed', outcomeSource: 'stage_result',
    url: 'https://t.me/tg_test/1', resultCount: 3, matchedResultCount: 1, locatedResultCount: 1, unlocatedResultCount: 2, excerpt: '<img src=x> Текст редакції', hasText: true,
  }
  await page.route('**/api/public/messages?**', (route) => {
    requests.push(new URL(route.request().url()).search)
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ from: '2026-09-16T00:00:00Z', to: '2026-09-17T00:00:00Z', dataset: 'live', consistency: 'best_effort_live', items: [row], refreshRecommended: false }) })
  })
  await page.route('**/api/public/messages/9007199254740993?**', (route) => route.fulfill({ contentType: 'application/json', body: JSON.stringify({ message: row, textState: 'available', text: '<b>Лише текст</b>', results: { items: [{ targetId: '9007199254740994', observationId: '9007199254740995', segmentIndex: 0, segmentText: 'фрагмент 1', catalogKind: 'impact.explosion.reported', at: row.publishedAt, confidence: 'high', sourceId: 1, mapAvailable: true, map: { locationKind: 'point', precision: 'point', geometry: { type: 'Point', coordinates: [30.5, 50.4] }, at: row.publishedAt }, relations: [] }, { targetId: '9007199254740996', segmentIndex: 1, segmentText: 'фрагмент 2', at: row.publishedAt, confidence: 'low', sourceId: 1, mapAvailable: false, map: { precision: 'region', unavailableReason: 'no_reported_location' }, relations: [] }, { targetId: '9007199254740997', segmentIndex: 2, segmentText: 'фрагмент 3', at: row.publishedAt, confidence: 'medium', sourceId: 1, mapAvailable: false, map: { precision: 'city', unavailableReason: 'no_reported_location' }, relations: [] }], totalCount: 3 }, revisions: { items: [{ id: row.id, publishedAt: row.publishedAt, sourceRevision: 'r2', isCurrent: true }, { id: '9007199254740992', publishedAt: '2026-09-16T11:00:00.000Z', sourceRevision: 'r1', isCurrent: false }], totalCount: 2 }, directRelations: [{ kind: 'alert', id: '9007199254740996', relation: 'start_message' }], links: {} }) }))
  await page.route('**/api/public/messages/9007199254740992?**', (route) => route.fulfill({ contentType: 'application/json', body: JSON.stringify({ message: { ...row, id: '9007199254740992', sourceRevision: 'r1' }, textState: 'structured_no_text', results: { items: [], totalCount: 0 }, revisions: { items: [], totalCount: 0 }, directRelations: [], links: {} }) }))

  await page.goto('/#/messages?outcome=completed&sourceIds=1')
  await expect(page.getByRole('heading', { name: 'Повідомлення' })).toBeVisible()
  await expect(page.getByText('<img src=x> Текст редакції')).toBeVisible()
  await expect(page.locator('img')).toHaveCount(0)
  // React development StrictMode may issue a canceled duplicate; every request must still carry the canonical URL filters.
  await expect.poll(() => requests.length).toBeGreaterThan(0)
  expect(requests[0]).toContain('outcome=completed')
  expect(requests[0]).toContain('sourceIds=1')

  await page.getByRole('link', { name: /tg_test/ }).click()
  await expect(page.getByRole('heading', { name: 'Повідомлення 9007199254740993' })).toBeVisible()
  await expect(page.getByText('<b>Лише текст</b>')).toBeVisible()
  await expect(page.locator('b')).toHaveCount(0)
  await expect(page.getByText('no_reported_location').first()).toBeVisible()
  await expect(page.getByText('точність: region')).toBeVisible()
  await page.getByRole('link', { name: 'Показати всі доступні (1)' }).click()
  await expect(page).toHaveURL(/#\/map\/history\?.*select=message%3A9007199254740993/)
  await expect(page.getByRole('status')).toContainText('Повідомлення 9007199254740993')
  await page.getByRole('button', { name: 'Повернутися' }).click()
  await expect(page.getByRole('heading', { name: 'Повідомлення 9007199254740993' })).toBeVisible()
  await expect(page.getByRole('link', { name: /alert: 9007199254740996/ })).toHaveAttribute('href', /#\/entities\/alert\/9007199254740996/)
  await page.getByRole('link', { name: /редакція r1/ }).click()
  await expect(page.getByRole('heading', { name: 'Повідомлення 9007199254740992' })).toBeVisible()
})

test('catalogue recovers from an error and follows its opaque next cursor without retaining prior pages', async ({ page }) => {
  await mockApi(page)
  let fail = true
  const requests: string[] = []
  const first = { id: '9007199254740993', sourceId: 1, sourceCode: 'tg_test', publishedAt: '2026-09-16T12:00:00Z', receivedAt: '2026-09-16T12:00:00Z', sourceMessageKey: 'one', sourceRevision: 'r1', revisionGroup: '1:one', outcome: 'pending', outcomeSource: 'legacy', resultCount: 0, matchedResultCount: 0, locatedResultCount: 0, unlocatedResultCount: 0, hasText: false }
  const second = { ...first, id: '9007199254740994', sourceMessageKey: 'two', outcome: 'no_facts', sourceRevision: 'r2' }
  await page.route('**/api/public/messages?**', (route) => {
    const url = new URL(route.request().url()); requests.push(url.search)
    if (fail) return route.fulfill({ status: 500, body: 'temporary' })
    const cursor = url.searchParams.get('cursor')
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ from: '', to: '', dataset: 'live', consistency: 'best_effort_live', items: cursor ? [second] : [first], nextCursor: cursor ? undefined : 'opaque-page-2', refreshRecommended: false }) })
  })
  await page.goto('/#/messages')
  await expect(page.getByRole('alert')).toContainText('Не вдалося завантажити каталог')
  fail = false
  await page.getByRole('button', { name: 'Повторити' }).click()
  await expect(page.getByText('очікує/обробляється')).toBeVisible()
  await page.getByRole('button', { name: 'Наступна сторінка' }).click()
  await expect(page.getByRole('link', { name: /tg_test/ }).filter({ hasText: 'результатів не знайдено' })).toBeVisible()
  expect(requests.some((query) => query.includes('cursor=opaque-page-2'))).toBe(true)
  await expect(page.getByText('очікує/обробляється')).toHaveCount(0)
})
