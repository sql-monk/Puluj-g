import { expect, test, type Page } from '@playwright/test'

// Isolated real-component harness: deliberately ignores AbortSignal so late results
// exercise identity guards as well as cancellation. No live API is contacted.
async function mount(page: Page, hash = '#/entities') {
  page.on('pageerror', error => console.error(error.message))
  await page.route('**/api/**', (route) => new URL(route.request().url()).pathname.startsWith('/api/') ? route.abort() : route.continue())
  await page.route('**/catalogue-audit', (route) => route.fulfill({ contentType: 'text/html', body: `<html><div id="root"></div><script type="module">
    import RefreshRuntime from '/@react-refresh';
    RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => (type) => type; window.__vite_plugin_react_preamble_installed__ = true;
    await import('/src/entities/EntityCatalogue.audit.tsx');
  </script></html>` }))
  await page.goto(`/catalogue-audit${hash}`)
  await expect.poll(() => page.evaluate(() => (window as any).pending?.length ?? 0)).toBeGreaterThan(0)
}
const row = (id: string, name = `Record ${id}`) => ({ entity: 'explosion', table: 'ee_explosions', id, values: { name } })
async function settle(page: Page, index: number, value: unknown, error = false) {
  await page.evaluate(({ index, value, error }) => { const req = (window as any).pending[index]; error ? req.reject(new Error(String(value))) : req.resolve(value) }, { index, value, error })
}
async function count(page: Page, n: number) { await expect.poll(() => page.evaluate(() => (window as any).pending.length)).toBe(n) }
async function go(page: Page, hash: string) { await page.evaluate(hash => { location.hash = hash }, hash) }

test('late filter responses cannot replace current data; cached filters clear errors and preserve counts', async ({ page }) => {
  await mount(page, '#/entities?q=old')
  await go(page, '#/entities?q=new'); await count(page, 2)
  expect(await page.evaluate(() => (window as any).pending[0].signal.aborted)).toBe(true)
  await settle(page, 1, { items: [row('2')], totalCount: 42 })
  await expect(page.getByText('Показано 1 із 42')).toBeVisible()
  await settle(page, 0, { items: [row('1')], totalCount: 1 })
  await expect(page.getByText('Record 1', { exact: true })).toHaveCount(0)
  await go(page, '#/entities?q=broken'); await count(page, 3)
  await expect(page.getByText('Record 2', { exact: true })).toHaveCount(0)
  await settle(page, 2, 'offline', true)
  await expect(page.getByRole('alert')).toContainText('offline')
  await expect(page.getByText('Сутностей не знайдено.')).toHaveCount(0)
  await go(page, '#/entities?q=new')
  await expect(page.getByText('Показано 1 із 42')).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await count(page, 3) // inline callback rerenders must not loop or fetch again
})

test('pagination and refresh share one request slot, retain stale data, and retry the failed page', async ({ page }) => {
  await mount(page)
  await settle(page, 0, { items: [row('9007199254740993')], totalCount: 3, nextCursor: 'next' })
  await page.getByRole('button', { name: 'Показати ще' }).click(); await count(page, 2)
  await page.evaluate(() => (window as any).refresh())
  await expect(page.getByRole('button', { name: 'Оновити', exact: true })).toBeDisabled()
  await count(page, 2)
  await settle(page, 1, 'offline', true)
  await expect(page.getByText('Показано попередні дані; вони можуть бути застарілими.')).toBeVisible()
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click(); await count(page, 3)
  expect(await page.evaluate(() => (window as any).pending[2].key.cursor)).toBe('next')
  await settle(page, 2, { items: [row('9007199254740993'), row('9007199254740994'), row('9007199254740994')], totalCount: 3 })
  await expect(page.getByText('Показано 2 із 3')).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  const state = await page.evaluate(() => (window as any).states.at(-1))
  expect(state.loading).toBe(false); expect(state.error).toBeUndefined(); expect(state.lastSuccess).toBeTruthy()
})

