import { expect, test } from '@playwright/test'
import { bulkIncidents } from './fixtures/api'
import { openMap, sourceFeatures } from './helpers'
import { incidentIds } from './store'

/** E07 (§8.6 budget): 10 000 incidents over 20 pages reach the map within budget and cluster when zoomed out; a 1 000-push burst is one store update. */
test('10k incidents load in pages and cluster; a 1k push burst is one update', async ({ page }) => {
  test.setTimeout(120_000)
  const started = Date.now()
  await openMap(page, '#/map/live', { incidents: bulkIncidents(10_000) })
  const elapsed = Date.now() - started
  expect((await incidentIds(page)).length).toBe(10_000)
  expect(elapsed, `first data after ${elapsed} ms`).toBeLessThanOrEqual(10_000) // page load + 20 pages + first draw (plan said 5 s for the data alone; this includes the dev-server page load)
  const points = await sourceFeatures(page, 'incident-points')
  expect(points.length).toBe(10_000)
  // Zoomed out over the country, the icons are clustered (the cluster layer has rendered features); the source data itself stays per-incident.
  const clustered = await page.evaluate(() => {
    const map = (window as unknown as { __map: { queryRenderedFeatures(o: { layers: string[] }): { properties: Record<string, unknown> }[] } }).__map
    return map.queryRenderedFeatures({ layers: ['incident-clusters'] }).length
  })
  expect(clustered).toBeGreaterThan(0)

  // A burst of 1 000 revisions through the real push batcher → exactly one store update.
  const updates = await page.evaluate(async () => {
    const w = window as unknown as {
      __incidents: { subscribe(cb: () => void): () => void; getState(): { byId: Record<number, { revision: number }> } }
      __incidentsPush: { push(dto: unknown): void }
    }
    let n = 0
    const off = w.__incidents.subscribe(() => n++)
    const sample = w.__incidents.getState().byId[10_000] as unknown as Record<string, unknown>
    const t0 = performance.now()
    for (let i = 0; i < 1000; i++) w.__incidentsPush.push({ ...sample, id: 10_000 + (i % 100), revision: 2 + Math.floor(i / 100) })
    await new Promise((r) => setTimeout(r, 600))
    off()
    return { n, ms: performance.now() - t0, rev: w.__incidents.getState().byId[10_000].revision }
  })
  expect(updates.n).toBe(1)
  expect(updates.rev).toBe(11)
  expect(updates.ms).toBeLessThan(1500)
})
