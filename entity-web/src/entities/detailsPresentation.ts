import type { Geometry } from 'geojson'
import type { EntityItem } from '../api/entityExtractor'
import { entityLabel } from './presentation'

const labels: Record<string, string> = {
  label: 'Опис', name: 'Назва', title: 'Заголовок', occurredAt: 'Час події',
  status: 'Стан', type: 'Тип', confidence: 'Впевненість', count: 'Кількість',
  place: 'Місце', region: 'Область', regions: 'Області', from: 'Звідки', to: 'Куди',
  role: 'Роль', aircraftType: 'Тип літака', launchType: 'Тип запуску', alertType: 'Тип тривоги',
  level: 'Рівень', threats: 'Загрози', locationType: 'Тип місця', direction: 'Напрямок',
}

const values: Record<string, string> = {
  active: 'активна', inactive: 'неактивна', started: 'почалася', ended: 'завершилася',
  confirmed: 'підтверджено', high: 'висока', medium: 'середня', low: 'низька', unknown: 'невідомо',
  air_raid: 'повітряна тривога', yellow: 'жовтий', red: 'червоний', oblast: 'область', drones: 'БпЛА', target: 'ціль',
}

export interface DetailRow { key: string; label: string; value: string }

export function entityTitle(item: EntityItem): string {
  for (const key of ['label', 'name', 'title', 'place', 'status']) if (item.values[key]) return String(item.values[key])
  return `${entityLabel(item.entity)} №${item.id}`
}

export function fieldLabel(key: string): string {
  return labels[key] ?? key.replace(/([a-zа-я])([A-ZА-Я])/g, '$1 $2').replaceAll('_', ' ')
}

export function displayValue(value: unknown): string {
  if (value == null || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'так' : 'ні'
  if (typeof value === 'number') return value.toLocaleString('uk-UA')
  if (Array.isArray(value)) return value.map(displayValue).join(', ')
  if (typeof value === 'object') return Object.entries(value as Record<string, unknown>).map(([key, nested]) => `${fieldLabel(key)}: ${displayValue(nested)}`).join('; ')
  const text = String(value)
  return values[text.toLowerCase()] ?? text
}

export function detailRows(item: EntityItem): DetailRow[] {
  const hidden = new Set(['label', 'name', 'title', 'geometry', 'attributes', 'occurredAt', 'stateKey', 'alertId', 'locationUid', 'source'])
  const rows = Object.entries(item.values).filter(([key, value]) => !hidden.has(key) && value != null && value !== '').map(([key, value]) => ({ key, label: fieldLabel(key), value: displayValue(value) }))
  const attributes = item.values.attributes
  if (attributes && typeof attributes === 'object' && !Array.isArray(attributes)) {
    for (const [key, value] of Object.entries(attributes as Record<string, unknown>)) if (!hidden.has(key) && value != null && value !== '') rows.push({ key: `attributes.${key}`, label: fieldLabel(key), value: displayValue(value) })
  }
  return rows
}

function coordinateCount(value: unknown): number {
  if (!Array.isArray(value)) return 0
  if (value.length >= 2 && value.every((part) => typeof part === 'number')) return 1
  return value.reduce((sum, part) => sum + coordinateCount(part), 0)
}

export function geometrySummary(geometry?: Geometry): string {
  if (!geometry) return 'Координати не визначено'
  if (geometry.type === 'Point') {
    const [lon, lat] = geometry.coordinates
    return `Точка: ${lat.toLocaleString('uk-UA', { maximumFractionDigits: 5 })}, ${lon.toLocaleString('uk-UA', { maximumFractionDigits: 5 })}`
  }
  const count = 'coordinates' in geometry ? coordinateCount(geometry.coordinates) : 0
  const names: Record<string, string> = { LineString: 'Лінія', MultiLineString: 'Кілька ліній', Polygon: 'Полігон', MultiPolygon: 'Кілька полігонів', MultiPoint: 'Кілька точок', GeometryCollection: 'Набір геометрій' }
  return `${names[geometry.type] ?? geometry.type}${count ? ` · ${count} точок` : ''}`
}

export function sortEntityItemsNewestFirst(items: EntityItem[]): EntityItem[] {
  return [...items].sort((left, right) => (right.occurredAt ?? '').localeCompare(left.occurredAt ?? '') || right.id.localeCompare(left.id))
}

export function externalUrl(value?: string): string | undefined {
  if (!value) return undefined
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.toString() : undefined
  } catch { return undefined }
}
