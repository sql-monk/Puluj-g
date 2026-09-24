// Pure helpers of the Entity Extractor panels: human wording of queue statuses and results, and what the settings form sends.
import type { EeDelivery, EeDeliveryStatus, EeRun, EeSetting } from '../api/entityAdmin'

export const DELIVERY_STATUS: Record<EeDeliveryStatus, { text: string; ok: boolean | null }> = {
  pending: { text: 'у черзі', ok: null },
  in_progress: { text: 'обробляється', ok: null },
  succeeded: { text: 'доставлено', ok: true },
  failed: { text: 'помилка', ok: false },
}

/** `result` of a delivery or run: 1 — entities were written; 0 — the message had nothing to extract, which is not a failure. */
export function resultLabel(result: number | null | undefined): string {
  if (result === 1) return 'сутності записано'
  if (result === 0) return 'без сутностей'
  return '—'
}

export function deliveryState(d: Pick<EeDelivery, 'status'>): { text: string; ok: boolean | null } {
  return DELIVERY_STATUS[d.status] ?? { text: d.status, ok: null }
}

export function runState(r: Pick<EeRun, 'status'>): { text: string; ok: boolean | null } {
  if (r.status === 'completed') return { text: 'завершено', ok: true }
  if (r.status === 'processing') return { text: 'обробляється', ok: null }
  if (r.status === 'failed') return { text: 'помилка', ok: false }
  return { text: r.status, ok: null }
}

export const SETTING_SOURCE: Record<EeSetting['source'], string> = {
  db: 'збережено тут',
  config: 'з конфігурації сервісу',
  default: 'типове значення',
}

/**
 * The values to save: only fields the operator changed. An emptied field is sent as null, which removes the stored value so
 * the configuration / built-in default applies again.
 */
export function changedSettings(settings: EeSetting[], draft: Record<string, string>): Record<string, string | null> {
  const out: Record<string, string | null> = {}
  for (const s of settings) {
    if (!(s.key in draft)) continue
    const next = draft[s.key].trim()
    if (next === (s.value ?? '')) continue
    out[s.key] = next === '' ? null : next
  }
  return out
}

/**
 * A reachable service with nothing to extract with: no enabled Python extractor and the LLM off. The status badge speaks of
 * availability, so this sentence says that deliveries now end "без сутностей" by configuration (admin re-audit O01).
 */
export function extractionOffNote(o: { extractors: number; llmEnabled: boolean; extractor: { available: boolean } }): string | null {
  if (!o.extractor.available || o.extractors > 0 || o.llmEnabled) return null
  return 'Сервіс доступний, але вилучення сутностей фактично вимкнене: немає жодного увімкненого Python-екстрактора, LLM вимкнено. Доставки завершуються «без сутностей», доки не ввімкнути одне з двох.'
}
