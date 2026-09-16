const KYIV = 'Europe/Kyiv'

function parts(date: Date) {
  return Object.fromEntries(
    new Intl.DateTimeFormat('en-CA', { timeZone: KYIV, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
      .formatToParts(date)
      .filter((part) => part.type !== 'literal')
      .map((part) => [part.type, part.value]),
  ) as Record<string, string>
}

function offsetAt(date: Date): number {
  const named = new Intl.DateTimeFormat('en-US', { timeZone: KYIV, timeZoneName: 'longOffset' }).formatToParts(date).find((part) => part.type === 'timeZoneName')?.value
  const match = named?.match(/GMT([+-])(\d{2}):(\d{2})/)
  if (!match) return 0
  return (Number(match[2]) * 60 + Number(match[3])) * 60_000 * (match[1] === '+' ? 1 : -1)
}

/** A datetime-local value is a Kyiv wall-clock value even when the browser is elsewhere. */
export function toKyivInput(date: Date): string {
  const p = parts(date)
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`
}

/** Returns null for a DST gap; an ambiguous hour resolves to its later (standard-time) offset. */
export function parseKyivInput(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value)
  if (!match) return null
  const wall = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), Number(match[4]), Number(match[5]))
  let candidate = new Date(wall - offsetAt(new Date(wall)))
  candidate = new Date(wall - offsetAt(candidate))
  // On a repeated hour select the later instant so the displayed offset is deterministic.
  const later = new Date(candidate.getTime() + 3600_000)
  if (toKyivInput(later) === value) candidate = later
  return toKyivInput(candidate) === value ? candidate : null
}

export function kyivLabel(date: Date): string {
  return new Intl.DateTimeFormat('uk-UA', { timeZone: KYIV, day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZoneName: 'shortOffset' }).format(date)
}
