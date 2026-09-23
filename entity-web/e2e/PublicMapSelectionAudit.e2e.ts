import { expect, test, type Page } from '@playwright/test'

async function openFixture(page: Page) {
  await page.route('**/map-selection-audit', route => route.fulfill({ contentType: 'text/html', body: `<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">
    import RefreshRuntime from '/@react-refresh';
    RefreshRuntime.injectIntoGlobalHook(window);
    window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => (type) => type;
    window.__vite_plugin_react_preamble_installed__ = true;
    await import('/src/map/MapSelection.audit.tsx');
  </script></body></html>` }))
  await page.goto('/map-selection-audit')
}

test('selected details follow refreshed values and clear permanently when removed or filtered out', async ({ page }) => {
  await openFixture(page)
  const selected = page.getByLabel('Selected status')
  await page.getByRole('button', { name: 'Select', exact: true }).click()
  await expect(selected).toHaveText('active')
  await page.getByRole('button', { name: 'Update', exact: true }).click()
  await expect(selected).toHaveText('closed')
  await page.getByRole('button', { name: 'Toggle filter', exact: true }).click()
  await expect(selected).toHaveCount(0)
  await page.getByRole('button', { name: 'Toggle filter', exact: true }).click()
  await expect(selected).toHaveCount(0)
  await page.getByRole('button', { name: 'Select', exact: true }).click()
  await page.getByRole('button', { name: 'Remove', exact: true }).click()
  await expect(selected).toHaveCount(0)
  await page.getByRole('button', { name: 'Restore', exact: true }).click()
  await expect(selected).toHaveCount(0)
  await page.getByRole('button', { name: 'Select', exact: true }).click()
  await page.getByRole('button', { name: 'Other kind', exact: true }).click()
  await expect(selected).toHaveCount(0)
})

test('region note makes no alert-status claim or legacy API request and fits mobile', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 568 })
  const apiRequests: string[] = []
  page.on('request', request => { if (request.url().includes('/api/')) apiRequests.push(request.url()) })
  await openFixture(page)
  const note = page.getByRole('complementary', { name: 'Вибраний регіон' })
  await expect(note).toContainText('не індикатор стану тривоги')
  await expect(note).not.toContainText('Тривоги немає')
  const box = await note.boundingBox()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(320)
  await page.getByRole('button', { name: 'Закрити вибраний регіон' }).click()
  await expect(note).toHaveCount(0)
  expect(apiRequests).toEqual([])
})
