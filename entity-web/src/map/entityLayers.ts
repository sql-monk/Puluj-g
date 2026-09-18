import type { Feature, FeatureCollection, Geometry } from 'geojson'
import type * as maplibregl from 'maplibre-gl'
import type { EntityDefinition, EntityItem } from '../api/entityExtractor'
import { setData } from './layers'

const source = 'ee-entities'
export const ENTITY_HIT_LAYERS = ['ee-entity-icons', 'ee-entity-points', 'ee-entity-lines', 'ee-entity-polygons']
const [iconLayer, pointLayer, lineLayer, polygonLayer] = ENTITY_HIT_LAYERS
const polygonLineLayer = 'ee-entity-polygon-lines'

export function addEntityLayers(map: maplibregl.Map) {
  map.addSource(source, { type: 'geojson', data: { type: 'FeatureCollection', features: [] } })
  map.addLayer({ id: polygonLayer, type: 'fill', source, filter: ['all', ['==', ['geometry-type'], 'Polygon'], ['==', ['get', 'renderer'], 'polygon']], paint: { 'fill-color': ['coalesce', ['get', 'color'], '#ef4444'], 'fill-opacity': ['coalesce', ['get', 'opacity'], 0.25] } })
  map.addLayer({ id: polygonLineLayer, type: 'line', source, filter: ['all', ['==', ['geometry-type'], 'Polygon'], ['==', ['get', 'renderer'], 'polygon']], paint: { 'line-color': ['coalesce', ['get', 'color'], '#ef4444'], 'line-width': ['coalesce', ['get', 'width'], 2], 'line-dasharray': ['case', ['==', ['get', 'dash'], 'dashed'], ['literal', [3, 2]], ['==', ['get', 'dash'], 'dotted'], ['literal', [1, 2]], ['literal', [1, 0]]] } })
  map.addLayer({ id: lineLayer, type: 'line', source, filter: ['all', ['==', ['geometry-type'], 'LineString'], ['==', ['get', 'renderer'], 'line']], paint: { 'line-color': ['coalesce', ['get', 'color'], '#f97316'], 'line-width': ['coalesce', ['get', 'width'], 3], 'line-opacity': ['coalesce', ['get', 'opacity'], 0.9], 'line-dasharray': ['case', ['==', ['get', 'dash'], 'dashed'], ['literal', [3, 2]], ['==', ['get', 'dash'], 'dotted'], ['literal', [1, 2]], ['literal', [1, 0]]] } })
  map.addLayer({ id: pointLayer, type: 'circle', source, filter: ['all', ['==', ['geometry-type'], 'Point'], ['==', ['get', 'renderer'], 'point'], ['!', ['has', 'icon']]], paint: { 'circle-radius': 7, 'circle-color': ['coalesce', ['get', 'color'], '#dc2626'], 'circle-stroke-color': '#fff', 'circle-stroke-width': 2, 'circle-opacity': ['coalesce', ['get', 'opacity'], 1] } })
  map.addLayer({ id: iconLayer, type: 'symbol', source, filter: ['all', ['==', ['geometry-type'], 'Point'], ['==', ['get', 'renderer'], 'icon'], ['has', 'icon']], layout: { 'icon-image': ['get', 'icon'], 'icon-size': 0.7, 'icon-allow-overlap': true } })
}

export function setEntityData(map: maplibregl.Map, definitions: EntityDefinition[], items: EntityItem[]) {
  const byName = new Map(definitions.map((definition) => [definition.entityName, definition]))
  for (const definition of definitions) if (definition.map.svgIcon) ensureSvgIcon(map, iconName(definition.entityName, definition.map.svgIcon), definition.map.svgIcon)
  const features: Feature<Geometry>[] = []
  for (const item of items) {
    if (!item.geometry) continue
    const definition = byName.get(item.entity)
    if (!definition?.map.visible) continue
    const labelValue = definition.map.labelField ? item.values[definition.map.labelField] : undefined
    const status = definition.map.statusField ? item.values[definition.map.statusField] : undefined
    const inactive = typeof status === 'string' && ['inactive', 'closed', 'ended', 'cancelled'].includes(status.toLowerCase())
    const renderer = definition.map.renderer === 'icon' && !definition.map.svgIcon ? 'point' : definition.map.renderer
    const icon = iconForRenderer(renderer, definition.entityName, definition.map.svgIcon)
    features.push({ type: 'Feature', id: `${item.entity}:${item.id}`, geometry: item.geometry, properties: { entity: item.entity, id: item.id, label: labelValue == null ? item.entity : String(labelValue), renderer, status: status == null ? '' : String(status), color: definition.map.color ?? '#dc2626', width: definition.map.width ?? 2, opacity: (definition.map.opacity ?? 0.9) * (inactive ? 0.35 : 1), dash: normalizeDash(definition.map.dash), ...(icon ? { icon } : {}) } })
  }
  setData(map, source, { type: 'FeatureCollection', features } as FeatureCollection<Geometry>)
}

function iconName(entity: string, svg: string) { let hash = 0; for (let i = 0; i < svg.length; i++) hash = ((hash << 5) - hash + svg.charCodeAt(i)) | 0; return `ee-icon-${entity}-${hash >>> 0}` }
export function iconForRenderer(renderer: EntityDefinition['map']['renderer'], entity: string, svg?: string) { return renderer === 'icon' && svg ? iconName(entity, svg) : undefined }
function normalizeDash(value?: string) { return value?.toLowerCase() === 'dotted' ? 'dotted' : value ? 'dashed' : 'solid' }
function ensureSvgIcon(map: maplibregl.Map, name: string, svg: string) {
  if (map.hasImage(name)) return
  const image = new Image(48, 48)
  image.onload = () => { if (!map.hasImage(name)) map.addImage(name, image, { pixelRatio: 2 }) }
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}
