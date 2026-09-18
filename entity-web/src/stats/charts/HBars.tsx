import { Tooltip } from './ChartCard'
import { barPath, textWidth } from './geometry'
import { useTooltip, useWidth, type TipLine } from './hooks'

export interface BarRow {
  key: string
  label: string
  value: number
  /** Extra tooltip lines. */
  details?: TipLine[]
  /** A category colour drawn as a thin swatch before the label (identity without recolouring the bar). */
  swatch?: string
  /** The bar's own colour (an "other" row in grey); the chart's colour otherwise. */
  color?: string
}

const fmt = (v: number) => v.toLocaleString('uk-UA')

/**
 * Horizontal bars, one hue, sorted by the caller: labels on the left, the value and its share of the total at the
 * bar end (inside the bar in surface ink when the bar fills the row). Rows are 22 px, bars 14 px with a 4 px rounded
 * data end; the hit target is the whole row and every row is focusable.
 */
export default function HBars({
  rows,
  color,
  format = fmt,
  labelWidth = 150,
  total,
  showShare = true,
  ariaLabel = 'розподіл',
}: {
  rows: BarRow[]
  color: string
  format?: (v: number) => string
  labelWidth?: number
  /** Denominator of the shares (the sum of the rows by default — pass the real total when rows are a top-N). */
  total?: number
  showShare?: boolean
  ariaLabel?: string
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const { tip, show, hide } = useTooltip()
  const rowH = 22
  const barH = 14
  const padR = 8
  const max = Math.max(0, ...rows.map((x) => x.value))
  const sum = total ?? rows.reduce((n, x) => n + x.value, 0)
  const plotW = Math.max(0, width - labelWidth - padR)
  const height = rows.length * rowH + 4
  const share = (v: number) => (sum > 0 ? `${Math.round((v / sum) * 100)}%` : '')
  return (
    <div ref={ref} className="relative" data-chart>
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label={`${ariaLabel}: ${rows.length} рядків`} className="text-[11px]">
          {rows.map((x, i) => {
            const w = max > 0 ? (plotW * x.value) / max : 0
            const yTop = 2 + i * rowH
            const text = showShare && x.value > 0 ? `${format(x.value)} · ${share(x.value)}` : format(x.value)
            const tw = textWidth(text, 6.2)
            const outside = w + 6 + tw < plotW
            const inside = !outside && tw + 12 < w
            const lines: TipLine[] = [{ value: format(x.value), label: showShare ? `${share(x.value)} від усього` : '' }, ...(x.details ?? [])]
            return (
              <g key={x.key}>
                <rect x={0} y={yTop} width={width} height={rowH} fill="transparent" tabIndex={0} onPointerMove={(e) => show(e, x.label, lines)} onFocus={(e) => show(e, x.label, lines)} onPointerLeave={hide} onBlur={hide} className="outline-none focus-visible:fill-blue-600/10" />
                {x.swatch && <rect x={0} y={yTop + 5} width={3} height={barH - 2} rx={1} fill={x.swatch} />}
                <text x={x.swatch ? 7 : 0} y={yTop + rowH / 2 + 4} fill="currentColor" fillOpacity={0.85}>
                  {truncate(x.label, Math.floor(labelWidth / 6.3))}
                </text>
                <line x1={labelWidth} x2={labelWidth} y1={yTop} y2={yTop + rowH} stroke="currentColor" strokeOpacity={0.3} />
                <path d={barPath(labelWidth, yTop + (rowH - barH) / 2, w, barH, 4)} fill={x.color ?? color} />
                {outside && (
                  <text x={labelWidth + w + 5} y={yTop + rowH / 2 + 4} fill="currentColor" fillOpacity={0.75} className="tabular-nums">
                    {text}
                  </text>
                )}
                {inside && (
                  <text x={labelWidth + w - 5} y={yTop + rowH / 2 + 4} textAnchor="end" fill="#ffffff" className="pointer-events-none tabular-nums">
                    {text}
                  </text>
                )}
              </g>
            )
          })}
        </svg>
      )}
      <Tooltip tip={tip} width={width} />
    </div>
  )
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, Math.max(1, max - 1)) + '…'
}
