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
  return isRetiredEntity(name) ? 'Треки вимкнено' : entries[key(name)]?.label ?? name
}

/** Fixed, self-contained SVG for use as an image. Custom SVG never enters the document's markup. */
export function entityIconSvg(name: string): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 32 32"><rect x="1" y="1" width="30" height="30" rx="9" fill="#fff" stroke="#475569" stroke-width="1.5"/><g transform="translate(4 4)" fill="none" stroke="#0f172a" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${entries[key(name)]?.shape ?? fallback}</g></svg>`
}

export const entityIconChoices = Object.keys(entries)
