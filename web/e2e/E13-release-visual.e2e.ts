import { expect, test } from '@playwright/test'
import { mockApi } from './fixtures/api'
import { waitForLayer } from './helpers'

/**
 * U13 keeps an inspectable visual record for the three release viewports.  The API is
 * deterministic so a map tile, a late response, or a remote font cannot make a layout
 * comparison non-reproducible.  These attachments are UI-contract evidence; the release
 * runner records their boundary separately from the provider-backed PostGIS tests.
 */
test('public map has no horizontal overflow at release viewports', async ({ page }, testInfo) => {
  await mockApi(page)

  for (const [width, height] of [[360, 800], [768, 1024], [1440, 900]] as const) {
    await page.setViewportSize({ width, height })
    await page.goto('/#/map/live')
    await page.waitForFunction(() => Boolean((window as unknown as { __map?: unknown }).__map))
    await waitForLayer(page)
    await expect(page.getByTestId('incident-legend')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)

    const path = testInfo.outputPath(`public-map-${width}x${height}.png`)
    await page.screenshot({ path })
    await testInfo.attach(`public-map-${width}x${height}`, { path, contentType: 'image/png' })
  }
})
