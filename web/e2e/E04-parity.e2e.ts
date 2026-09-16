import { expect, test } from '@playwright/test'
import { openMap, sourceFeatures } from './helpers'

/** E04 (§8.6): the same incident layer on the country map and on the Kyiv map; the Kyiv district incident draws its polygon on both. */
test('the Kyiv map shows the same incidents with the same properties as the country map', async ({ page }) => {
  await openMap(page, '#/map/live')
  const mainPoints = await sourceFeatures(page, 'incident-points')
  const mainAreas = await sourceFeatures(page, 'incident-areas')
  const mainEvents = await sourceFeatures(page, 'events')
  expect(mainEvents.map((f) => f.properties.id)).toEqual([1003]) // the legacy explosion marker is hidden while the incident layer is on; the cancellation stays

  const countryMap = await page.evaluateHandle(() => (window as unknown as { __map: unknown }).__map)
  await page.goto('/#/map/live?preset=kyiv')
  // A same-document hash navigation: wait for the Kyiv map to replace the country map on the hook and to hold the data.
  await page.waitForFunction(
    (old) => {
      const w = window as unknown as { __map?: { getSource(id: string): { serialize(): { data?: { features?: unknown[] } } } | undefined; isStyleLoaded(): boolean } }
      const src = w.__map?.getSource('incident-points')
      return Boolean(w.__map && w.__map !== old && w.__map.isStyleLoaded() && src && (src.serialize().data?.features?.length ?? 0) > 0)
    },
    countryMap,
    { timeout: 30_000 },
  )
  const kyivPoints = await sourceFeatures(page, 'incident-points')
  const kyivAreas = await sourceFeatures(page, 'incident-areas')
  const strip = (f: { properties: Record<string, unknown> }) => ({ ...f.properties, opacity: undefined })
  expect(kyivPoints.map(strip)).toEqual(mainPoints.map(strip))
  expect(kyivAreas.map((f) => f.geometry)).toEqual(mainAreas.map((f) => f.geometry))
  const pechersk = kyivAreas.find((f) => f.properties.id === 7)!
  expect((pechersk.geometry.coordinates as number[][][])[0][0]).toEqual([30.52, 50.4]) // the district polygon from the regions payload, not a circle
})
