import { expect, test } from '@playwright/test'
import { openMap, sourceFeatures, waitForLayer } from './helpers'
import { storeCall } from './store'

/** E05 (§8.6): legend and filters come from the catalog; catalog visibility and the viewer's filter are two settings; the layer switch restores the legacy markers. */
test('legend from the catalog, per-kind filter, catalog visibility, layer switch', async ({ page }) => {
  await openMap(page)
  const legend = page.locator('details', { hasText: 'Інциденти' })
  await legend.locator('summary').click()
  const items = legend.getByRole('list', { name: 'Легенда видів подій' }).getByRole('listitem')
  // Catalog order (sortOrder 30, 40, 41, 50); the catalog-hidden kind (outage, mapVisible=false) is not even offered.
  await expect(items).toHaveText([/Робота ППО/, /Повідомлення про вибух/, /Підтверджене влучання/, /Пожежа/])
  await expect(items.nth(1)).toContainText('зірка') // meaning by shape, not colour alone
  await expect(items.nth(1)).toContainText('3') // 3 explosions on the map (ids 1, 2, 7)

  // The viewer switches a kind off: the layer drops it, the catalog is untouched.
  await legend.getByLabel('Показувати: Повідомлення про вибух').uncheck()
  await expect.poll(async () => (await sourceFeatures(page, 'incident-points')).map((f) => f.properties.id).sort()).toEqual([3, 4])
  await legend.getByLabel('Показувати: Повідомлення про вибух').check()
  await expect.poll(async () => (await sourceFeatures(page, 'incident-points')).length).toBe(5)

  // The layer switch: nothing drawn, the legend gone, the legacy explosion marker back on the map.
  await storeCall(page, 'setFilter', 'events', false)
  await expect.poll(async () => (await sourceFeatures(page, 'incident-points')).length).toBe(0)
  await expect(legend).toHaveCount(0)
  await storeCall(page, 'setFilter', 'events', true)
  await expect.poll(async () => (await sourceFeatures(page, 'incident-points')).length).toBe(5)
  await expect.poll(async () => (await sourceFeatures(page, 'events')).map((f) => f.properties.id)).toEqual([1003]) // one switch for both layers; the explosion stays hidden behind its incident
})

test('the Map navigation item returns directly to the live map', async ({ page }) => {
  await openMap(page)
  await page.getByRole('link', { name: 'Цілі і події' }).click()
  await expect(page).toHaveURL(/#\/entities$/)

  await page.getByRole('link', { name: 'Мапа', exact: true }).click()
  await expect(page).toHaveURL(/#\/map\/live$/)
  await waitForLayer(page)
})
