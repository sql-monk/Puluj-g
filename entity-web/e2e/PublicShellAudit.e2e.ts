import { expect, test, type Page, type Route } from '@playwright/test'
import type { EntityDefinition, EntityItem } from '../src/api/entityExtractor'

const now = new Date('2026-09-24T12:00:00Z')
const definitions: EntityDefinition[] = [{ entityName: 'explosion', tableName: 'ee_explosions', enabled: true, fields: [], map: { visible: true, renderer: 'point', labelField: 'name' } }]
const item = (id: string, name: string, occurredAt = '2026-09-24T11:58:00Z', located = true): EntityItem => ({
  entity: 'explosion', table: 'ee_explosions', id, sourceId: 1, occurredAt, values: { name },
  ...(located ? { geometry: { type: 'Point' as const, coordinates: [30.52, 50.45] } } : {}),
})
const items = [item('1', 'Київ Alpha'), item('2', 'Львів Beta'), item('3', 'Київ без координат', undefined, false), item('4', 'Старий запис', '2026-09-23T10:00:00Z')]
const snapshot = (rows = items, at?: string) => ({ generatedAt: now.toISOString(), at, items: rows, truncated: false, limitPerEntity: 1000 })
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, json: body })

async function mockApp(page: Page, onSnapshot?: (route: Route) => Promise<void>) {
  await page.clock.setFixedTime(now)
  await page.addInitScript(() => localStorage.setItem('puluj.ee.pollSeconds', '120'))
  const unexpected: string[] = []
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  // Empty style has no remote tiles, sprites or fonts; map overlays still use real MapLibre.
  await page.route('https://tiles.openfreemap.org/**', route => json(route, { version: 8, sources: {}, layers: [] }))
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url())
    switch (url.pathname) {
      case '/api/map/config': return json(route, { lifetimeOptionsMinutes: [15, 30, 60, 120], maxLifetimeMinutes: 120, feedHours: 24 })
      case '/api/places/regions': return json(route, [])
      case '/api/sources': return json(route, [{ id: 1, code: 'audit', name: 'Audit source', type: 'Telegram', trustLevel: 1 }])
      case '/api/taxonomy': return json(route, { categories: [] })
      case '/api/event-kinds': return json(route, [])
      case '/api/ee/definitions': return json(route, definitions)
      case '/api/ee/snapshot': return onSnapshot ? onSnapshot(route) : json(route, snapshot())
      case '/api/ee/entities': return json(route, { items: items.slice(0, 3), totalCount: 3 })
      case '/api/stats/targets': return json(route, {
        period: { from: url.searchParams.get('from'), to: url.searchParams.get('to'), bucket: 'hour', bucketStarts: [] },
        filters: { applied: [], unavailable: [], timeBasis: 'occurredAt', population: 'audit' },
        targets: 0, tracks: 0, objectsDeclared: 0, unlocated: 0, categories: [], targetsByBucket: [], tracksByBucket: [], byClass: [], byRegion: [], routes: [], hourWeekday: Array.from({ length: 7 }, () => Array(24).fill(0)),
      })
      default: unexpected.push(url.pathname); return json(route, { error: 'Unmocked audit endpoint' }, 501)
    }
  })
  return { unexpected, errors }
}

const toggle = (page: Page) => page.getByRole('button', { name: 'Панель фільтрів і налаштувань' })
const panel = (page: Page) => page.locator('[data-section-panel="open"]')
async function closePanel(page: Page) { await panel(page).getByRole('button', { name: 'Згорнути панель' }).click() }
const historyHash = '#/map/history?from=2026-09-24T11:00:00.000Z&to=2026-09-24T12:00:00.000Z&at=2026-09-24T11:30:00.000Z'

