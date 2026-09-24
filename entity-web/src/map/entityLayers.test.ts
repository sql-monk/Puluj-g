import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Map as MapLibreMap } from 'maplibre-gl'
import type { FeatureCollection } from 'geojson'
import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import { addEntityLayers, disposeEntityIcons, iconForRenderer, setEntityData } from './entityLayers'

describe('EE renderer icon selection', () => {
  it('does not suppress a point renderer merely because an SVG is configured', () => {
    expect(iconForRenderer('point', 'target', '<svg/>')).toBeUndefined()
  })

  it('versions icon renderer image names when SVG content changes', () => {
    expect(iconForRenderer('icon', 'target', '<svg><circle/></svg>')).not.toBe(iconForRenderer('icon', 'target', '<svg><path/></svg>'))
  })
})

class FakeImage {
  static all: FakeImage[] = []
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  src = ''
  constructor() { FakeImage.all.push(this) }
}
function fixture() {
  FakeImage.all = []
  vi.stubGlobal('Image', FakeImage)
  const images = new Set<string>()
  let collection: FeatureCollection = { type: 'FeatureCollection', features: [] }
  let currentSource = { setData: (data: FeatureCollection) => { collection = data } }
  const map = {
    addSource: () => { currentSource = { setData: data => { collection = data } } },
    getSource: () => currentSource,
    addLayer: vi.fn(),
    hasImage: (name: string) => images.has(name),
    addImage: vi.fn((name: string) => images.add(name)),
    removeImage: (name: string) => images.delete(name),
  }
  const definition: EntityDefinition = { entityName: 'explosion', tableName: 'ee_explosions', fields: [], enabled: true, map: { visible: true, renderer: 'icon', opacity: 0.5 } }
  const item: EntityItem = { entity: 'explosion', table: 'ee_explosions', id: '1', values: {}, geometry: { type: 'Point', coordinates: [0, 0] } }
  const typedMap = map as unknown as MapLibreMap
  addEntityLayers(typedMap)
  return { map: typedMap, addImage: map.addImage, images, definition, item, data: () => collection }
}
afterEach(() => vi.unstubAllGlobals())

describe('icon lifecycle and retired tracks', () => {
  it('keeps a selectable fallback while loading then renders the icon, without duplicate loads', () => {
    const f = fixture()
    setEntityData(f.map, [f.definition], [f.item])
    expect(f.data().features[0].properties).toMatchObject({ renderer: 'point', opacity: 0.5 })
    setEntityData(f.map, [f.definition], [{ ...f.item, id: 'new' }])
    expect(FakeImage.all).toHaveLength(1)
    FakeImage.all[0].onload?.()
    expect(f.data().features[0].properties).toMatchObject({ renderer: 'icon', id: 'new' })
    expect(f.addImage).toHaveBeenCalledTimes(1)
  })
  it('keeps invalid SVG visible without retry loops and retries a changed SVG', () => {
    const f = fixture()
    f.definition.map.svgIcon = 'invalid'
    setEntityData(f.map, [f.definition], [f.item])
    FakeImage.all[0].onerror?.()
    setEntityData(f.map, [f.definition], [f.item])
    expect(FakeImage.all).toHaveLength(1)
    expect(f.data().features[0].properties?.renderer).toBe('point')
    const changed = { ...f.definition, map: { ...f.definition.map, svgIcon: '<svg/>' } }
    setEntityData(f.map, [changed], [f.item])
    expect(FakeImage.all).toHaveLength(2)
    FakeImage.all[1].onload?.()
    expect(f.data().features[0].properties?.renderer).toBe('icon')
  })
  it('ignores late image callbacks after style replacement or disposal', () => {
    const f = fixture()
    setEntityData(f.map, [f.definition], [f.item])
    const oldLoad = FakeImage.all[0].onload!
    addEntityLayers(f.map)
    oldLoad()
    expect(f.addImage).not.toHaveBeenCalled()
    setEntityData(f.map, [f.definition], [f.item])
    const newLoad = FakeImage.all[1].onload!
    disposeEntityIcons(f.map)
    newLoad()
    expect(f.addImage).not.toHaveBeenCalled()
  })
  it('removes retired tracks while retaining custom lines and explicit point renderers', () => {
    const f = fixture()
    const line = { ...f.definition, entityName: 'route', tableName: 'ee_routes', map: { visible: true, renderer: 'line' as const } }
    const track = { ...line, entityName: 'track', tableName: 'ee_tracks' }
    const geometry = { type: 'LineString' as const, coordinates: [[0, 0], [1, 1]] }
    setEntityData(f.map, [{ ...f.definition, map: { ...f.definition.map, renderer: 'point' } }, line, track], [f.item, { ...f.item, entity: 'route', table: 'ee_routes', geometry }, { ...f.item, entity: 'track', table: 'ee_tracks', geometry }])
    expect(f.data().features.map(feature => feature.properties?.renderer)).toEqual(['point', 'line'])
    expect(FakeImage.all).toHaveLength(0)
  })
})

it('loads separate silhouettes per target and releases variants no longer displayed', () => {
  const f = fixture()
  const definition = { ...f.definition, entityName: 'target' }
  const items = ['shahed_drone', 'jet_drone', 'ballistic_missile'].map((targetType, index) => ({ ...f.item, entity: 'target', id: String(index), values: { targetType } }))
  setEntityData(f.map, [definition], items)
  expect(FakeImage.all).toHaveLength(3)
  for (const image of FakeImage.all) image.onload?.()
  expect(new Set(f.data().features.map(feature => feature.properties?.icon)).size).toBe(3)
  setEntityData(f.map, [definition], [items[0]])
  expect(f.images.size).toBe(1)
})
