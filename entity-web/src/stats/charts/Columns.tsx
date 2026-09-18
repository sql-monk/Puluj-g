import type { PointerEvent } from 'react'
import { SURFACE } from '../palette'
import { Tooltip } from './ChartCard'
import { columnPath, frame, indexAt, labelEvery, linePath, niceTicks, scale, slots, stackLayers } from './geometry'
import { useBucketCursor, useWidth, type TipLine } from './hooks'

export interface Series {
  key: string
  label: string
  color: string
  values: number[]
}

/** A second measure on its own small panel under the columns (shared x, own scale) — never a second y axis. */
export interface Secondary {
  label: string
  color: string
  values: number[]
  format?: (v: number) => string
}

const fmt = (v: number) => v.toLocaleString('uk-UA')

/**
 * Columns over an ordered axis (time buckets, hours of the day, bins), stacked when there are several series. Axes,
 * gridlines, x labels that thin out to fit, columns capped at 24 px with a 2 px surface gap between segments, the top
 * of every column rounded. One cursor per bucket: the pointer or ←/→ on the focused SVG, one tooltip for the whole
 * stack (zeros skipped, the total when stacked, then the extra lines).
 */
export default function Columns({
  labels,
  titles,
  series,
  height = 200,
  valueLabel,
  format = fmt,
  extra,
  secondary,
  showValues = false,
  ariaLabel,
}: {
  labels: string[]
  titles: string[]
  series: Series[]
  height?: number
  /** What the y axis measures, for the aria-label ("фактів", "годин під тривогою"). */
  valueLabel: string
  format?: (v: number) => string
  /** More tooltip lines for a bucket (measures that are not drawn). */
  extra?: (i: number) => TipLine[]
  secondary?: Secondary
  /** Direct value labels on top of every column (small ordinal charts). */
  showValues?: boolean
  ariaLabel?: string
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const n = labels.length
  const { cursor, setCursor, onKeyDown, onBlur } = useBucketCursor(n)
  const { totals, layers } = stackLayers(series.map((s) => s.values))
  const max = Math.max(0, ...totals)
  const ticks = niceTicks(max)
  const secH = secondary ? 56 : 0
  const f = frame(width, height - secH, ticks.map(format), { padB: secondary ? 4 : 24, padT: showValues ? 16 : 8 })
  const { slot, bar } = slots(n, f.plotW)
  const y = scale(ticks[ticks.length - 1], f.padT, f.plotH, true)
  const every = labelEvery(n, Math.max(2, Math.floor(f.plotW / 56)))
  const xLabelY = height - 8
  // Secondary panel: its own [0, max] under the main plot, 24 px above the x labels.
  const sec = secondary ? { top: f.padT + f.plotH + 12, h: secH - 12 - 24 } : null
  const secMax = secondary ? Math.max(0, ...secondary.values) : 0
  const secY = sec ? scale(secMax, sec.top, sec.h, true) : () => 0
  const secFormat = secondary?.format ?? fmt

  const lines = (i: number): TipLine[] => [
    ...series.map((s) => ({ value: format(s.values[i] ?? 0), label: s.label, color: s.color })).filter((_, j) => (series[j].values[i] ?? 0) > 0),
    ...(series.length > 1 ? [{ value: format(totals[i]), label: 'разом' }] : []),
    ...(secondary ? [{ value: secFormat(secondary.values[i] ?? 0), label: secondary.label, color: secondary.color }] : []),
    ...(extra?.(i) ?? []),
  ]
  const onPointerMove = (e: PointerEvent<SVGSVGElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    setCursor(indexAt(e.clientX - r.left, f.padL, slot, n))
  }
  const cx = (i: number) => f.padL + slot * i + slot / 2
  const tip = cursor === null ? null : { x: cx(cursor), y: y(totals[cursor]), title: titles[cursor], lines: lines(cursor) }
  return (
    <div ref={ref} className="relative" data-chart>
      {width > 0 && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={`${ariaLabel ?? valueLabel}: ${n} значень; стрілки ← → читають по одному`}
          tabIndex={0}
          className="text-[10px] outline-none focus-visible:ring-2 focus-visible:ring-blue-600"
          onPointerMove={onPointerMove}
          onPointerLeave={() => setCursor(null)}
          onKeyDown={onKeyDown}
          onBlur={onBlur}
        >
          {ticks.map((t) => (
            <g key={t}>
              <line x1={f.padL} x2={width - f.padR} y1={y(t)} y2={y(t)} stroke="currentColor" strokeOpacity={0.12} />
              <text x={f.padL - 4} y={y(t) + 3} textAnchor="end" fill="currentColor" fillOpacity={0.65}>
                {format(t)}
              </text>
            </g>
          ))}
          {cursor !== null && <rect x={f.padL + slot * cursor} y={f.padT} width={slot} height={height - f.padT - 20} fill="currentColor" fillOpacity={0.06} />}
          {labels.map((label, i) => {
            const x = f.padL + slot * i + (slot - bar) / 2
            const last = series.findLastIndex((s) => (s.values[i] ?? 0) > 0)
            return (
              <g key={i}>
                {series.map((s, j) => {
                  const [b, t] = layers[j][i]
                  return t > b ? <path key={s.key} d={columnPath(x, y(t), bar, y(b) - y(t), j === last ? 3 : 0)} fill={s.color} stroke={SURFACE} strokeWidth={1} /> : null
                })}
                {showValues && totals[i] > 0 && (
                  <text x={cx(i)} y={y(totals[i]) - 4} textAnchor="middle" fill="currentColor" fillOpacity={0.75} className="tabular-nums">
                    {format(totals[i])}
                  </text>
                )}
                {i % every === 0 && (
                  <text x={cx(i)} y={xLabelY} textAnchor="middle" fill="currentColor" fillOpacity={0.65}>
                    {label}
                  </text>
                )}
              </g>
            )
          })}
          <line x1={f.padL} x2={width - f.padR} y1={y(0)} y2={y(0)} stroke="currentColor" strokeOpacity={0.3} />
          {secondary && sec && (
            <g>
              <line x1={f.padL} x2={width - f.padR} y1={secY(0)} y2={secY(0)} stroke="currentColor" strokeOpacity={0.3} />
              <text x={f.padL - 4} y={secY(secMax) + 3} textAnchor="end" fill="currentColor" fillOpacity={0.65}>
                {secFormat(secMax)}
              </text>
              <text x={f.padL - 4} y={secY(0) + 3} textAnchor="end" fill="currentColor" fillOpacity={0.65}>
                0
              </text>
              <path d={linePath(labels.map((_, i) => cx(i)), secondary.values.map(secY))} fill="none" stroke={secondary.color} strokeWidth={2} strokeLinejoin="round" />
              {cursor !== null && <circle cx={cx(cursor)} cy={secY(secondary.values[cursor] ?? 0)} r={4} fill={secondary.color} stroke={SURFACE} strokeWidth={2} />}
            </g>
          )}
        </svg>
      )}
      <Tooltip tip={tip} width={width} />
    </div>
  )
}
