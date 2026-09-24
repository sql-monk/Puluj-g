/** Presentation only: names already supplied by the entity registry, never inferred from message content. */
const entries: Record<string, { label: string; shape: string }> = {
  alert: { label: 'Тривога', shape: '<path d="M12 4 22 21H2Z"/><path d="M12 10v5m0 3h.01"/>' },
  target: { label: 'Об’єкт', shape: '<path d="m12 3 9 9-9 9-9-9Z"/><circle cx="12" cy="12" r="2"/>' },
  explosion: { label: 'Вибух', shape: '<path d="m12 2 2.5 6 6-2.5-2.5 6 4 4-6 .5-2 6-4-5-6 2 2-6-4-4 6-.5Z"/>' },
  impact: { label: 'Влучання', shape: '<circle cx="12" cy="12" r="8"/><path d="m8 8 8 8m0-8-8 8"/>' },
  airdefenseaction: { label: 'Подія ППО', shape: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z"/><path d="m8 12 3 3 5-6"/>' },
  launch: { label: 'Запуск', shape: '<path d="M12 17V3m-5 5 5-5 5 5M4 16v5h16v-5"/>' },
  takeoff: { label: 'Зліт', shape: '<path d="M4 18 19 5M11 5h8v8M3 22h18"/>' },
}
const fallback = '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="2"/>'
const normalize = (name: string) => name.trim().toLowerCase().replace(/^ee_/, '').replaceAll('_', '')
const key = (name: string) => {
  const normalized = normalize(name)
  return entries[normalized] ? normalized : normalized.endsWith('s') ? normalized.slice(0, -1) : normalized
}

export function isRetiredEntity(name: string, table?: string): boolean {
  return [name, table ?? ''].some(value => ['track', 'tracks'].includes(normalize(value)))
}

export function entityLabel(name: string): string {
  return isRetiredEntity(name) ? 'Треки вимкнено' : entries[key(name)]?.label ?? silhouettes[key(name)]?.label ?? name
}

// Small, fixed silhouettes; no record text is interpolated into SVG markup.
const silhouettes: Record<string, { label: string; color: string; shape: string }> = {
  missile: { label: 'Ракета', color: '#ef704d', shape: '<path d="M12 2c-3 3-3 5-3 11l-3 5 4-1v3h4v-3l4 1-3-5c0-6 0-8-3-11Z"/><path d="M11 7h2" stroke="#fff"/>' },
  cruise: { label: 'Крилата ракета', color: '#38a4dc', shape: '<path d="m12 2 2 4v5l8 5v2l-8-2v4l3 2H7l3-2v-4l-8 2v-2l8-5V6Z"/>' },
  ballistic: { label: 'Балістична ракета', color: '#ac7deb', shape: '<path d="M12 1 9 7v10l-3 4 5-1h2l5 1-3-4V7Z"/><path d="M9 8h6" stroke="#fff"/><path d="m11 21 1 2 1-2" fill="#ffbe54"/>' },
  shahed: { label: 'Шахед', color: '#68aa77', shape: '<path d="m12 2 2 7 8 11-7-1-3 2-3-2-7 1 8-11Z"/><path d="M12 7v11M9 22h6" fill="none"/>' },
  geran: { label: 'Герань', color: '#94b85f', shape: '<path d="m12 2 3 9 7 8v3l-7-3-3 1-3-1-7 3v-3l7-8Z"/><path d="M12 7v10M9 22h6M3 18v3m18-3v3" fill="none"/>' },
  gerbera: { label: 'Гербера', color: '#c3a36a', shape: '<path d="m12 3 2 8 8 8-8-2v4h-4v-4l-8 2 8-8Z"/><path d="M12 8v11" fill="none"/>' },
  jet: { label: 'Реактивний БпЛА', color: '#34b6b3', shape: '<path d="m12 2 2 8 7 9-7-2v3h-4v-3l-7 2 7-9Z"/><path d="m10 21 2 3 2-3" fill="#fb923c" stroke="#c65b23"/><path d="M11 10h2v6h-2Z" fill="#164e63"/>' },
  uav: { label: 'БпЛА', color: '#6baa9b', shape: '<path d="m12 3 2 3v5l8 2v3l-8-1v4l3 2H7l3-2v-4l-8 1v-3l8-2V6Z"/>' },
  recon: { label: 'Розвідувальний БпЛА', color: '#80bbaa', shape: '<path d="m12 3 1 7 10 2v3l-10-1v5l4 1v2H7v-2l4-1v-5L1 15v-3l10-2Z"/><circle cx="12" cy="12" r="1" fill="#fff"/>' },
  fpv: { label: 'FPV-дрон', color: '#d7a94f', shape: '<path d="m5 5 14 14M19 5 5 19" fill="none" stroke-width="3"/><circle cx="5" cy="5" r="4"/><circle cx="19" cy="5" r="4"/><circle cx="5" cy="19" r="4"/><circle cx="19" cy="19" r="4"/><path d="M9 9h6v6H9Z" fill="#fff"/>' },
  aircraft: { label: 'Літак', color: '#7299cf', shape: '<path d="m12 2 2 3v5l9 6v2l-9-3v5l3 2H7l3-2v-5L1 18v-2l9-6V5Z"/>' },
  bomb: { label: 'Керована авіабомба', color: '#bd8d68', shape: '<path d="m8 2 4 2 4-2v5l-2 2c6 11 1 15-2 15S4 20 10 9L8 7Z"/><path d="M8 14h8" stroke="#ffda78"/>' },
}

/** Only explicit classification fields; never guess a weapon from message prose or a place name. */
export function entityObjectKind(values: Record<string, unknown> = {}): string | undefined {
  const text = ['model', 'targetModel', 'target_model', 'subtype', 'targetType', 'target_type', 'launchType', 'launch_type', 'aircraftType', 'aircraft_type'].map(field => typeof values[field] === 'string' ? values[field] : '').join(' ').toLowerCase()
  if (/jet|реактив|герань[- ]?[34]|shahed[- ]?238/.test(text)) return 'jet'
  if (/gerbera|гербер/.test(text)) return 'gerbera'
  if (/geran|герань/.test(text)) return 'geran'
  if (/shahed|шах[еі]д/.test(text)) return 'shahed'
  if (/ballistic|баліст|баллист/.test(text)) return 'ballistic'
  if (/cruise|крилат|крылат/.test(text)) return 'cruise'
  if (/missile|ракет/.test(text)) return 'missile'
  if (/fpv|фпв/.test(text)) return 'fpv'
  if (/recon|розвід/.test(text)) return 'recon'
  if (/uav|drone|бпла|дрон/.test(text)) return 'uav'
  if (/bomb|каб/.test(text)) return 'bomb'
  if (/aircraft|авіа|літак|ту[- ]|су[- ]|міг[- ]|миг[- ]|іл[- ]|а-50/.test(text)) return 'aircraft'
  return undefined
}

/** Transparent silhouette with a light contour for both map themes. */
export function entityIconSvg(name: string, values: Record<string, unknown> = {}): string {
  const kind = key(name)
  const object = entityObjectKind(values)
  const moving = kind === 'launch' || kind === 'takeoff'
  const selected = silhouettes[(kind === 'target' || moving ? object : undefined) ?? (kind === 'launch' ? 'missile' : kind === 'takeoff' ? 'aircraft' : kind)]
  const color = selected?.color ?? ({ alert: '#f5bf42', explosion: '#fb923c', impact: '#f07465', airdefenseaction: '#4db9c0' } as Record<string, string>)[kind] ?? '#94a3b8'
  const body = selected?.shape ?? entries[kind]?.shape ?? fallback
  const shape = moving ? `<path d="M2 22h12M3 18l3-2M1 14l3-2" fill="none" stroke="#94a3b8"/><g transform="translate(6 -1) rotate(32 12 12) scale(.8)">${body}<path d="m10 21 2 3 2-3" fill="#fb923c" stroke="#c65b23"/></g>` : body
  return `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="-2 -2 28 28"><g fill="${color}" stroke="#f8fafc" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">${shape}</g><g fill="${color}" stroke="#253448" stroke-width="1.15" stroke-linecap="round" stroke-linejoin="round">${shape}</g></svg>`
}

export const entityIconChoices = [...Object.keys(entries), ...Object.keys(silhouettes)]
