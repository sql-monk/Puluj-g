import { describe, expect, it } from 'vitest'
import { parseKyivInput, toKyivInput } from './kyivTime'

describe('Kyiv wall-clock inputs', () => {
  it('formats Kyiv independently from the browser timezone', () => {
    expect(toKyivInput(new Date('2026-01-15T10:30:00.000Z'))).toBe('2026-01-15T12:30')
  })

  it('rejects Kyiv DST gaps and round-trips an ambiguous autumn hour', () => {
    expect(parseKyivInput('2026-03-29T03:30')).toBeNull()
    const ambiguous = parseKyivInput('2026-10-25T03:30')
    expect(ambiguous).not.toBeNull()
    expect(toKyivInput(ambiguous!)).toBe('2026-10-25T03:30')
  })
})
