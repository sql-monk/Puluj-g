import { expect, test } from '@playwright/test'
import { mockApi } from './fixtures/api'

const from = '2026-09-16T00:00:00.000Z'
const to = '2026-09-17T00:00:00.000Z'
const period = { from, to, bucket: 'hour', bucketStarts: [from] }

/** U12: the public analytics shell uses the shared filter panel and only links a source to its matching raw messages. */
test('analytics exposes applied filters and a source drill-down with the same UTC population', async ({ page }) => {
  await mockApi(page)
  const requests: string[] = []
  await page.route('**/api/stats/sources?**', (route) => {
    requests.push(route.request().url())
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ period, filters: { applied: ['sourceIds=1'], unavailable: [], timeBasis: 'PublishedAt UTC', population: 'збережені ревізії повідомлень' }, messages: 2, processed: 2, withTargets: 1, targets: 1, sources: [{ id: 1, code: 'tg_test', name: 'Тестовий канал', messages: 2, processed: 2, withTargets: 1, targets: 1, series: [2] }] }) })
  })
  await page.route('**/api/public/messages?**', (route) => route.fulfill({ contentType: 'application/json', body: JSON.stringify({ from, to, dataset: 'live', consistency: 'best_effort_live', items: [], refreshRecommended: false }) }))

  await page.goto('/#/analytics?metric=sources&sourceIds=1')
  await expect(page.getByRole('heading', { name: 'Аналітика' })).toBeVisible()
  await expect(page.getByText('sourceIds=1')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Тестовий канал' })).toHaveAttribute('href', /#\/messages\?sourceIds=1.*from=2026-09-16T00%3A00%3A00.000Z/)
  expect(requests.some((url) => url.includes('sourceIds=1'))).toBe(true)

  await page.getByRole('button', { name: 'Фільтри та період' }).click()
  await expect(page.getByText('Дані', { exact: true })).toBeVisible()
  // On a phone the bottom sheet intentionally covers the source row; its
  // mobile assertion is that filters remain reachable. Desktop also follows
  // the link end-to-end after the sheet is closed.
  if (await page.evaluate(() => window.innerWidth < 700)) return
  await page.getByRole('button', { name: 'Згорнути панель' }).click()
  await page.getByRole('link', { name: 'Тестовий канал' }).click()
  await expect(page).toHaveURL(/#\/messages\?sourceIds=1.*from=2026-09-16T00%3A00%3A00.000Z/)
  await expect(page.getByRole('heading', { name: 'Повідомлення' })).toBeVisible()
})
