import { useState, type ReactNode } from 'react'
import type { TipState } from './hooks'

/** A chart with its table twin: the SVG is the default view, "таблиця" swaps in the same numbers as a <table>. */
export interface TableSpec {
  head: string[]
  rows: (string | number)[][]
}

export const EMPTY_TEXT = 'немає даних за період'

export default function ChartCard({
  title,
  subtitle,
  legend,
  table,
  empty,
  note,
  children,
  className,
}: {
  title: string
  subtitle?: string
  legend?: ReactNode
  table?: TableSpec
  /** Nothing to draw: the card says so instead of an empty frame. */
  empty?: boolean
  /** A footnote under the chart (what is not in it, totals). */
  note?: ReactNode
  children: ReactNode
  className?: string
}) {
  const [showTable, setShowTable] = useState(false)
  return (
    <section className={`flex flex-col gap-2 rounded-xl bg-white p-3 shadow-sm dark:bg-slate-900 ${className ?? ''}`} aria-label={title}>
      <header className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h3 className="text-sm font-semibold">{title}</h3>
        {subtitle && <span className="text-xs text-slate-500 dark:text-slate-400">{subtitle}</span>}
        {table && !empty && (
          <button
            type="button"
            className="ml-auto rounded border border-slate-300 px-1.5 py-0.5 text-[11px] text-slate-600 hover:bg-slate-100 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800"
            onClick={() => setShowTable((v) => !v)}
            aria-pressed={showTable}
          >
            {showTable ? 'графік' : 'таблиця'}
          </button>
        )}
      </header>
      {legend && !showTable && !empty && <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-600 dark:text-slate-300">{legend}</div>}
      {empty ? (
        <div className="py-6 text-center text-xs text-slate-500 dark:text-slate-400">{EMPTY_TEXT}</div>
      ) : showTable && table ? (
        <DataTable spec={table} />
      ) : (
        children
      )}
      {note && !empty && <div className="text-[11px] text-slate-500 dark:text-slate-400">{note}</div>}
    </section>
  )
}

export function DataTable({ spec }: { spec: TableSpec }) {
  return (
    <div className="max-h-80 overflow-auto">
      <table className="w-full text-xs tabular-nums">
        <thead className="sticky top-0 bg-white text-left text-slate-500 dark:bg-slate-900 dark:text-slate-400">
          <tr>
            {spec.head.map((h, i) => (
              <th key={i} className={`py-1 pr-2 font-medium ${i > 0 ? 'text-right' : ''}`}>
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {spec.rows.map((r, i) => (
            <tr key={i} className="border-t border-slate-100 dark:border-slate-800">
              {r.map((c, j) => (
                <td key={j} className={`py-1 pr-2 ${j > 0 ? 'text-right' : ''}`}>
                  {typeof c === 'number' ? c.toLocaleString('uk-UA') : c}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** A colour key beside a series name (a rect for bars/areas, a line for lines) — identity never rides on text colour. */
export function LegendItem({ color, label, line }: { color: string; label: string; line?: boolean }) {
  return (
    <span className="inline-flex items-center gap-1">
      <span aria-hidden className={line ? 'inline-block h-0.5 w-3' : 'inline-block h-2.5 w-2.5 rounded-sm'} style={{ background: color }} />
      {label}
    </span>
  )
}

/** The one floating readout of a chart; flips to the left of the cursor near the right edge. */
export function Tooltip({ tip, width }: { tip: TipState | null; width: number }) {
  if (!tip) return null
  const flip = width > 0 && tip.x > width * 0.6
  return (
    <div
      role="status"
      className="pointer-events-none absolute z-10 min-w-28 rounded-md border border-slate-200 bg-white/95 px-2 py-1.5 text-xs shadow-lg dark:border-slate-700 dark:bg-slate-800/95"
      style={{ left: flip ? undefined : tip.x + 12, right: flip ? width - tip.x + 12 : undefined, top: Math.max(0, tip.y - 8) }}
    >
      {tip.title && <div className="mb-0.5 text-[11px] text-slate-500 dark:text-slate-400">{tip.title}</div>}
      {tip.lines.map((l, i) => (
        <div key={i} className="flex items-center gap-1.5 whitespace-nowrap">
          {l.color && <span aria-hidden className="inline-block h-2 w-2 rounded-sm" style={{ background: l.color }} />}
          <span className="font-semibold tabular-nums">{l.value}</span>
          <span className="text-slate-500 dark:text-slate-400">{l.label}</span>
        </div>
      ))}
    </div>
  )
}

/** A headline figure: label above, the number, a note below. */
export function StatTile({ label, value, exactValue, note }: { label: string; value: string; exactValue?: string; note?: string }) {
  return (
    <div className="rounded-xl bg-white px-3 py-2 shadow-sm dark:bg-slate-900" aria-label={exactValue ? `${label}: ${exactValue}` : undefined}>
      <div className="text-[11px] text-slate-500 dark:text-slate-400">{label}</div>
      <div className="text-xl font-semibold tabular-nums">{value}</div>
      {note && <div className="text-[11px] text-slate-500 dark:text-slate-400">{note}</div>}
    </div>
  )
}
