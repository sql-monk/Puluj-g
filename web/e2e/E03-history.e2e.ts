import { expect, test } from '@playwright/test'
import { openMap } from './helpers'
import { storeCall } from './store'

/** E03 (§8.5 history mode): history asks the recorded-mode window at `asOf`; replay ticks are throttled; live asks effective. */
test('history mode is recorded&asOf, throttled under replay ticks, effective again in live', async ({ page }) => {
  const requests: string[] = []
  await openMap(page, '#/map/live', { requests })
  const liveRequests = requests.length
  expect(requests[0]).toContain('/api/incidents?from=')
  expect(requests[0]).not.toContain('mode=recorded')

  const at = new Date(Date.now() - 60 * 60_000)
  await storeCall(page, 'setMode', 'history', at.toISOString())
  await expect.poll(() => requests.filter((u) => u.includes('mode=recorded')).length).toBe(1)
  const recorded = requests.find((u) => u.includes('mode=recorded'))!
  expect(recorded).toContain(`asOf=${encodeURIComponent(at.toISOString())}`)
  expect(recorded).toContain(`to=${encodeURIComponent(at.toISOString())}`)

  // Replay ticks: ten `at` moves fired inside one page turn (no re-render between them, as the replay engine does every second);
  // the store's trailing throttle (3 s) turns them into exactly two recorded reloads — the leading one and one trailing run.
  const HISTORY_RELOAD_MS = 3000
  await page.waitForTimeout(HISTORY_RELOAD_MS + 200) // the throttle window of the mode change above has passed
  const before = requests.filter((u) => u.includes('mode=recorded')).length
  await page.evaluate(([base]) => {
    const store = (window as unknown as { __store: { getState(): { setMode(m: string, at: Date): void } } }).__store.getState()
    for (let i = 1; i <= 10; i++) store.setMode('history', new Date(base + i * 1000))
  }, [at.getTime()] as [number])
  await page.waitForTimeout(HISTORY_RELOAD_MS + 700)
  const during = requests.filter((u) => u.includes('mode=recorded')).length - before
  expect(during).toBe(2)
  // The last reload carries the last tick's `at`.
  const last = requests.filter((u) => u.includes('mode=recorded')).pop()!
  expect(last).toContain(encodeURIComponent(new Date(at.getTime() + 10_000).toISOString()))

  await storeCall(page, 'setMode', 'live')
  await expect.poll(() => requests.length).toBeGreaterThan(liveRequests + 3)
  expect(requests[requests.length - 1]).not.toContain('mode=recorded')
})
