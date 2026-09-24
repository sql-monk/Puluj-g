import { describe, expect, it } from 'vitest'
import { entityIconChoices, entityIconSvg, entityLabel, isRetiredEntity } from './presentation'

describe('entity presentation', () => {
  it('uses distinct self-contained icons for registered names and a neutral unknown fallback', () => {
    expect(new Set(entityIconChoices.map(entityIconSvg)).size).toBe(entityIconChoices.length)
    expect(entityIconSvg('unknown')).toBe(entityIconSvg('another_custom_entity'))
    expect(entityIconSvg('ee_explosions')).toBe(entityIconSvg('explosion'))
    expect(entityIconSvg('<script>')).not.toContain('<script>')
    expect(entityLabel('airDefenseAction')).toBe('Подія ППО')
    expect(entityLabel('custom')).toBe('custom')
  })
  it('retires track names and physical table aliases without suppressing custom line entities', () => {
    for (const name of ['track', 'tracks', 'EE_TRACKS', ' Track ']) expect(isRetiredEntity(name)).toBe(true)
    expect(isRetiredEntity('renamed', 'ee_tracks')).toBe(true)
    expect(isRetiredEntity('route', 'ee_routes')).toBe(false)
  })
})
