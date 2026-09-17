import { describe, expect, it } from 'vitest'
import type { AdminSourceDto } from '../../api/admin'
import { filterSources, sortSources } from './sources'

const source = (name: string, overrides: Partial<AdminSourceDto> = {}): AdminSourceDto => ({
  id: name.charCodeAt(0),
  code: `tg_${name.toLowerCase()}`,
  name,
  type: 'Telegram',
  enabled: true,
  trustLevel: 0.6,
  priority: 50,
  hasToken: false,
  rawMessageCount: 0,
  consecutiveFailures: 0,
  status: 'idle',
  ...overrides,
})

describe('source settings table helpers', () => {
  it('finds a source by its displayed name, channel, or code', () => {
    const rows = [source('Повітряні сили', { channel: 'kpszsu' }), source('Монітор')]
    expect(filterSources(rows, 'повітряні').map((x) => x.name)).toEqual(['Повітряні сили'])
    expect(filterSources(rows, 'KPSZSU').map((x) => x.name)).toEqual(['Повітряні сили'])
    expect(filterSources(rows, 'tg_монітор').map((x) => x.name)).toEqual(['Монітор'])
  })

  it('sorts numeric columns and uses the name to break ties', () => {
    const rows = [source('Бета', { priority: 20 }), source('Альфа', { priority: 20 }), source('Гамма', { priority: 5 })]
    expect(sortSources(rows, 'priority', true).map((x) => x.name)).toEqual(['Гамма', 'Альфа', 'Бета'])
    expect(sortSources(rows, 'priority', false).map((x) => x.name)).toEqual(['Альфа', 'Бета', 'Гамма'])
  })
})
