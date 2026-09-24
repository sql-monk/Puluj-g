import { describe, expect, it } from 'vitest'
import { entityIconChoices, entityIconSvg, entityLabel, isRetiredEntity } from './presentation'

describe('entity presentation', () => {
  it('uses distinct self-contained icons for registered names and a neutral unknown fallback', () => {
    expect(new Set(entityIconChoices.map(name => entityIconSvg(name))).size).toBe(entityIconChoices.length)
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

 it('distinguishes explicit weapon classes without guessing from message text', () => {
   const types = ['missile', 'cruise_missile', 'ballistic_missile', 'shahed_drone', 'geran_drone', 'gerbera_drone', 'jet_drone', 'recon_drone', 'fpv_drone', 'uav', 'aircraft', 'guided_bomb']
   expect(new Set(types.map(targetType => entityIconSvg('target', { targetType }))).size).toBe(types.length)
   expect(entityIconSvg('target', { targetType: 'uav', model: 'Герань-3' })).toBe(entityIconSvg('jet'))
   expect(entityIconSvg('target', { label: 'Герань', attributes: { line: 'Шахед' } })).toBe(entityIconSvg('target'))
   expect(entityIconSvg('launch', { launchType: 'jet_drone' })).not.toBe(entityIconSvg('launch', { launchType: 'ballistic_missile' }))
   for (const name of entityIconChoices) expect(entityIconSvg(name)).not.toContain('<rect')
 })
