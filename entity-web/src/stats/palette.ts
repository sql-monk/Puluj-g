import type { CSSProperties } from 'react'

/**
 * Chart colours as CSS custom properties on the page root (`chartVars`), one set per light / dark, so every chart
 * reads `var(--st-…)` and never a theme flag. Categories are keyed by code and always drawn in this order, so a
 * filter never repaints a survivor; the hues echo the map (drones green, missiles blue). Series colours for sources
 * are the reference categorical theme (eight slots, validated on both surfaces). One sequential blue ramp carries
 * every magnitude (heatmaps); text never wears a series colour.
 */
export const CATEGORY_ORDER = ['UAV', 'MISSILE', 'GUIDED_BOMB', 'AIRCRAFT', 'UNKNOWN'] as const

const CATEGORY_LIGHT: Record<string, string> = { UAV: '#15803d', MISSILE: '#2563eb', GUIDED_BOMB: '#ea580c', AIRCRAFT: '#7c3aed', UNKNOWN: '#b45309' }
const CATEGORY_DARK: Record<string, string> = { UAV: '#16a34a', MISSILE: '#3b82f6', GUIDED_BOMB: '#ea580c', AIRCRAFT: '#9085e9', UNKNOWN: '#d97706' }

const SERIES_LIGHT = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948']
const SERIES_DARK = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767']

// Sequential blue, light → dark (reference ramp, steps 100…700).
const RAMP = ['#cde2fb', '#b7d3f6', '#9ec5f4', '#86b6ef', '#6da7ec', '#5598e7', '#3987e5', '#2a78d6', '#256abf', '#1c5cab', '#184f95', '#104281', '#0d366b']

/** How many distinct series colours exist: more series fold into "other". */
export const SERIES_SLOTS = SERIES_LIGHT.length

/** The variables of one theme, to spread onto the page root's `style`. */
export function chartVars(dark: boolean): CSSProperties {
  const vars: Record<string, string> = {}
  for (const code of CATEGORY_ORDER) vars[`--st-cat-${code.toLowerCase()}`] = (dark ? CATEGORY_DARK : CATEGORY_LIGHT)[code]
  ;(dark ? SERIES_DARK : SERIES_LIGHT).forEach((c, i) => (vars[`--st-s${i}`] = c))
  // On dark surfaces the ramp runs the other way so that "more" is brighter; ink follows the fill's real lightness.
  RAMP.forEach((_, i) => {
    const fill = dark ? RAMP[RAMP.length - 1 - i] : RAMP[i]
    vars[`--st-seq-${i}`] = fill
    vars[`--st-seq-ink-${i}`] = RAMP.indexOf(fill) >= 7 ? '#ffffff' : '#0b0b0b'
  })
  vars['--st-accent'] = dark ? '#3b82f6' : '#2a78d6'
  vars['--st-accent-2'] = dark ? '#d95926' : '#eb6834'
  vars['--st-muted'] = dark ? '#64748b' : '#94a3b8'
  vars['--st-surface'] = dark ? 'var(--color-slate-900)' : 'var(--color-white)'
  return vars as CSSProperties
}

export function categoryColor(code: string): string {
  const known = (CATEGORY_ORDER as readonly string[]).includes(code) ? code : 'UNKNOWN'
  return `var(--st-cat-${known.toLowerCase()})`
}

/** The i-th series colour of the categorical theme (identity by position in a fixed list, never by rank). */
export function seriesColor(i: number): string {
  return `var(--st-s${i % SERIES_SLOTS})`
}

/** The single hue for one-series bars and columns. */
export const ACCENT = 'var(--st-accent)'
/** The second hue when a chart carries exactly two measures. */
export const ACCENT_2 = 'var(--st-accent-2)'
/** De-emphasis grey for sparklines, "other" rows and context marks. */
export const MUTED = 'var(--st-muted)'
/** The chart surface: the 2 px gap between stacked segments. */
export const SURFACE = 'var(--st-surface)'

/** Step 0…12 of the sequential ramp for a magnitude 0…max (square root, so small values still show); −1 for zero. */
export function seqStep(v: number, max: number): number {
  if (v <= 0 || max <= 0) return -1
  const t = Math.min(1, Math.sqrt(v / max))
  return Math.min(RAMP.length - 1, Math.round(t * (RAMP.length - 1)))
}

export function seqColor(v: number, max: number): string {
  const s = seqStep(v, max)
  return s < 0 ? 'transparent' : `var(--st-seq-${s})`
}

/** Ink for a label placed inside a filled cell: by the fill's step, not by guesswork. */
export function seqInk(v: number, max: number): string {
  const s = seqStep(v, max)
  return s < 0 ? 'currentColor' : `var(--st-seq-ink-${s})`
}
