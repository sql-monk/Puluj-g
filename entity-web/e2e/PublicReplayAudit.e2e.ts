import { expect, test, type Page } from '@playwright/test'

const from = '2026-01-15T09:00:00.000Z'
const to = '2026-01-15T10:00:00.000Z'
const end = '2026-01-15T09:59:59.999Z'

async function openReplay(page: Page, at = from) {
  // A real React/engine/store fixture isolates this audit from App/map/network changes.
  await page.route('**/replay-audit', (route) => route.fulfill({ contentType: 'text/html', body: `
    <html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div>
    <script type="module">
      import RefreshRuntime from '/@react-refresh';
      RefreshRuntime.injectIntoGlobalHook(window);
      window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => (type) => type;
      window.__vite_plugin_react_preamble_installed__ = true;
      await import('/src/components/ReplayBar.audit.tsx');
    </script></body></html>` }))
  await page.route(url => url.pathname.startsWith('/api/'), (route) => route.abort())
  await page.goto(`/replay-audit#${new URLSearchParams({ from, to, at })}`)
  await expect(page.getByRole('region', { name: 'Відтворення історії' })).toBeVisible()
}

async function routeAt(page: Page) {
  return page.evaluate(() => new URLSearchParams(location.hash.slice(1)).get('at'))
}

test.use({ timezoneId: 'America/Los_Angeles' })

test('half-open edges, slider keyboard, route restoration and Kyiv input', async ({ page }) => {
  await openReplay(page)
  const slider = page.getByRole('slider', { name: 'Час відтворення (Київ)' })
  const date = page.getByLabel('Кінець вікна (час Києва)')
  await expect(date).toHaveValue('2026-01-15T12:00')
  await page.getByRole('button', { name: 'В кінець', exact: true }).click()
  await expect.poll(() => routeAt(page)).toBe(end)
  await expect(page.getByLabel('Поточний знімок')).toHaveText(end)
  await expect(page.getByRole('button', { name: '▶ відтворити', exact: true })).toBeDisabled()
  await slider.focus()
  await page.keyboard.press('Home')
  await expect.poll(() => routeAt(page)).toBe(from)
  await page.keyboard.press('Shift+ArrowRight')
  await expect.poll(() => routeAt(page)).toBe('2026-01-15T09:10:00.000Z')
  await page.keyboard.press('End')
  await expect.poll(() => routeAt(page)).toBe(end)
  await page.getByRole('button', { name: 'Інша позиція URL' }).click()
  await expect(slider).toHaveValue('2')
  await expect(slider).toHaveAttribute('aria-valuetext', /11:02/)
  await date.fill('2026-01-15T14:00')
  await expect.poll(() => page.evaluate(() => new URLSearchParams(location.hash.slice(1)).get('to'))).toBe('2026-01-15T12:00:00.000Z')
})

test('button Space is native, filters own Escape, and pause/end persist the exact clock', async ({ page }) => {
  await openReplay(page)
  await page.clock.install()
  const play = page.getByRole('button', { name: '▶ відтворити', exact: true })
  await play.focus()
  await page.keyboard.press('Space')
  const pause = page.getByRole('button', { name: '⏸ пауза', exact: true })
  await expect(pause).toBeVisible()
  await page.clock.runFor(450)
  await pause.click()
  const paused = await routeAt(page)
  expect(Date.parse(paused!)).toBeGreaterThan(Date.parse(from))
  await expect(page.getByLabel('Поточний знімок')).toHaveText(paused!)
  await page.clock.runFor(1500)
  expect(await routeAt(page)).toBe(paused)
  await page.getByRole('button', { name: 'Фільтри тесту' }).click()
  await page.getByRole('button', { name: 'Поле фільтрів' }).focus()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('region', { name: 'Відтворення історії' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Поле фільтрів' })).toHaveCount(0)
  await page.getByRole('button', { name: 'В кінець', exact: true }).click()
  await page.getByRole('button', { name: 'Назад на 1 хвилину' }).click()
  await play.click()
  await page.clock.runFor(1000)
  await expect.poll(() => routeAt(page)).toBe(end)
  await expect(page.getByLabel('Поточний знімок')).toHaveText(end)
  await expect(play).toBeDisabled()
  await page.clock.runFor(2000)
  expect(await routeAt(page)).toBe(end)
})

test('transport stays within a 320px viewport', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 568 })
  await openReplay(page)
  const bar = page.getByRole('region', { name: 'Відтворення історії' })
  expect(await bar.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true)
  for (const control of await bar.locator('button, input, select').all()) {
    const box = await control.boundingBox()
    expect(box).not.toBeNull()
    expect(box!.x).toBeGreaterThanOrEqual(0)
    expect(box!.x + box!.width).toBeLessThanOrEqual(320)
  }
})
