import { afterEach, describe, expect, it, vi } from 'vitest'
import { entityApi, type EntityItem } from './entityExtractor'

const track: EntityItem = { entity: 'track', table: 'ee_tracks', id: '1', values: {} }
const other: EntityItem = { entity: 'custom', table: 'ee_custom', id: '2', values: {} }
function response(body: unknown) { return new Response(JSON.stringify(body), { status: 200 }) }
afterEach(() => vi.unstubAllGlobals())

describe('retired tracks against an old API', () => {
  it('removes track definitions, snapshot entries and related history', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response([{ entityName: 'renamed', tableName: 'ee_tracks' }, { entityName: 'custom', tableName: 'ee_custom' }]))
      .mockResolvedValueOnce(response({ items: [track, other], generatedAt: 'now' }))
      .mockResolvedValueOnce(response([track, other]))
    vi.stubGlobal('fetch', fetch)
    expect(await entityApi.definitions()).toEqual([{ entityName: 'custom', tableName: 'ee_custom' }])
    expect((await entityApi.snapshot()).items).toEqual([other])
    expect(await entityApi.history('custom', '2')).toEqual([other])
  })
  it('rejects a retired detail and returns an empty retired-only query without contacting the server', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    await expect(entityApi.detail('ee_tracks', '1')).rejects.toThrow('Треки вимкнено')
    expect(await entityApi.catalogue({ kinds: ['track', 'ee_tracks'] })).toEqual({ items: [], totalCount: 0, totalCountExact: true })
    expect(await entityApi.history('track', '1')).toEqual([])
    expect(fetch).not.toHaveBeenCalled()
  })
  it('keeps pagination but marks the old server total as unavailable instead of guessing', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({ items: [track, other], totalCount: 900, nextCursor: 'next' })))
    const page = await entityApi.catalogue()
    expect(page).toMatchObject({ items: [other], totalCountExact: false, nextCursor: 'next' })
  })
  it('does not trust a legacy total even if retired rows occur only on later pages', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response({ items: [other], totalCount: 900, nextCursor: 'next' }))
      .mockResolvedValueOnce(response({ items: [other], totalCount: 1, excludesRetiredTracks: true }))
    vi.stubGlobal('fetch', fetch)
    expect((await entityApi.catalogue()).totalCountExact).toBe(false)
    expect((await entityApi.catalogue()).totalCountExact).toBe(true)
  })
})
