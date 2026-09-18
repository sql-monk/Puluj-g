import { linePath, scale } from './geometry'

/** A small trend line for a table cell: no axes, no hover (the table twin carries the numbers). */
export default function Sparkline({ values, color, width = 96, height = 20 }: { values: number[]; color: string; width?: number; height?: number }) {
  const n = values.length
  const max = Math.max(0, ...values)
  if (n === 0 || max === 0) return <svg width={width} height={height} aria-hidden />
  const x = n > 1 ? (i: number) => (i / (n - 1)) * (width - 2) + 1 : () => width / 2
  const y = scale(max, 1, height - 2, true)
  const xs = values.map((_, i) => x(i))
  const ys = values.map(y)
  const total = values.reduce((a, b) => a + b, 0)
  return (
    <svg width={width} height={height} role="img" aria-label={`динаміка: разом ${total.toLocaleString('uk-UA')}`}>
      <path d={`${linePath(xs, ys)}L${xs[n - 1]},${height - 1}L${xs[0]},${height - 1}Z`} fill={color} fillOpacity={0.18} />
      <path d={linePath(xs, ys)} fill="none" stroke={color} strokeWidth={1.5} strokeLinejoin="round" />
    </svg>
  )
}
