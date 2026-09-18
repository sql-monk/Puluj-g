import { ACCENT } from '../palette'
import Columns from './Columns'

/** Counts per ordinal bin (durations): one series of columns with the value written on top of each. */
export default function Histogram({ bins, valueLabel, height = 180 }: { bins: { key: string; label: string; count: number }[]; valueLabel: string; height?: number }) {
  return (
    <Columns
      labels={bins.map((b) => b.label)}
      titles={bins.map((b) => b.label)}
      series={[{ key: 'count', label: valueLabel, color: ACCENT, values: bins.map((b) => b.count) }]}
      valueLabel={valueLabel}
      height={height}
      showValues
    />
  )
}
