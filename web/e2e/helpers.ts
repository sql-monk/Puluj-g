import { expect, type Page } from '@playwright/test'
import { mockApi, type MockOptions } from './fixtures/api'

/** Opens the map route with the mocked API and waits until the incident layer has data. */
export async function openMap(page: Page, hash = '#/map/live', opts: MockOptions = {}) {
  await mockApi(page, opts)
  await page.goto(`/${hash}`)
  await page.waitForFunction(() => Boolean((window as unknown as { __map?: unknown }).__map && (window as unknown as { __incidents?: unknown }).__incidents), null, { timeout: 30_000 })
  await page.waitForFunction(() => {
    const store = (window as unknown as { __incidents: { getState(): { byId: Record<number, unknown>; loading: boolean } } }).__incidents.getState()
    return !store.loading && Object.keys(store.byId).length > 0
  }, null, { timeout: 30_000 })
  await waitForLayer(page)
}

/** The incident source has received its data and the style is idle. */
export async function waitForLayer(page: Page) {
  await page.waitForFunction(
    () => {
      const map = (window as unknown as { __map: { getSource(id: string): unknown; isStyleLoaded(): boolean } }).__map
      return map.isStyleLoaded() && Boolean(map.getSource('incident-points'))
    },
    null,
    { timeout: 30_000 },
  )
}

export interface SourceFeature {
  id?: number | string
  properties: Record<string, unknown>
  geometry: { type: string; coordinates: unknown }
}

/** Reads a GeoJSON source's data straight from MapLibre (what the layer draws, independent of the viewport). */
export async function sourceFeatures(page: Page, sourceId: string): Promise<SourceFeature[]> {
  return page.evaluate((id) => {
    const map = (window as unknown as { __map: { getSource(id: string): { serialize(): { data?: { features?: unknown[] } | string } } | undefined } }).__map
    const src = map.getSource(id)
    if (!src) return []
    const data = src.serialize().data
    return ((typeof data === 'object' ? data?.features : undefined) ?? []) as never[]
  }, sourceId)
}

/** Screen position of a lon/lat for clicking. */
export async function project(page: Page, lon: number, lat: number): Promise<{ x: number; y: number }> {
  return page.evaluate(([lng, la]) => {
    const map = (window as unknown as { __map: { project(l: [number, number]): { x: number; y: number }; getContainer(): HTMLElement } }).__map
    const p = map.project([lng, la])
    const box = map.getContainer().getBoundingClientRect()
    return { x: box.left + p.x, y: box.top + p.y }
  }, [lon, lat] as [number, number])
}

/** Moves the map and waits until MapLibre is idle (symbols placed) — a click right after a jump would miss the glyph. */
export async function centerOn(page: Page, lon: number, lat: number, zoom: number) {
  await page.evaluate(
    ([lng, la, z]) =>
      new Promise<void>((resolve) => {
        const map = (window as unknown as { __map: { jumpTo(o: { center: [number, number]; zoom: number }): void; once(ev: string, cb: () => void): void; loaded(): boolean } }).__map
        map.once('idle', () => resolve())
        map.jumpTo({ center: [lng, la], zoom: z })
        setTimeout(resolve, 3000)
      }),
    [lon, lat, zoom] as [number, number, number],
  )
  await page.waitForTimeout(150)
}

export async function expectNoIncidentSelected(page: Page) {
  await expect(page.getByRole('dialog')).toHaveCount(0)
}

/** Clicks the incident glyph at lon/lat until its popup opens (symbol placement after a jump is asynchronous; a miss lands on the region). */
export async function clickIncident(page: Page, lon: number, lat: number, zoom: number) {
  for (let attempt = 0; attempt < 4; attempt++) {
    await centerOn(page, lon, lat, zoom)
    const at = await project(page, lon, lat)
    await page.mouse.move(at.x, at.y)
    await page.mouse.click(at.x, at.y)
    try {
      await expect(page.getByRole('dialog')).toBeVisible({ timeout: 1500 })
      return
    } catch {
      await page.keyboard.press('Escape')
      await page.waitForTimeout(300)
    }
  }
  throw new Error(`no incident popup after clicking ${lon},${lat}`)
}
