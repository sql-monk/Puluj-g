import { useState, type KeyboardEvent, type PointerEvent } from 'react'
import { seqColor, seqInk } from '../palette'
import { Tooltip } from './ChartCard'
import { useWidth } from './hooks'

const fmt = (v: number) => v.toLocaleString('uk-UA')

/**
 * A grid of magnitudes on one sequential hue: rows × columns with a value inside every cell that fits. Zero cells
 * stay on the surface (a hairline outline only). One cursor cell from the pointer or the arrow keys on the focused
 * SVG; one tooltip.
 */
export default function Heatmap({
  rows,
  cols,
  values,
  cellHeight = 20,
  labelWidth = 120,
  format = fmt,
  title,
  rotateCols = false,
  ariaLabel = 'теплова карта',
}: {
  rows: string[]
  cols: string[]
  values: number[][]
  cellHeight?: number
  labelWidth?: number
  format?: (v: number) => string
  title: (r: number, c: number) => string
  /** Long column names: drawn at 40° in a taller header. */
  rotateCols?: boolean
  ariaLabel?: string
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [cursor, setCursor] = useState<[number, number] | null>(null)
  const max = Math.max(0, ...values.flat())
  const padT = rotateCols ? 70 : 18
  // Rotated headers lean to the right past the last column: leave them room instead of clipping.
  const padR = rotateCols ? 36 : 0
  const cellW = cols.length > 0 ? Math.max(8, (width - labelWidth - padR) / cols.length) : 0
  const height = padT + rows.length * cellHeight + 2
  const colEvery = rotateCols ? 1 : Math.max(1, Math.ceil(36 / cellW))
  const svgW = Math.max(width, labelWidth + cellW * cols.length)
  const at = (r: number, c: number) => ({ x: labelWidth + cellW * c, y: padT + cellHeight * r })
  const onPointerMove = (e: PointerEvent<SVGSVGElement>) => {
    const b = e.currentTarget.getBoundingClientRect()
    const c = Math.floor((e.clientX - b.left - labelWidth) / cellW)
    const r = Math.floor((e.clientY - b.top - padT) / cellHeight)
    setCursor(c >= 0 && c < cols.length && r >= 0 && r < rows.length ? [r, c] : null)
  }
  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    const [r, c] = cursor ?? [-1, -1]
    let next: [number, number] | null
    switch (e.key) {
      case 'ArrowLeft':
        next = cursor ? [r, Math.max(0, c - 1)] : [0, 0]
        break
      case 'ArrowRight':
        next = cursor ? [r, Math.min(cols.length - 1, c + 1)] : [0, 0]
        break
      case 'ArrowUp':
        next = cursor ? [Math.max(0, r - 1), c] : [0, 0]
        break
      case 'ArrowDown':
        next = cursor ? [Math.min(rows.length - 1, r + 1), c] : [0, 0]
        break
      case 'Escape':
        next = null
        break
      default:
        return
    }
    e.preventDefault()
    if (rows.length > 0 && cols.length > 0) setCursor(next)
  }
  const tip = cursor ? { x: at(cursor[0], cursor[1]).x + cellW / 2, y: at(cursor[0], cursor[1]).y, title: title(cursor[0], cursor[1]), lines: [{ value: format(values[cursor[0]]?.[cursor[1]] ?? 0), label: '' }] } : null
  return (
    <div ref={ref} className="relative overflow-x-auto" data-chart>
      {width > 0 && (
        <svg
          width={svgW}
          height={height}
          role="img"
          aria-label={`${ariaLabel}: ${rows.length} × ${cols.length}; стрілки читають по клітинці`}
          tabIndex={0}
          className="text-[10px] outline-none focus-visible:ring-2 focus-visible:ring-blue-600"
          onPointerMove={onPointerMove}
          onPointerLeave={() => setCursor(null)}
          onKeyDown={onKeyDown}
          onBlur={() => setCursor(null)}
        >
          {cols.map((c, j) =>
            j % colEvery === 0 ? (
              rotateCols ? (
                <text key={j} transform={`translate(${labelWidth + cellW * j + cellW / 2 + 4},${padT - 6}) rotate(-40)`} fill="currentColor" fillOpacity={0.75} className="text-[11px]">
                  {c}
                </text>
              ) : (
                <text key={j} x={labelWidth + cellW * j + cellW / 2} y={12} textAnchor="middle" fill="currentColor" fillOpacity={0.65}>
                  {c}
                </text>
              )
            ) : null,
          )}
          {rows.map((label, i) => (
            <g key={i}>
              <text x={labelWidth - 6} y={padT + cellHeight * i + cellHeight / 2 + 3.5} textAnchor="end" fill="currentColor" fillOpacity={0.85} className="text-[11px]">
                {label}
              </text>
              {cols.map((_, j) => {
                const v = values[i]?.[j] ?? 0
                const { x, y } = at(i, j)
                const hot = cursor?.[0] === i && cursor[1] === j
                return (
                  <g key={j}>
                    <rect x={x + 1} y={y + 1} width={cellW - 2} height={cellHeight - 2} rx={2} fill={seqColor(v, max)} stroke="currentColor" strokeOpacity={hot ? 0.9 : v > 0 ? 0 : 0.08} strokeWidth={hot ? 1.5 : 1} />
                    {v > 0 && cellW >= 30 && (
                      <text x={x + cellW / 2} y={y + cellHeight / 2 + 3.5} textAnchor="middle" fill={seqInk(v, max)} className="pointer-events-none tabular-nums">
                        {format(v)}
                      </text>
                    )}
                  </g>
                )
              })}
            </g>
          ))}
        </svg>
      )}
      <Tooltip tip={tip} width={width} />
    </div>
  )
}