test('detail identity changes discard old data and failures; successful retry recovers and related records link', async ({ page }) => {
  await mount(page, '#/entities/explosion/1?q=kept'); await count(page, 2)
  await settle(page, 0, row('1')); await settle(page, 1, [row('1')])
  await expect(page.getByRole('heading', { name: 'Пов’язані записи цього повідомлення' })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Record 1' })).toBeVisible()
  await go(page, '#/entities/explosion/2?q=kept'); await count(page, 4)
  await expect(page.getByRole('heading', { name: 'Record 1' })).toHaveCount(0)
  await settle(page, 2, 'failed detail', true)
  await expect(page.getByRole('alert')).toBeVisible()
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click(); await count(page, 6)
  await settle(page, 4, row('2')); await settle(page, 5, [row('2'), row('3')])
  await expect(page.getByRole('heading', { name: 'Record 2' })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.getByRole('link', { name: /explosion #2/ })).toHaveCount(0)
  await expect(page.getByRole('link', { name: /explosion #3/ })).toHaveAttribute('href', '#/entities/explosion/3?q=kept')
  await settle(page, 3, [row('wrong')])
  await expect(page.getByText(/#wrong/)).toHaveCount(0)
  await page.getByRole('link', { name: /explosion #3/ }).click(); await count(page, 8)
  await go(page, '#/entities/explosion/4'); await count(page, 10)
  await settle(page, 8, row('4')); await settle(page, 9, [])
  await settle(page, 6, row('3')); await settle(page, 7, [])
  await expect(page.getByRole('heading', { name: 'Record 4' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Record 3' })).toHaveCount(0)
})

test('mobile content clears the real TopBar, long titles wrap, and list scroll survives detail navigation', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await mount(page)
  await settle(page, 0, { items: Array.from({ length: 30 }, (_, i) => row(String(i), i === 0 ? 'Long'.repeat(100) : `Record ${i}`)), totalCount: 30 })
  const heading = await page.getByRole('heading', { name: 'Сутності Entity Extractor' }).boundingBox()
  const header = await page.locator('header').boundingBox()
  expect(heading!.y).toBeGreaterThanOrEqual(header!.y + header!.height)
  expect(await page.locator('main').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await page.locator('main').evaluate(el => { el.scrollTop = 600 })
  await expect.poll(() => page.locator('main').evaluate(el => el.scrollTop)).toBe(600)
  await go(page, '#/entities/explosion/5'); await count(page, 3)
  await settle(page, 1, row('5')); await settle(page, 2, [])
  await page.getByRole('link', { name: '← До каталогу' }).click()
  await expect.poll(() => page.locator('main').evaluate(el => el.scrollTop)).toBe(600)
  await count(page, 3)
})

test('loading, empty success and failed retry stay distinct and only success advances freshness', async ({ page }) => {
  await mount(page)
  await expect(page.getByRole('status')).toHaveText('Завантаження…')
  await expect(page.getByText('Сутностей не знайдено.')).toHaveCount(0)
  expect(await page.evaluate(() => (window as any).states.at(-1).lastSuccess)).toBeUndefined()
  await settle(page, 0, 'unavailable', true)
  await expect(page.getByRole('status')).toHaveCount(0)
  await expect(page.getByRole('alert')).toBeVisible()
  expect(await page.evaluate(() => (window as any).states.at(-1).lastSuccess)).toBeUndefined()
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click(); await count(page, 2)
  await settle(page, 1, { items: [], totalCount: 0 })
  await expect(page.getByText('Сутностей не знайдено.')).toBeVisible()
  await expect(page.getByText('Показано 0 із 0')).toBeVisible()
  const success = await page.evaluate(() => (window as any).states.at(-1).lastSuccess)
  expect(success).toBeTruthy()
  await page.evaluate(() => (window as any).refresh()); await count(page, 3)
  await settle(page, 2, 'unavailable again', true)
  await expect(page.getByRole('alert')).toBeVisible()
  expect(await page.evaluate(() => (window as any).states.at(-1).lastSuccess)).toBe(success)
})
