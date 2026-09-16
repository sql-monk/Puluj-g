import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { openMap } from './helpers'

/**
 * E06 (§8.6 mobile/keyboard/screen-reader): the DOM legend is the keyboard and screen-reader path to every incident on the
 * map (the canvas is not); it stays out of the way of the scale and attribution boxes; axe finds nothing serious.
 */
test.describe('E06 mobile / accessibility', () => {
  test('legend: collapsed by default, keyboard reachable, opens the popup, does not cover the map controls', async ({ page }, testInfo) => {
    await openMap(page)
    const legend = page.getByTestId('incident-legend')
    await expect(legend).toBeVisible()
    await expect(legend.locator('summary')).toHaveAttribute('aria-expanded', 'false')
    await expect(legend.locator('summary')).toHaveAttribute('aria-label', /Інциденти на мапі: 5, без локації: 1/)
    // Nothing interactive overlaps the scale control or the attribution box.
    const overlaps = async (selector: string) => {
      const a = await legend.boundingBox()
      const b = await page.locator(selector).first().boundingBox()
      if (!a || !b) return false
      return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
    }
    expect(await overlaps('.maplibregl-ctrl-scale')).toBe(false)
    expect(await overlaps('.maplibregl-ctrl-attrib')).toBe(false)

    // Keyboard: open the legend, focus the first incident button, Enter → dialog; Escape closes it.
    await legend.locator('summary').focus()
    await page.keyboard.press('Enter')
    await expect(legend.locator('summary')).toHaveAttribute('aria-expanded', 'true')
    expect(await overlaps('.maplibregl-ctrl-scale')).toBe(false) // expanded too
    expect(await overlaps('.maplibregl-ctrl-attrib')).toBe(false)
    const first = legend.getByRole('list', { name: 'Видимі інциденти' }).getByRole('button').first()
    await first.focus()
    await expect(first).toBeFocused()
    await page.keyboard.press('Enter')
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await expect(dialog).toHaveAttribute('aria-label', /.+/)
    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)
    // Unlocated incidents are listed, not drawn (§8.5); their row opens the popup (anchored at the map centre).
    const unlocated = legend.getByRole('list', { name: 'Інциденти без локації' })
    await expect(unlocated).toContainText('Пожежа · без локації')
    await unlocated.getByRole('button').first().click()
    await expect(page.getByRole('dialog')).toContainText('без локації')
    await page.keyboard.press('Escape')
    // Shape names sit next to the colour swatches: meaning is not colour alone.
    await expect(legend.getByRole('list', { name: 'Легенда видів подій' })).toContainText('зірка')
    if (testInfo.project.name === 'mobile') {
      const box = await legend.boundingBox()
      expect(box!.height).toBeLessThanOrEqual(page.viewportSize()!.height * 0.4 + 2)
    }
    await expect(legend).toHaveScreenshot(`legend-${testInfo.project.name}.png`, { maxDiffPixelRatio: 0.02 })
  })

  test('axe: no serious or critical violations with the legend open and a popup shown', async ({ page }) => {
    await openMap(page)
    const legend = page.getByTestId('incident-legend')
    await legend.locator('summary').click()
    await legend.getByRole('list', { name: 'Видимі інциденти' }).getByRole('button').first().click()
    await expect(page.getByRole('dialog')).toBeVisible()
    const results = await new AxeBuilder({ page }).include('[data-testid="incident-legend"]').include('[role="dialog"]').analyze()
    const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
    expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([])
  })
})
