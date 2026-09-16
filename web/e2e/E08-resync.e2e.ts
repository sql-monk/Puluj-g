import { expect, test } from '@playwright/test'
import { openMap } from './helpers'
import { incidentsCall } from './store'

/** E08 (ADR-0011): the server's Resync (or our own reconnect) reloads the window from the API — the recovery path never depends on replayed packets. */
test('resync reloads the window once; two overlapping reloads leave one consistent set', async ({ page }) => {
  const requests: string[] = []
  await openMap(page, '#/map/live', { requests })
  const before = requests.length
  await incidentsCall(page, 'resync', 0)
  await expect.poll(() => requests.length).toBe(before + 1)
  expect(requests[requests.length - 1]).toContain('/api/incidents?from=')
  // Two reloads back to back: both hit the API; the newer one is the truth (sequence guard) and `loading` clears.
  await Promise.all([incidentsCall(page, 'reloadWindow'), incidentsCall(page, 'reloadWindow')])
  await expect.poll(() => requests.length).toBe(before + 3)
  const state = await page.evaluate(() => {
    const s = (window as unknown as { __incidents: { getState(): { byId: Record<string, unknown>; loading: boolean } } }).__incidents.getState()
    return { ids: Object.keys(s.byId).length, loading: s.loading }
  })
  expect(state).toEqual({ ids: 7, loading: false })
})
