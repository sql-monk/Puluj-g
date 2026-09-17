import { expect, test } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

const baseUrl = process.env.E13_ACTUAL_BASE_URL

test.describe('U13 actual public acceptance', () => {
  test.skip(!baseUrl, 'scripts/test-u13-actual.ps1 supplies a real API base URL')

  test('built SPA uses the real API, PostGIS catalogue and SignalR hub', async ({ page }) => {
    const apiResponses: string[] = []
    page.on('response', (response) => {
      const path = new URL(response.url()).pathname
      if (path.startsWith('/api/')) apiResponses.push(response.url())
    })

    await page.goto('/#/messages')
    await expect(page.getByRole('heading', { name: 'Повідомлення' })).toBeVisible()
    if (process.env.E13_SCREENSHOT_PATH) await page.screenshot({ path: process.env.E13_SCREENSHOT_PATH, fullPage: true })
    const accessibility = await new AxeBuilder({ page }).analyze()
    expect(accessibility.violations).toEqual([])
    const firstMessage = page.locator('a[href^="#/messages/"]').first()
    await expect(firstMessage).toBeVisible()
    await firstMessage.click()
    await expect(page.getByRole('heading', { name: 'Повідомлення' })).toBeVisible()
    await expect(page).toHaveURL(/#\/messages\/\d+$/)

    await page.goto('/#/entities')
    await expect(page.getByRole('heading', { name: 'Цілі і події' })).toBeVisible()
    await expect(page.getByText(/Трек|Тривога|Подія/).first()).toBeVisible()

    const negotiate = await page.request.post('/hubs/map/negotiate?negotiateVersion=1')
    expect(negotiate.ok()).toBe(true)
    expect(await negotiate.json()).toHaveProperty('connectionId')
    await expect.poll(() => apiResponses.length).toBeGreaterThan(2)
    for (const url of apiResponses) expect(url.startsWith(baseUrl!)).toBe(true)
  })

  test('real public contracts expose paging, revisions, aggregates and an exact bigint', async ({ request }) => {
    const from = new Date(Date.now() - 60 * 60 * 1000).toISOString()
    const to = new Date(Date.now() + 60 * 1000).toISOString()
    const windowQuery = `from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&pageSize=100`
    const page = await request.get(`/api/public/messages?${windowQuery}`)
    expect(page.ok()).toBe(true)
    const first = await page.json() as { items: Array<{ id: string; sourceMessageKey: string; sourceRevision: string }>; nextCursor?: string }
    expect(first.items).toHaveLength(100)
    expect(first.nextCursor).toBeTruthy()

    const continuation = await request.get(`/api/public/messages?${windowQuery}&cursor=${encodeURIComponent(first.nextCursor!)}`)
    const second = await continuation.json() as { items: Array<{ id: string; sourceMessageKey: string; sourceRevision: string }> }
    const revision = [...first.items, ...second.items].find((item) => item.sourceMessageKey === 'revision' && item.sourceRevision === '0')
    expect(revision).toBeTruthy()

    const revisions = await request.get(`/api/public/messages/${revision!.id}/revisions`)
    const revisionBody = await revisions.json() as { totalCount: number }
    expect(revisions.ok()).toBe(true)
    expect(revisionBody.totalCount).toBe(2)

    const results = await request.get(`/api/public/messages/${revision!.id}/results`)
    const resultBody = await results.json() as { items: Array<{ targetId: string }> }
    expect(results.ok()).toBe(true)
    expect(resultBody.items.some((item) => item.targetId === '9007199254740993')).toBe(true)

    const entities = await request.get('/api/public/entities?pageSize=100')
    const entityBody = await entities.json() as { items: Array<{ kind: string }> }
    expect(entities.ok()).toBe(true)
    expect(entityBody.items.map((item) => item.kind)).toEqual(expect.arrayContaining(['track', 'incident', 'alert']))
  })
})
