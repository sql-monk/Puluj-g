import type { PointerEvent } from 'react'
import { SURFACE } from '../palette'
import { Tooltip } from './ChartCard'
import Columns, { type Series } from './Columns'
import { areaPath, frame, indexAt, labelEvery, niceTicks, scale, slots, stackLayers } from './geometry'
import { useBucketCursor, useWidth, type TipLine } from './hooks'

const fmt = (v: number) => v.toLocaleString('uk-UA')

/**
 * Stacked areas over time buckets (one series = one category, fixed order and colour): the composition and the
 * trend in one picture. Axes and gridlines as in Columns; the cursor is a vertical hairline with a dot on every
 * layer top and one tooltip for the whole stack. With fewer than three buckets there is no area to speak of, so
 * the same data is drawn as stacked columns.
 */
export default function AreaStack({
  labels,
  titles,
  series,
  height = 220,
  valueLabel,
  format = fmt,
  extra,
}: {
  labels: string[]
  titles: string[]
  series: Series[]
  height?: number
  valueLabel: string
  format?: (v: number) => string
  extra?: (i: number) => TipLine[]
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const n = labels.length
  const { cursor, setCursor, onKeyDown, onBlur } = useBucketCursor(n)
  if (n < 3) return <Columns labels={labels} titles={titles} series={series} height={height} valueLabel={valueLabel} format={format} extra={extra} />
  const { totals, layers } = stackLayers(series.map((s) => s.values))
  const max = Math.max(0, ...totals)
  const ticks = niceTicks(max)
  const f = frame(width, height, ticks.map(format))
  const { slot } = slots(n, f.plotW)
  const y = scale(ticks[ticks.length - 1], f.padT, f.plotH, true)
  const cx = (i: number) => f.padL + slot * i + slot / 2
  const xs = labels.map((_, i) => cx(i))
  const every = labelEvery(n, Math.max(2, Math.floor(f.plotW / 56)))
  const lines = (i: number): TipLine[] => [
    ...series.map((s) => ({ value: format(s.values[i] ?? 0), label: s.label, color: s.color })).filter((_, j) => (series[j].values[i] ?? 0) > 0),
    ...(series.length > 1 ? [{ value: format(totals[i]), label: 'разом' }] : []),
    ...(extra?.(i) ?? []),
  ]
  const onPointerMove = (e: PointerEvent<SVGSVGElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    setCursor(indexAt(e.clientX - r.left, f.padL, slot, n))
  }
  const tip = cursor === null ? null : { x: cx(cursor), y: y(totals[cursor]), title: titles[cursor], lines: lines(cursor) }
  return (
    <div ref={ref} className="relative" data-chart>
      {width > 0 && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={`${valueLabel} за часом: ${n} значень; стрілки ← → читають по одному`}
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
          {series.map((s, j) => (
            <path key={s.key} d={areaPath(xs, layers[j].map(([, t]) => y(t)), layers[j].map(([b]) => y(b)))} fill={s.color} fillOpacity={0.85} stroke={SURFACE} strokeWidth={1} strokeLinejoin="round" />
          ))}
          {labels.map((label, i) =>
            i % every === 0 ? (
              <text key={i} x={cx(i)} y={height - 8} textAnchor="middle" fill="currentColor" fillOpacity={0.65}>
                {label}
              </text>
            ) : null,
          )}
          <line x1={f.padL} x2={width - f.padR} y1={y(0)} y2={y(0)} stroke="currentColor" strokeOpacity={0.3} />
          {cursor !== null && (
            <g>
              <line x1={cx(cursor)} x2={cx(cursor)} y1={f.padT} y2={y(0)} stroke="currentColor" strokeOpacity={0.4} strokeDasharray="2 2" />
              {series.map((s, j) => ((s.values[cursor] ?? 0) > 0 ? <circle key={s.key} cx={cx(cursor)} cy={y(layers[j][cursor][1])} r={3.5} fill={s.color} stroke={SURFACE} strokeWidth={2} /> : null))}
            </g>
          )}
        </svg>
      )}
      <Tooltip tip={tip} width={width} />
    </div>
  )
}
