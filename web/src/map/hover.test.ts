import { describe, expect, it } from 'vitest'
import { hoverLabel } from './hover'

describe('map hover priority', () => {
  it('keeps visible markers ahead of polygon names', () => {
    expect(hoverLabel({ target: 'Shahed', event: 'Вибух', incident: 'Пожежа', region: 'Київська область' })).toBe('Shahed')
    expect(hoverLabel({ event: 'Вибух', incident: 'Пожежа', region: 'Київська область' })).toBe('Вибух')
    expect(hoverLabel({ incident: 'Пожежа', region: 'Київська область' })).toBe('Пожежа')
    expect(hoverLabel({ region: 'Київська область' })).toBe('Київська область')
    expect(hoverLabel({})).toBeNull()
  })
})
