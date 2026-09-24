import type { Feature, FeatureCollection, Geometry } from 'geojson'
import type * as maplibregl from 'maplibre-gl'
import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import { setData } from './layers'
import { entityIconSvg, isRetiredEntity } from '../entities/presentation'

const source = 'ee-entities'
export const ENTITY_HIT_LAYERS = ['ee-entity-icons', 'ee-entity-points', 'ee-entity-lines', 'ee-entity-polygons']
const [iconLayer, pointLayer, lineLayer, polygonLayer] = ENTITY_HIT_LAYERS
const polygonLineLayer = 'ee-entity-polygon-lines'
type IconState = { source: unknown; images: Map<string, HTMLImageElement>; refresh?: () => void }
const iconStates = new WeakMap<maplibregl.Map, IconState>()

export function disposeEntityIcons(map: maplibregl.Map) {
  const state = iconStates.get(map)
  if (state) for (const image of state.images.values()) { image.onload = null; image.onerror = null }
  iconStates.delete(map)
}

export function addEntityLayers(map: maplibregl.Map) {
  disposeEntityIcons(map)
  map.addSource(source, { type: 'geojson', data: { type: 'FeatureCollection', features: [] } })
  iconStates.set(map, { source: map.getSource(source), images: new Map() })
  map.addLayer({ id: polygonLayer, type: 'fill', source, filter: ['all', ['==', ['geometry-type'], 'Polygon'], ['==', ['get', 'renderer'], 'polygon']], paint: { 'fill-color': ['coalesce', ['get', 'color'], '#ef4444'], 'fill-opacity': ['coalesce', ['get', 'opacity'], 0.25] } })
  map.addLayer({ id: polygonLineLayer, type: 'line', source, filter: ['all', ['==', ['geometry-type'], 'Polygon'], ['==', ['get', 'renderer'], 'polygon']], paint: { 'line-color': ['coalesce', ['get', 'color'], '#ef4444'], 'line-width': ['coalesce', ['get', 'width'], 2], 'line-dasharray': ['case', ['==', ['get', 'dash'], 'dashed'], ['literal', [3, 2]], ['==', ['get', 'dash'], 'dotted'], ['literal', [1, 2]], ['literal', [1, 0]]] } })
  map.addLayer({ id: lineLayer, type: 'line', source, filter: ['all', ['==', ['geometry-type'], 'LineString'], ['==', ['get', 'renderer'], 'line']], paint: { 'line-color': ['coalesce', ['get', 'color'], '#f97316'], 'line-width': ['coalesce', ['get', 'width'], 3], 'line-opacity': ['coalesce', ['get', 'opacity'], 0.9], 'line-dasharray': ['case', ['==', ['get', 'dash'], 'dashed'], ['literal', [3, 2]], ['==', ['get', 'dash'], 'dotted'], ['literal', [1, 2]], ['literal', [1, 0]]] } })
  map.addLayer({ id: pointLayer, type: 'circle', source, filter: ['all', ['==', ['geometry-type'], 'Point'], ['==', ['get', 'renderer'], 'point'], ['!', ['has', 'icon']]], paint: { 'circle-radius': 7, 'circle-color': ['coalesce', ['get', 'color'], '#dc2626'], 'circle-stroke-color': '#fff', 'circle-stroke-width': 2, 'circle-opacity': ['coalesce', ['get', 'opacity'], 1] } })
  map.addLayer({ id: iconLayer, type: 'symbol', source, filter: ['all', ['==', ['geometry-type'], 'Point'], ['==', ['get', 'renderer'], 'icon'], ['has', 'icon']], layout: { 'icon-image': ['get', 'icon'], 'icon-size': 1, 'icon-allow-overlap': true }, paint: { 'icon-opacity': ['coalesce', ['get', 'opacity'], 1] } })
}

export function setEntityData(map: maplibregl.Map, definitions: EntityDefinition[], items: EntityItem[]) {
  const state = iconStates.get(map)
  if (!state || state.source !== map.getSource(source)) return
  state.refresh = () => setEntityData(map, definitions, items)
  const byName = new Map(definitions.map((definition) => [definition.entityName, definition]))
  const usedIcons = new Set<string>()
  for (const definition of definitions) {
    if (!definition.map.visible || isRetiredEntity(definition.entityName, definition.tableName) || definition.map.renderer !== 'icon') continue
    const svg = definition.map.svgIcon || entityIconSvg(definition.entityName)
    const name = iconName(definition.entityName, svg)
    usedIcons.add(name)
    ensureSvgIcon(map, state, name, svg)
  }
  // Do not retain every historical revision of an administrator's SVG in the style atlas.
  for (const [name, image] of state.images) if (!usedIcons.has(name)) {
    image.onload = null; image.onerror = null
    state.images.delete(name)
    if (map.hasImage(name)) map.removeImage(name)
  }
  const features: Feature<Geometry>[] = []
  for (const item of items) {
    if (!item.geometry || isRetiredEntity(item.entity, item.table)) continue
    const definition = byName.get(item.entity)
    if (!definition?.map.visible) continue
    const labelValue = definition.map.labelField ? item.values[definition.map.labelField] : undefined
    const status = definition.map.statusField ? item.values[definition.map.statusField] : undefined
    const inactive = typeof status === 'string' && ['inactive', 'closed', 'ended', 'cancelled'].includes(status.toLowerCase())
    const requestedIcon = iconForRenderer(definition.map.renderer, definition.entityName, definition.map.svgIcon)
    const icon = requestedIcon && map.hasImage(requestedIcon) ? requestedIcon : undefined
    // A pending or invalid SVG leaves a visible, clickable point instead of an invisible entity.
    const renderer = definition.map.renderer === 'icon' && !icon ? 'point' : definition.map.renderer
    features.push({ type: 'Feature', id: `${item.entity}:${item.id}`, geometry: item.geometry, properties: { entity: item.entity, id: item.id, label: labelValue == null ? item.entity : String(labelValue), renderer, status: status == null ? '' : String(status), color: definition.map.color ?? '#dc2626', width: definition.map.width ?? 2, opacity: (definition.map.opacity ?? 0.9) * (inactive ? 0.35 : 1), dash: normalizeDash(definition.map.dash), ...(icon ? { icon } : {}) } })
  }
  setData(map, source, { type: 'FeatureCollection', features } as FeatureCollection<Geometry>)
}

function iconName(entity: string, svg: string) { let hash = 0; for (let i = 0; i < svg.length; i++) hash = ((hash << 5) - hash + svg.charCodeAt(i)) | 0; return `ee-icon-${entity}-${hash >>> 0}` }
export function iconForRenderer(renderer: EntityDefinition['map']['renderer'], entity: string, svg?: string) { return renderer === 'icon' ? iconName(entity, svg || entityIconSvg(entity)) : undefined }
function normalizeDash(value?: string) { return value?.toLowerCase() === 'dotted' ? 'dotted' : value ? 'dashed' : 'solid' }
function ensureSvgIcon(map: maplibregl.Map, state: IconState, name: string, svg: string) {
  if (map.hasImage(name) || state.images.has(name)) return
  const image = new Image(48, 48)
  state.images.set(name, image)
  image.onload = () => {
    if (iconStates.get(map) !== state || state.images.get(name) !== image || map.getSource(source) !== state.source) return
    if (!map.hasImage(name)) map.addImage(name, image, { pixelRatio: 2 })
    state.refresh?.()
  }
  // Cache failures until the SVG or style changes; avoid retrying on every snapshot.
  image.onerror = () => { image.onload = null; image.onerror = null }
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}
