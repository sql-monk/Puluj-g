import { describe, expect, it } from 'vitest'
import type { AdminSourceDto, WorkerInstanceDto } from '../api/admin'
import type { EeSetting } from '../api/entityAdmin'
import { selectionScope } from '../components/settings/sources'
import { changedSettings, deliveryState, resultLabel } from './ee'
import { sqlErrorLocation } from './format'
import { messageFilterFromQuery, messagesHash } from './messages'
import { processorBadge } from './workers'

// The pure parts of the fixes for docs/audits/admin-ui-audit-2026-09-24.md.

describe('A01 message route', () => {
  it('reads the source from the collectors link, including the older sourceIds form', () => {
    expect(messageFilterFromQuery('sourceId=86')).toEqual({ sourceId: 86 })
    expect(messageFilterFromQuery('?sourceIds=86,87')).toEqual({ sourceId: 86 })
    expect(messageFilterFromQuery('rawMessageId=123&sourceId=86')).toEqual({ rawMessageId: 123 })
    expect(messageFilterFromQuery('sourceId=abc')).toEqual({})
    expect(messageFilterFromQuery('')).toEqual({})
  })

  it('builds the hash back', () => {
    expect(messagesHash({ sourceId: 86 })).toBe('#/messages?sourceId=86')
    expect(messagesHash({ rawMessageId: 5 })).toBe('#/messages?rawMessageId=5')
    expect(messagesHash({})).toBe('#/messages')
  })
})

describe('A02 processor badge', () => {
  const processor = (alive: boolean) => ({ name: 'processor', kind: 'processor', alive, processed24h: 0, inProgress: 0 }) as WorkerInstanceDto
  it('is unknown, not "не працює", while the answer is pending', () => {
    expect(processorBadge(null)).toEqual({ ok: null, text: 'завантаження…' })
    expect(processorBadge(null, 'HTTP 502')).toEqual({ ok: null, text: 'стан невідомий' })
  })
  it('reflects the received instances', () => {
    expect(processorBadge([processor(true)])).toEqual({ ok: true, text: 'працює' })
    expect(processorBadge([processor(false)])).toEqual({ ok: false, text: 'не працює' })
    expect(processorBadge([])).toEqual({ ok: false, text: 'не працює' })
  })
})

describe('A03 queue wording', () => {
  it('explains result 0 as "nothing to write", not an error', () => {
    expect(resultLabel(0)).toBe('без сутностей')
    expect(resultLabel(1)).toBe('сутності записано')
    expect(resultLabel(undefined)).toBe('—')
    expect(deliveryState({ status: 'failed' })).toEqual({ text: 'помилка', ok: false })
  })
})

describe('A05 settings form', () => {
  const setting = (key: string, value?: string): EeSetting => ({ key, label: key, value, source: value ? 'db' : 'default', effective: value ?? '00:00:30', default: '00:00:30', format: '', hint: '' })
  it('sends only changed fields; an emptied field removes the stored value', () => {
    const settings = [setting('A', '5'), setting('B'), setting('C', '7')]
    expect(changedSettings(settings, { A: '5', B: ' 00:01:00 ', C: '' })).toEqual({ B: '00:01:00', C: null })
    expect(changedSettings(settings, {})).toEqual({})
  })
})

describe('A06 selection scope', () => {
  const source = (id: number, name: string) => ({ id, name, code: `s${id}` }) as AdminSourceDto
  it('keeps selected sources hidden by the search in the scope and names them', () => {
    const all = [source(1, 'kpszsu'), source(2, 'other'), source(3, 'third')]
    const scope = selectionScope(new Set([1, 3, 99]), [all[1]], all)
    expect(scope.sources.map((s) => s.id)).toEqual([1, 3])
    expect(scope.hidden.map((s) => s.id)).toEqual([1, 3])
    expect(selectionScope(new Set([2]), [all[1]], all).hidden).toEqual([])
  })
})

describe('A09 SQL error position', () => {
  it('maps the 1-based position to line and column', () => {
    expect(sqlErrorLocation('SELECT 1 +', 11)).toEqual({ line: 1, column: 11, text: 'SELECT 1 +' })
    expect(sqlErrorLocation('SELECT a\nFROM x\nWHER y', 17)).toEqual({ line: 3, column: 1, text: 'WHER y' })
    expect(sqlErrorLocation('SELECT 1', 0)).toBeNull()
    expect(sqlErrorLocation('SELECT 1', 50)).toBeNull()
  })
})
