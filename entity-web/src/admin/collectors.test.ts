import { describe, expect, it } from 'vitest'
import type { CollectorStatusDto } from '../api/admin'
import { sortCollectors } from './collectors'

const collector = (name: string, overrides: Partial<CollectorStatusDto> = {}): CollectorStatusDto => ({
  sourceId: name.charCodeAt(0),
  code: `source_${name}`,
  name,
  type: 'Telegram',
  enabled: true,
  consecutiveFailures: 0,
  messages24h: 0,
  perHour: [],
  ...overrides,
})

describe('collectors table sorting', () => {
  it('sorts displayed Telegram names and breaks ties predictably', () => {
    const rows = [collector('Бета'), collector('Альфа', { channelTitle: 'Гамма' }), collector('Вітер')]
    expect(sortCollectors(rows, 'name', true).map((x) => x.name)).toEqual(['Бета', 'Вітер', 'Альфа'])
  })

  it('sorts date and numeric columns while keeping missing values last in either direction', () => {
    const rows = [
      collector('Пізній', { lastPolledAt: '2026-09-18T12:00:00Z', messages24h: 4 }),
      collector('Без дати', { messages24h: 9 }),
      collector('Ранній', { lastPolledAt: '2026-09-18T08:00:00Z', messages24h: 1 }),
    ]
    expect(sortCollectors(rows, 'lastPolledAt', false).map((x) => x.name)).toEqual(['Пізній', 'Ранній', 'Без дати'])
    expect(sortCollectors(rows, 'messages24h', true).map((x) => x.name)).toEqual(['Ранній', 'Пізній', 'Без дати'])
  })

  it('places failed enabled collectors before unpolled, healthy, and disabled ones by status', () => {
    const rows = [
      collector('Вимкнений', { enabled: false }),
      collector('Здоровий', { lastSuccessAt: '2026-09-18T12:00:00Z' }),
      collector('Не опитаний'),
      collector('Помилка', { consecutiveFailures: 2 }),
    ]
    expect(sortCollectors(rows, 'status', true).map((x) => x.name)).toEqual(['Помилка', 'Не опитаний', 'Здоровий', 'Вимкнений'])
  })
})
