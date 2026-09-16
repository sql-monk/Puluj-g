import type { Page } from '@playwright/test'

/** Typed access to the DEV hooks (`window.__store`, `window.__incidents`, `window.__incidentsPush`, `window.__map`). */
export const storeCall = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => {
    const s = (window as unknown as { __store: { getState(): Record<string, (...x: unknown[]) => unknown> } }).__store.getState()
    // ISO strings that look like dates cross the bridge as strings: hand the store real Dates.
    const revived = a.map((x) => (typeof x === 'string' && /^\d{4}-\d{2}-\d{2}T.*Z$/.test(x) ? new Date(x) : x))
    return s[f](...revived)
  }, [fn, args] as [string, unknown[]])

export const incidentsCall = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => {
    const s = (window as unknown as { __incidents: { getState(): Record<string, (...x: unknown[]) => unknown> } }).__incidents.getState()
    return s[f](...a)
  }, [fn, args] as [string, unknown[]])

export const incidentIds = (page: Page) =>
  page.evaluate(() => Object.keys((window as unknown as { __incidents: { getState(): { byId: Record<string, unknown> } } }).__incidents.getState().byId).map(Number).sort((a, b) => a - b))
