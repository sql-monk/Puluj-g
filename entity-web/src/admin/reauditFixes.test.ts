import { describe, expect, it } from 'vitest'
import type { SettingDto } from '../api/admin'
import { pendingChanges } from '../components/settings/fields'
import { quietLabel } from './collectors'
import { extractionOffNote } from './ee'
import { callsHint, inputTokens } from './llmUsage'
import { revisionNote } from './messages'

// The pure parts of the fixes for docs/audits/admin-ui-reaudit-2026-09-24.md.

describe('R01 pending settings changes', () => {
  const setting = (key: string, value: string | null, over: Partial<SettingDto> = {}) => ({ key, value, isSecret: false, hasValue: value !== null, source: 'db', ...over }) as SettingDto
  const all = [setting('Correlation:SlackKm', '8'), setting('Admin:Token', null, { isSecret: true, hasValue: false }), setting('Telegram:ApiHash', null, { isSecret: true, hasValue: true })]

  it('drops values equal to the loaded ones and an empty secret that has nothing stored', () => {
    expect(pendingChanges(all, { 'Correlation:SlackKm': '8', 'Admin:Token': '' })).toEqual({})
  })

  it('keeps real changes, a new secret and the removal of a stored secret', () => {
    expect(pendingChanges(all, { 'Correlation:SlackKm': '10', 'Admin:Token': 'x', 'Telegram:ApiHash': '' })).toEqual({ 'Correlation:SlackKm': '10', 'Admin:Token': 'x', 'Telegram:ApiHash': '' })
  })
})

describe('R02 message revisions', () => {
  it('marks an edit and says its time is the edit time', () => {
    const note = revisionNote({ sourceMessageKey: '79815', sourceRevision: 'e1790208504', revisions: 2 })
    expect(note?.label).toBe('редакція')
    expect(note?.title).toContain('час редагування')
  })

  it('marks the original only when edits of it are stored', () => {
    expect(revisionNote({ sourceMessageKey: '79815', sourceRevision: '0', revisions: 2 })?.label).toBe('версій: 2')
    expect(revisionNote({ sourceMessageKey: '79816', sourceRevision: '0', revisions: 1 })).toBeNull()
  })
})

describe('R04 LLM totals add up', () => {
  const usage = { calls: 4226, withFacts: 15, empty: 2024, refusals: 27, failures: 260, inputTokens: 4304410, cacheWriteTokens: 5398, cacheReadTokens: 45883 }
  it('names refusals and EE successes, the rest of the calls', () => {
    expect(callsHint(usage).replace(/\s/g, ' ')).toBe('фактів 15 · порожньо 2 024 · відмов 27 · успішні (EE) 1 900 · помилок 260')
  })
  it('names cache write in the input total', () => {
    const r = inputTokens(usage)
    expect(r.total).toBe(4355691)
    expect(r.hint).toContain('cache write')
  })
})

describe('R07 quiet source', () => {
  const now = Date.parse('2026-09-24T03:00:00Z')
  it('is quiet after a day without messages', () => {
    expect(quietLabel('2026-09-07T06:09:00Z', now)).toBe('тиша 16 дн')
    expect(quietLabel('2026-09-23T12:00:00Z', now)).toBeNull()
    expect(quietLabel(undefined, now)).toBeNull()
  })
})

describe('O01 extraction switched off', () => {
  it('is said only for a reachable service with no extractor and no LLM', () => {
    expect(extractionOffNote({ extractors: 0, llmEnabled: false, extractor: { available: true } })).toContain('вимкнене')
    expect(extractionOffNote({ extractors: 1, llmEnabled: false, extractor: { available: true } })).toBeNull()
    expect(extractionOffNote({ extractors: 0, llmEnabled: true, extractor: { available: true } })).toBeNull()
    expect(extractionOffNote({ extractors: 0, llmEnabled: false, extractor: { available: false } })).toBeNull()
  })
})
