import { expect, test } from '@playwright/test'
import { centerOn, clickIncident, openMap, project, sourceFeatures } from './helpers'

/** E02 (§8.5 popup): state, both time scales, sources, revision/policy, raw text with the source permalink, revisions with redacted actors. */
test.describe('E02 provenance', () => {
  test('incident popup carries the provenance chain and the permalink', async ({ page }) => {
    await openMap(page)
    await clickIncident(page, 36.23, 49.99, 11)
    const dialog = page.getByRole('dialog')
    await expect(dialog).toHaveAttribute('aria-label', /Повідомлення про вибух: Харків/)
    await expect(dialog).toContainText('повідомлено') // the state, as a word, not a colour
    await expect(dialog.locator('dt', { hasText: 'Подія' })).toBeVisible() // event time (effective)
    await expect(dialog.locator('dt', { hasText: 'Останнє' })).toBeVisible() // last report (recorded scale)
    await expect(dialog).toContainText('2 · 2 повідомл.') // sourceCount · observationCount
    await expect(dialog).toContainText('incident-1/p2') // policy version behind the linking
    await expect(dialog).toContainText('Текст повідомлення про подію #2')
    const link = dialog.getByRole('link', { name: 'джерело' })
    await expect(link).toHaveAttribute('href', 'https://t.me/tg_test/902')
    await expect(link).toHaveAttribute('rel', /noreferrer/)
    const revisions = dialog.getByRole('list', { name: 'Ревізії' })
    await expect(revisions).toContainText('#1 created')
    await expect(revisions).toContainText('#2 updated')
    await expect(revisions).toContainText('operator') // redacted on the public API, never a name
    await expect(revisions).not.toContainText('@')
    // Snapshot of the popup DOM only (the map behind is not part of the contract); relative times are masked.
    await expect(dialog).toHaveScreenshot('incident-popup.png', { mask: [dialog.locator('dd[title]'), dialog.getByRole('list', { name: 'Ревізії' })], maxDiffPixelRatio: 0.02 })
    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)
  })

  test('legacy event popup keeps the source permalink; the explosion marker is hidden behind its incident', async ({ page }) => {
    await openMap(page)
    // The cancellation has no incident kind: it stays a legacy marker with its own popup and the source permalink.
    const events = await sourceFeatures(page, 'events')
    expect(events.map((f) => f.properties.id)).toEqual([1003])
    await centerOn(page, 35.5, 49.0, 9)
    const at = await project(page, 35.5, 49.0)
    await page.mouse.click(at.x, at.y)
    const popup = page.locator('div', { hasText: 'Скасування повідомлення про ціль' }).last()
    await expect(popup).toBeVisible()
    await expect(page.getByRole('link', { name: 'оригінал' })).toHaveAttribute('href', 'https://t.me/tg_test/1903')
    // Incident layer on → the legacy marker of the same explosion is hidden: the click opens the incident, not two popups.
    await clickIncident(page, 36.23, 49.99, 11)
    await expect(page.getByRole('dialog')).toHaveCount(1)
  })
})