test('EE success is online; filter count and feed share the actual sample and search', async ({ page }) => {
  const audit = await mockApp(page)
  await page.goto('/#/map/live')
  await expect(page.getByTitle('дані отримано', { exact: true })).toBeVisible()
  await expect(page.locator('header')).not.toContainText(/offline|офлайн/i)
  await toggle(page).click()
  await expect(panel(page)).toContainText('3 записів після фільтрів')
  await panel(page).getByRole('textbox', { name: 'Пошук', exact: true }).fill('Київ')
  await expect(panel(page)).toContainText('2 записів після фільтрів')
  await expect(page).toHaveURL(/q=/)
  await closePanel(page)
  await page.getByRole('button', { name: 'Записи карти (2)', exact: true }).click()
  const feed = page.getByRole('complementary', { name: 'Записи карти', exact: true })
  await expect(feed.getByRole('listitem')).toHaveCount(2)
  await expect(feed.getByRole('link', { name: /Київ Alpha/ })).toHaveAttribute('href', /#\/entities\/explosion\/1\?q=/)
  await expect(feed).toContainText('Київ без координат')
  await expect(feed).not.toContainText('Львів Beta')
  await page.getByRole('button', { name: 'Згорнути записи карти' }).click()
  await toggle(page).click()
  await panel(page).getByRole('textbox', { name: 'Пошук', exact: true }).fill('немає-збігів')
  await expect(panel(page)).toContainText('0 записів після фільтрів')
  await closePanel(page)
  await page.getByRole('button', { name: 'Записи карти (0)', exact: true }).click()
  await expect(feed).toContainText('За цими фільтрами записів немає.')
  expect(audit.unexpected).toEqual([])
  expect(audit.errors).toEqual([])
})

test('map exposes EE kinds without unsupported taxonomy or forecast controls', async ({ page }) => {
  const audit = await mockApp(page)
  await page.goto('/#/map/live')
  await toggle(page).click()
  await expect(panel(page).getByRole('group', { name: 'Тип сутності', exact: true }).getByRole('checkbox')).toHaveCount(1)
  for (const name of ['Вид події', 'Категорія події', 'Категорія', 'Клас', 'Сімейство', 'Модель', 'Область']) {
    await expect(panel(page).getByLabel(name, { exact: true })).toHaveCount(0)
  }
  await expect(panel(page).getByRole('checkbox', { name: /прогноз|forecast/i })).toHaveCount(0)
  await expect(panel(page)).not.toContainText(/прогноз|forecast/i)
  expect(audit.unexpected).toEqual([])
})

test('failed EE refresh keeps the previous feed and retry restores success', async ({ page }) => {
  let fail = false
  let requests = 0
  await mockApp(page, route => { requests++; return fail ? json(route, { error: 'unavailable' }, 503) : json(route, snapshot()) })
  await page.goto('/#/map/live')
  await expect(page.getByRole('button', { name: 'Записи карти (3)' })).toBeVisible()
  fail = true
  await page.getByRole('button', { name: 'Оновити', exact: true }).click()
  await expect(page.getByRole('alert', { name: 'Оновлення даних' })).toContainText('Не вдалося оновити дані')
  await expect(page.getByTitle('помилка оновлення', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Записи карти (3)' }).click()
  await expect(page.getByRole('complementary', { name: 'Записи карти' }).getByRole('listitem')).toHaveCount(3)
  await expect(page.getByRole('complementary', { name: 'Записи карти' })).toContainText('Показано попередній знімок')
  fail = false
  const beforeRetry = requests
  await page.getByRole('button', { name: 'Оновити', exact: true }).click()
  await expect(page.getByTitle('дані отримано', { exact: true })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(requests).toBeGreaterThan(beforeRetry)
})

for (const [hash, heading] of [['#/analytics', 'Аналітика'], ['#/entities', 'Цілі та події']]) {
  test(`415px ${hash}: heading clears header and backdrop closes the panel`, async ({ page }) => {
    await page.setViewportSize({ width: 415, height: 900 })
    const audit = await mockApp(page)
    await page.goto(`/${hash}`)
    const title = page.getByRole('heading', { name: heading, exact: true })
    await expect(title).toBeVisible()
    const titleBox = await title.boundingBox()
    const headerBox = await page.locator('header').filter({ has: page.getByRole('navigation', { name: 'Основна навігація' }) }).boundingBox()
    expect(titleBox!.y).toBeGreaterThanOrEqual(headerBox!.y + headerBox!.height)
    await toggle(page).click()
    await expect(panel(page)).toBeVisible()
    const drawerBox = await panel(page).boundingBox()
    // Click an exposed part of the real backdrop, without force or DOM dispatch.
    await page.getByRole('button', { name: 'Закрити панель', exact: true }).click({ position: { x: 10, y: (headerBox!.height + drawerBox!.y) / 2 } })
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false')
    await expect(panel(page)).toHaveCount(0)
    await expect(toggle(page)).toBeFocused()
    expect(audit.errors).toEqual([])
    expect(audit.unexpected).toEqual([])
  })
}

test('playback completes slow snapshots and labels the actual frame while its clock advances', async ({ page }) => {
  const completed: string[] = []
  const requested: string[] = []
  const audit = await mockApp(page, async route => {
    const at = new URL(route.request().url()).searchParams.get('at')
    if (!at) return json(route, snapshot([]))
    requested.push(at)
    await new Promise(resolve => setTimeout(resolve, 1500))
    await json(route, snapshot([item('frame', `Frame ${at}`, at)], at))
    completed.push(at)
  })
  await page.goto(`/${historyHash}`)
  await page.getByRole('button', { name: /відтворити/i }).click()
  const frame = page.locator('[aria-label="Оновлення даних"]').getByText(/^Кадр /)
  await expect(frame).toBeVisible()
  const firstFrame = await frame.textContent()
  await expect.poll(() => completed.length).toBeGreaterThanOrEqual(2)
  await expect(frame).not.toHaveText(firstFrame!)
  await page.getByRole('button', { name: /пауза/i }).click()
  await expect(page.getByRole('button', { name: 'Оновити', exact: true })).toBeEnabled()
  const last = completed.at(-1)!
  const label = await page.evaluate(at => `Кадр ${new Date(at).toLocaleString('uk-UA', { timeZone: 'Europe/Kyiv' })}`, last)
  await expect(frame).toHaveText(label)
  expect(new Set(requested).size).toBeGreaterThan(1)
  await page.getByRole('button', { name: 'Записи карти (1)', exact: true }).click()
  await expect(page.getByRole('complementary', { name: 'Записи карти' })).toContainText(`Frame ${last}`)
  expect(audit.errors).toEqual([])
  expect(audit.unexpected).toEqual([])
})

test('leaving history cancels its pending snapshot and late data cannot replace the next live feed', async ({ page }) => {
  let pending: Route | undefined
  const audit = await mockApp(page, async route => {
    if (new URL(route.request().url()).searchParams.has('at')) {
      pending = route // Deliberately hold the history response until after navigation.
      return
    }
    await json(route, snapshot([item('live', 'Поточний live запис')]))
  })
  await page.goto(`/${historyHash}`)
  await expect.poll(() => Boolean(pending)).toBe(true)
  const cancelled = page.waitForEvent('requestfailed', request => request === pending!.request())
  await page.getByRole('link', { name: 'Цілі і події', exact: true }).click()
  await cancelled
  await expect(page.getByRole('heading', { name: 'Цілі та події' })).toBeVisible()
  // Playwright may discard fulfillment of an already-aborted browser request.
  await json(pending!, snapshot([item('obsolete', 'Застарілий історичний кадр')], '2026-09-24T11:30:00.000Z'))
  await page.evaluate(() => { window.location.hash = '#/map/live' })
  await expect(page).toHaveURL(/#\/map\/live/)
  await expect(page.getByTitle('дані отримано', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Записи карти (1)', exact: true }).click()
  const feed = page.getByRole('complementary', { name: 'Записи карти' })
  await expect(feed).toContainText('Поточний live запис')
  await expect(feed).not.toContainText('Застарілий історичний кадр')
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(audit.errors).toEqual([])
  expect(audit.unexpected).toEqual([])
})

test('Online menu immediately cancels a held history request and loads the live feed directly', async ({ page }) => {
  let pending: Route | undefined
  let liveRequests = 0
  let switchToLive = false
  const audit = await mockApp(page, async route => {
    if (new URL(route.request().url()).searchParams.has('at')) {
      pending = route
      return // Never release history until cancellation and live rendering have been verified.
    }
    liveRequests++
    await json(route, snapshot(switchToLive ? [item('online', 'Новий онлайн запис')] : []))
  })
  await page.goto(`/${historyHash}`)
  await expect.poll(() => Boolean(pending)).toBe(true)
  const held = pending!
  const beforeSwitch = liveRequests
  await page.getByRole('button', { name: 'Обрати режим мапи' }).click()
  switchToLive = true
  const [cancelled] = await Promise.all([
    page.waitForEvent('requestfailed', { predicate: request => request === held.request(), timeout: 5000 }),
    page.getByRole('menuitem', { name: 'Онлайн', exact: true }).click(),
  ])
  expect(cancelled.failure()?.errorText).toMatch(/abort|cancel/i)
  await expect(page).toHaveURL(/#\/map\/live(?:\?|$)/)
  await expect(page.getByRole('region', { name: 'Відтворення історії' })).toHaveCount(0)
  await expect.poll(() => liveRequests).toBeGreaterThan(beforeSwitch)
  await expect(page.getByTitle('дані отримано', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Записи карти (1)', exact: true }).click()
  const feed = page.getByRole('complementary', { name: 'Записи карти' })
  await expect(feed).toContainText('Новий онлайн запис')
  await json(held, snapshot([item('obsolete', 'Старий затриманий кадр')], '2026-09-24T11:30:00.000Z'))
  await expect(feed.getByRole('listitem')).toHaveCount(1)
  await expect(feed).toContainText('Новий онлайн запис')
  await expect(feed).not.toContainText('Старий затриманий кадр')
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(audit.errors).toEqual([])
  expect(audit.unexpected).toEqual([])
})

test('415px expanded history feed stays above replay controls after window remounts and resize', async ({ page }) => {
  await page.setViewportSize({ width: 415, height: 900 })
  const audit = await mockApp(page, route => {
    const at = new URL(route.request().url()).searchParams.get('at')
    return json(route, snapshot(at ? [item('history', 'Історичний запис', '2026-09-24T11:29:00Z')] : [], at ?? undefined))
  })
  await page.goto(`/${historyHash}`)
  await page.getByRole('button', { name: 'Записи карти (1)', exact: true }).click()
  const feed = page.getByRole('complementary', { name: 'Записи карти' })
  const replay = page.getByRole('region', { name: 'Відтворення історії' })
  await expect(feed).toContainText('Історичний запис')
  await expect(replay).toBeVisible()
  const expectFeedGeometry = async (stage: string) => {
    // Allow ResizeObserver to publish the measured transport height; inspect real layout.
    await expect.poll(async () => {
      const feedBox = await feed.boundingBox()
      const replayBox = await replay.boundingBox()
      return !!feedBox && !!replayBox && feedBox.height > 0 && feedBox.y + feedBox.height <= replayBox.y
    }, { message: `Expanded feed must end above replay controls: ${stage}` }).toBe(true)
    // A stale observer can leave a safe-looking but incorrect gap. The measured
    // replay height must preserve the intended 12px clearance after each remount.
    await expect.poll(async () => {
      const feedBox = await feed.boundingBox()
      const replayBox = await replay.boundingBox()
      return feedBox && replayBox ? replayBox.y - (feedBox.y + feedBox.height) : -1
    }, { message: `Feed must follow the current replay bar height: ${stage}` }).toBeCloseTo(12, 0)
    await expect(feed.getByRole('link', { name: /Історичний запис/ })).toBeInViewport()
  }
  await expectFeedGeometry('initial window')

  const originalBar = await replay.elementHandle()
  await replay.getByRole('button', { name: '3 год', exact: true }).click()
  await expect(page).toHaveURL(url => new URLSearchParams(url.hash.split('?')[1]).get('from') === '2026-09-24T09:00:00.000Z')
  await expect.poll(() => originalBar!.evaluate(element => element.isConnected)).toBe(false)
  await originalBar!.dispose()
  await expectFeedGeometry('3-hour preset remount')

  // Also change the end through the real input: both window boundaries change,
  // and the historical-end "зараз" control can alter the bar's wrapped height.
  const presetBar = await replay.elementHandle()
  await replay.getByLabel('Кінець вікна (час Києва)').fill('2026-09-24T14:45')
  await expect(page).toHaveURL(url => {
    const query = new URLSearchParams(url.hash.split('?')[1])
    return query.get('from') === '2026-09-24T08:45:00.000Z' && query.get('to') === '2026-09-24T11:45:00.000Z'
  })
  await expect.poll(() => presetBar!.evaluate(element => element.isConnected)).toBe(false)
  await presetBar!.dispose()
  await expect(replay.getByRole('button', { name: 'зараз', exact: true })).toBeVisible()
  await expectFeedGeometry('end-time remount')

  for (const height of [740, 900]) {
    await page.setViewportSize({ width: 415, height })
    await expectFeedGeometry(`remounted window at 415×${height}`)
    await replay.getByRole('button', { name: 'Вперед на 1 хвилину', exact: true }).click()
    await expect(page).toHaveURL(/#\/map\/history/)
  }
  expect(audit.errors).toEqual([])
  expect(audit.unexpected).toEqual([])
})

test('filter panel keeps checkbox selection, URL history, retired kinds and separate reset semantics', async ({ page }) => {
  const audit = await mockApp(page)
  await page.goto('/#/map/live?entityKinds=track&sourceIds=999')
  await toggle(page).click()
  await expect(panel(page)).toContainText('Треки вимкнено')
  await expect(panel(page)).toContainText('0 записів після фільтрів')
  await panel(page).getByRole('checkbox', { name: 'Треки вимкнено' }).click()
  await expect(page).not.toHaveURL(/entityKinds=/)
  await expect(panel(page).getByRole('checkbox', { name: 'Треки вимкнено' })).toHaveCount(0)
  await panel(page).locator('summary').filter({ hasText: /^Джерела/ }).click()
  await expect(panel(page).getByRole('checkbox', { name: 'Недоступне джерело #999' })).toBeChecked()
  const auditSource = panel(page).getByRole('checkbox', { name: 'Audit source', exact: true })
  await expect(auditSource).not.toBeChecked()
  // URL-controlled inputs settle after hashchange, beyond check()'s immediate state check.
  await auditSource.click()
  await expect(auditSource).toBeChecked()
  await expect(page).toHaveURL(/sourceIds=1%2C999/)
  await page.goBack()
  await expect(page).toHaveURL(/sourceIds=999(?:&|$)/)
  await expect(auditSource).not.toBeChecked()
  await panel(page).locator('summary').filter({ hasText: /^Відображення/ }).click()
  await panel(page).getByRole('checkbox', { name: 'Події', exact: true }).uncheck()
  await panel(page).getByRole('button', { name: 'Скинути фільтри даних', exact: true }).click()
  await expect(page).not.toHaveURL(/sourceIds=/)
  await expect(panel(page).getByRole('checkbox', { name: 'Події', exact: true })).not.toBeChecked()
  await panel(page).getByRole('button', { name: 'Скинути відображення', exact: true }).click()
  await expect(panel(page).getByRole('checkbox', { name: 'Події', exact: true })).toBeChecked()
  await expect(panel(page)).toContainText('3 записів після фільтрів')
  expect(audit.errors).toEqual([])
})

test('320px panel keyboard loop skips collapsed controls and Escape restores focus', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 740 })
  const audit = await mockApp(page)
  await page.goto('/#/map/live')
  await toggle(page).click()
  const close = panel(page).getByRole('button', { name: 'Згорнути панель', exact: true })
  await expect(close).toBeFocused()
  await page.keyboard.press('Shift+Tab')
  await expect(panel(page).getByRole('button', { name: 'Відтворення історії', exact: true })).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(close).toBeFocused()
  await expect(panel(page).getByLabel('Час життя позначки')).not.toBeVisible()
  const box = await panel(page).boundingBox()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(320)
  expect(await panel(page).evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
  await page.keyboard.press('Escape')
  await expect(toggle(page)).toBeFocused()
  await expect(panel(page)).toHaveCount(0)
  expect(audit.errors).toEqual([])
})

test('panel visual review: desktop and mobile in light and dark', async ({ page }, testInfo) => {
  await mockApp(page)
  await page.goto('/#/map/live')
  await toggle(page).click()
  await expect(panel(page).getByRole('checkbox')).toHaveCount(1)
  for (const width of [1280, 415, 320]) {
    await page.setViewportSize({ width, height: 900 })
    for (const dark of [false, true]) {
      await closePanel(page)
      await page.getByLabel('Кольорова тема').selectOption(dark ? 'dark' : 'light')
      await expect(page.locator('html')).toHaveAttribute('data-theme', dark ? 'dark' : 'light')
      await toggle(page).click()
      const contrast = await panel(page).evaluate(el => {
        const style = getComputedStyle(el)
        const canvas = document.createElement('canvas')
        canvas.width = canvas.height = 1
        const context = canvas.getContext('2d')!
        const luminance = (color: string) => {
          context.fillStyle = color
          context.fillRect(0, 0, 1, 1)
          const channels = Array.from(context.getImageData(0, 0, 1, 1).data).slice(0, 3).map(value => { const v = value / 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 })
          return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722
        }
        const foreground = luminance(style.color)
        const background = luminance(style.backgroundColor)
        return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05)
      })
      expect(contrast).toBeGreaterThanOrEqual(4.5)
      const path = testInfo.outputPath(`panel-${width}-${dark ? 'dark' : 'light'}.png`)
      await page.screenshot({ path, fullPage: true })
      await testInfo.attach(`panel-${width}-${dark ? 'dark' : 'light'}`, { path, contentType: 'image/png' })
      expect(await panel(page).evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true)
    }
  }
})
