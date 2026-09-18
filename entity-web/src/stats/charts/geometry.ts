import type { StatsRouteDto } from '../../api/types'

/**
 * The arithmetic of the charts, without React or the DOM: axis ticks, the frame around a plot, column slots, stacks,
 * SVG paths, pointer → bucket, keyboard cursors, the route matrix. Tested on its own.
 */

/** Clean axis ticks: 0 and a few round steps, the last one at or above the maximum. */
export function niceTicks(max: number, count = 4): number[] {
  if (max <= 0) return [0]
  const raw = max / count
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? raw
  const ticks: number[] = []
  for (let v = 0; ; v += step) {
    ticks.push(Math.round(v * 1000) / 1000)
    if (v >= max - 1e-9) break
  }
  return ticks
}

/** How many axis labels fit: every k-th bucket gets one. */
export function labelEvery(count: number, maxLabels = 12): number {
  return Math.max(1, Math.ceil(count / maxLabels))
}

/** Rough text width for layout (system UI at 10–11 px averages ~6 px per glyph). */
export function textWidth(s: string, perChar = 6): number {
  return s.length * perChar
}

export interface Frame {
  width: number
  height: number
  padL: number
  padR: number
  padT: number
  padB: number
  plotW: number
  plotH: number
}

/** The plot area inside an SVG: the left band grows with the longest tick label, the bottom one holds the x labels. */
export function frame(width: number, height: number, tickLabels: string[], opts: { padT?: number; padR?: number; padB?: number; minPadL?: number } = {}): Frame {
  const padT = opts.padT ?? 8
  const padR = opts.padR ?? 8
  const padB = opts.padB ?? 24
  const padL = Math.max(opts.minPadL ?? 36, ...tickLabels.map((t) => textWidth(t) + 10))
  return { width, height, padL, padR, padT, padB, plotW: Math.max(0, width - padL - padR), plotH: Math.max(0, height - padT - padB) }
}

/** Column slots: `slot` per bucket, the column itself capped at `maxBar` with a 2 px gap on either side. */
export function slots(n: number, plotW: number, maxBar = 24): { slot: number; bar: number } {
  const slot = n > 0 ? plotW / n : 0
  return { slot, bar: n > 0 ? Math.min(maxBar, Math.max(2, slot - 2)) : 0 }
}

/** A linear scale from a domain [0, max] onto pixels, `flip` for y (pixels grow downwards). */
export function scale(max: number, from: number, length: number, flip = false): (v: number) => number {
  const top = max > 0 ? max : 1
  return (v) => (flip ? from + length * (1 - v / top) : from + (length * v) / top)
}

/** Cumulative layers of stacked series: layers[s][i] = [bottom, top] of series s at bucket i; totals per bucket. */
export function stackLayers(series: number[][]): { totals: number[]; layers: [number, number][][] } {
  const n = series[0]?.length ?? 0
  const totals = new Array<number>(n).fill(0)
  const layers = series.map((values) =>
    values.map((v, i) => {
      const y0 = totals[i]
      totals[i] = y0 + Math.max(0, v)
      return [y0, totals[i]] as [number, number]
    }),
  )
  return { totals, layers }
}

/** A closed area between two polylines (top left → right, bottom right → left). */
export function areaPath(xs: number[], tops: number[], bottoms: number[]): string {
  if (xs.length === 0) return ''
  const up = xs.map((x, i) => `${i === 0 ? 'M' : 'L'}${r(x)},${r(tops[i])}`).join('')
  const down = xs
    .map((x, i) => `L${r(x)},${r(bottoms[i])}`)
    .reverse()
    .join('')
  return `${up}${down}Z`
}

export function linePath(xs: number[], ys: number[]): string {
  return xs.map((x, i) => `${i === 0 ? 'M' : 'L'}${r(x)},${r(ys[i])}`).join('')
}

function r(v: number): number {
  return Math.round(v * 10) / 10
}

/** A column segment: square at the bottom, the data end rounded by `radius` (only the topmost segment of a stack). */
export function columnPath(x: number, y: number, w: number, h: number, radius: number): string {
  const rr = Math.min(radius, w / 2, h)
  if (h <= 0 || w <= 0) return ''
  if (rr <= 0) return `M${x},${y}h${w}v${h}h${-w}Z`
  return `M${x},${y + h}v${-(h - rr)}a${rr},${rr} 0 0 1 ${rr},${-rr}h${w - 2 * rr}a${rr},${rr} 0 0 1 ${rr},${rr}v${h - rr}Z`
}

/** A horizontal bar: square at the baseline (left), the data end rounded. */
export function barPath(x: number, y: number, w: number, h: number, radius: number): string {
  const rr = Math.min(radius, h / 2, w)
  if (w <= 0 || h <= 0) return ''
  if (rr <= 0) return `M${x},${y}h${w}v${h}h${-w}Z`
  return `M${x},${y}h${w - rr}a${rr},${rr} 0 0 1 ${rr},${rr}v${h - 2 * rr}a${rr},${rr} 0 0 1 ${-rr},${rr}h${-(w - rr)}Z`
}

/** Bucket under a pointer x (SVG coordinates); null outside the plot. */
export function indexAt(px: number, padL: number, slot: number, n: number): number | null {
  if (n === 0 || slot <= 0) return null
  const i = Math.floor((px - padL) / slot)
  return i >= 0 && i < n ? i : null
}

/** Move a keyboard cursor by `delta`, clamped to [0, n): from nothing, Right starts at the first bucket and Left at the last. */
export function stepCursor(cursor: number | null, delta: number, n: number): number | null {
  if (n === 0) return null
  if (cursor === null) return delta >= 0 ? 0 : n - 1
  return Math.min(n - 1, Math.max(0, cursor + delta))
}

/** Top origins × top destinations (by their totals) as a matrix; the rest of the pairs live in the list and the table. */
export function routeMatrix(routes: StatsRouteDto[], top: number): { rows: { id: number; name: string }[]; cols: { id: number; name: string }[]; values: number[][] } {
  const fromTotals = new Map<number, { name: string; n: number }>()
  const toTotals = new Map<number, { name: string; n: number }>()
  for (const x of routes) {
    fromTotals.set(x.fromId, { name: x.fromName, n: (fromTotals.get(x.fromId)?.n ?? 0) + x.count })
    toTotals.set(x.toId, { name: x.toName, n: (toTotals.get(x.toId)?.n ?? 0) + x.count })
  }
  const pickTop = (m: Map<number, { name: string; n: number }>) =>
    [...m.entries()]
      .sort((a, b) => b[1].n - a[1].n || a[1].name.localeCompare(b[1].name, 'uk'))
      .slice(0, top)
      .map(([id, v]) => ({ id, name: v.name }))
  const rows = pickTop(fromTotals)
  const cols = pickTop(toTotals)
  const values = rows.map((row) => cols.map((c) => routes.find((x) => x.fromId === row.id && x.toId === c.id)?.count ?? 0))
  return { rows, cols, values }
}

/** The first `slots` series stay, the rest are summed into one "other" row (identity beyond the palette is not drawn). */
export function foldSeries<T extends { values: number[] }>(series: T[], slots: number, other: (values: number[]) => T): T[] {
  if (series.length <= slots) return series
  const keep = series.slice(0, slots)
  const n = series[0]?.values.length ?? 0
  const rest = new Array<number>(n).fill(0)
  for (const s of series.slice(slots)) s.values.forEach((v, i) => (rest[i] += v))
  return [...keep, other(rest)]
}
