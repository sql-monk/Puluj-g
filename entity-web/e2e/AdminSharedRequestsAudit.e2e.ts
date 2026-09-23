import { expect, test, type Page } from '@playwright/test'

async function mount(page: Page, paged = false) {
  await page.route('**/shared-requests-audit*', route => route.fulfill({ contentType: 'text/html', body: `<html><div id="root"></div><script type="module">
    import RefreshRuntime from '/@react-refresh';
    RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => (type) => type; window.__vite_plugin_react_preamble_installed__ = true;
    await import('/src/admin/SharedRequests.audit.tsx');
  </script></html>` }))
  await page.goto(`/shared-requests-audit${paged ? '?paged' : ''}`)
  await count(page, 1)
}
async function count(page: Page, expected: number) {
  await expect.poll(() => page.evaluate(() => (window as any).pending?.length ?? 0)).toBe(expected)
}
async function settle(page: Page, index: number, value: unknown, reject = false) {
  await page.evaluate(({ index, value, reject }) => {
    const request = (window as any).pending[index]
    reject ? request.reject(new Error(String(value))) : request.resolve(value)
  }, { index, value, reject })
}
async function state(page: Page) { return JSON.parse((await page.getByLabel('state').textContent())!) }

test('automatic polling waits across two intervals while manual reload still supersedes', async ({ page }) => {
  await page.clock.install()
  await mount(page)
  await page.clock.fastForward(60_000)
  await page.clock.fastForward(60_001)
  await count(page, 1)
  await settle(page, 0, 'slow first response')
  await expect.poll(async () => (await state(page)).data).toBe('slow first response')
  await page.clock.fastForward(60_000)
  await count(page, 2)
  await page.getByRole('button', { name: 'Overlap' }).click(); await count(page, 4)
  await settle(page, 1, 'obsolete automatic')
  await settle(page, 2, 'obsolete manual')
  // Neither obsolete completion may unlock the still-pending latest manual request.
  await page.clock.fastForward(60_000)
  await page.clock.fastForward(60_001)
  await count(page, 4)
  expect((await state(page)).data).toBe('slow first response')
  await settle(page, 3, 'latest manual')
  await expect.poll(async () => (await state(page)).data).toBe('latest manual')
  await page.clock.fastForward(60_000)
  await count(page, 5)
})

test('poll drops obsolete A after A-B-A, including errors, and newest same-key request wins', async ({ page }) => {
  await mount(page)
  await page.getByRole('button', { name: 'Filter B' }).click(); await count(page, 2)
  await page.getByRole('button', { name: 'Filter A' }).click(); await count(page, 3)
  await settle(page, 2, 'new A')
  await expect.poll(async () => (await state(page)).data).toBe('new A')
  await settle(page, 0, 'obsolete A')
  await settle(page, 1, 'obsolete error', true)
  expect((await state(page)).data).toBe('new A')
  expect((await state(page)).error).toBeNull()
  await page.getByRole('button', { name: 'Overlap' }).click(); await count(page, 5)
  await settle(page, 4, 'latest')
  await settle(page, 3, 'older same-key')
  await expect.poll(async () => (await state(page)).data).toBe('latest')
  await page.getByRole('button', { name: 'Overlap' }).click(); await count(page, 7)
  await settle(page, 6, 'latest again')
  await settle(page, 5, 'obsolete same-key error', true)
  await expect.poll(async () => (await state(page)).data).toBe('latest again')
  expect((await state(page)).error).toBeNull()
})

test('filter changes clear poll errors; cleanup rejects pending results after unmount', async ({ page }) => {
  await mount(page)
  await settle(page, 0, 'failure', true)
  await expect.poll(async () => (await state(page)).error).toBe('failure')
  await page.getByRole('button', { name: 'Filter B' }).click(); await count(page, 2)
  expect((await state(page)).error).toBeNull()
  await page.getByRole('button', { name: 'Toggle mount' }).click()
  await settle(page, 1, 'late B')
  await expect(page.getByLabel('state')).toHaveCount(0)
  await page.getByRole('button', { name: 'Toggle mount' }).click(); await count(page, 3)
  expect((await state(page)).data).toBeNull()
  await settle(page, 2, 'fresh mount')
  await expect.poll(async () => (await state(page)).data).toBe('fresh mount')
})

test('pages drop obsolete A, lock duplicate more, and refresh invalidates pending pages', async ({ page }) => {
  await mount(page, true)
  await page.getByRole('button', { name: 'Filter B' }).click(); await count(page, 2)
  await page.getByRole('button', { name: 'Filter A' }).click(); await count(page, 3)
  await settle(page, 2, { items: ['new A'], total: 9, next: 'cursor' })
  await settle(page, 0, { items: ['obsolete A'], total: 1 })
  await settle(page, 1, 'obsolete error', true)
  await expect.poll(async () => (await state(page)).items).toEqual(['new A'])
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 4)
  expect(await page.evaluate(() => (window as any).pending[3].cursor)).toBe('cursor')
  await page.getByRole('button', { name: 'Refresh', exact: true }).click(); await count(page, 5)
  expect(await state(page)).toMatchObject({ items: null, error: null, busy: true, hasMore: false })
  expect((await state(page)).total).toBeUndefined()
  await settle(page, 3, { items: ['old page'], next: 'old-next', total: 99 })
  expect((await state(page)).busy).toBe(true)
  await settle(page, 4, { items: ['refreshed'], total: 1 })
  await expect.poll(async () => (await state(page)).items).toEqual(['refreshed'])
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 5)
})

test('filter changes reset page errors, totals and next cursor while retry keeps current page', async ({ page }) => {
  await mount(page, true)
  await settle(page, 0, { items: ['A'], total: 12, next: 'A-next' })
  await expect.poll(async () => (await state(page)).hasMore).toBe(true)
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 2)
  await settle(page, 1, 'page failure', true)
  await expect.poll(async () => (await state(page)).error).toBe('page failure')
  await page.getByRole('button', { name: 'Filter B' }).click(); await count(page, 3)
  expect(await state(page)).toMatchObject({ items: null, error: null, hasMore: false, busy: true })
  expect((await state(page)).total).toBeUndefined()
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 3)
  await settle(page, 2, { items: ['B'], next: 'B-next', total: 2 })
  await expect.poll(async () => (await state(page)).hasMore).toBe(true)
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 4)
  await settle(page, 3, 'retry me', true)
  await expect.poll(async () => (await state(page)).busy).toBe(false)
  await page.getByRole('button', { name: 'More twice' }).click(); await count(page, 5)
  expect(await page.evaluate(() => (window as any).pending[4].cursor)).toBe('B-next')
  await settle(page, 4, { items: ['B2'], total: 2 })
  await expect.poll(async () => (await state(page)).items).toEqual(['B', 'B2'])
  await page.getByRole('button', { name: 'Refresh', exact: true }).click(); await count(page, 6)
  await page.getByRole('button', { name: 'Toggle mount' }).click()
  await settle(page, 5, { items: ['unmounted result'], total: 99, next: 'obsolete' })
  await expect(page.getByLabel('state')).toHaveCount(0)
  await page.getByRole('button', { name: 'Toggle mount' }).click(); await count(page, 7)
  expect(await state(page)).toMatchObject({ items: null, hasMore: false, error: null, busy: true })
  expect((await state(page)).total).toBeUndefined()
})
