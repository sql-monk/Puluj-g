import { useMemo } from 'react'
import { api } from '../../api/client'
import type { StatsAlertsDto } from '../../api/types'
import ChartCard, { LegendItem, StatTile } from '../charts/ChartCard'
import Columns from '../charts/Columns'
import HBars from '../charts/HBars'
import Histogram from '../charts/Histogram'
import { ACCENT, ACCENT_2 } from '../palette'
import { HOURS, bucketLabel, bucketTitle, compact, dayTitle, hoursText, num, perBucket, type Period } from '../period'
import { SectionShell, useSection } from '../section'
import type { DataQuery } from '../../public/query'
import FilterMeta from '../FilterMeta'

/** Region-level air-raid alerts: hours under alert over time, per region, durations, hour of day, the heaviest days. */
export default function AlertsTab({ period, filter }: { period: Period; filter: DataQuery }) {
  const state = useSection(api.stats.alerts, period, filter)
  return <SectionShell {...state}>{(data) => <Alerts data={data} />}</SectionShell>
}

const hours = (v: number) => `${v.toLocaleString('uk-UA', { maximumFractionDigits: 1 })} год`

function Alerts({ data }: { data: StatsAlertsDto }) {
  const p = data.period
  const labels = useMemo(() => p.bucketStarts.map((b) => bucketLabel(b, p.bucket)), [p])
  const titles = useMemo(() => p.bucketStarts.map((b) => bucketTitle(b, p.bucket)), [p])
  const empty = data.alerts === 0
  return (
    <>
      <FilterMeta meta={data.filters} />
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <StatTile label="Тривог по областях" value={compact(data.alerts)} note="область або Київ; громади не рахуються" />
        <StatTile label="Область-години під тривогою" value={hoursText(data.alertHours)} note="сума перекритих інтервалів по областях" />
        <StatTile label="Областей у тривозі" value={String(data.byRegion.length)} note="хоч раз за період" />
        <StatTile label="Тривали наприкінці періоду" value={String(data.openAtEnd)} note="не завершені на кінець періоду" />
      </div>

      <ChartCard
        title="Під тривогою"
        subtitle={`область-годин ${perBucket(p.bucket)} (сума по областях); нижче — скільки тривог оголошено`}
        empty={empty}
        legend={
          <>
            <LegendItem color={ACCENT} label="область-годин під тривогою" />
            <LegendItem color={ACCENT_2} label="оголошено тривог" line />
          </>
        }
        table={{ head: ['Час', 'Область-годин', 'Оголошено'], rows: titles.map((t, i) => [t, Math.round(data.hoursByBucket[i] * 10) / 10, data.declaredByBucket[i]]) }}
      >
        <Columns
          labels={labels}
          titles={titles}
          series={[{ key: 'hours', label: 'годин під тривогою', color: ACCENT, values: data.hoursByBucket }]}
          valueLabel="область-годин під тривогою"
          format={hours}
          height={260}
          secondary={{ label: 'оголошено тривог', color: ACCENT_2, values: data.declaredByBucket }}
        />
      </ChartCard>

      <div className="grid gap-3 lg:grid-cols-2">
        <ChartCard title="По областях" subtitle="годин під тривогою за період; топ-15" empty={data.byRegion.length === 0} table={{ head: ['Область', 'Годин', 'Тривог'], rows: data.byRegion.map((a) => [a.name, Math.round(a.hours * 10) / 10, a.count]) }}>
          <HBars rows={data.byRegion.slice(0, 15).map((a) => ({ key: String(a.id), label: a.name, value: a.hours, details: [{ value: num(a.count), label: 'тривог' }, { value: hoursText(a.count > 0 ? a.hours / a.count : 0), label: 'у середньому' }] }))} color={ACCENT} format={hoursText} total={data.alertHours} labelWidth={170} ariaLabel="години під тривогою за областями" />
        </ChartCard>
        <ChartCard title="Тривалість" subtitle="завершені тривоги по областях" empty={data.durations.every((d) => d.count === 0)} table={{ head: ['Тривалість', 'Тривог'], rows: data.durations.map((d) => [d.label, d.count]) }}>
          <Histogram bins={data.durations} valueLabel="тривог" />
        </ChartCard>
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        <ChartCard title="Коли оголошують" subtitle="тривог оголошено за годиною доби, київський час" empty={data.declaredByHour.every((n) => n === 0)} table={{ head: ['Година', 'Оголошено'], rows: data.declaredByHour.map((n, h) => [`${HOURS[h]}:00`, n]) }}>
          <Columns labels={HOURS} titles={HOURS.map((h, i) => `${h}:00–${HOURS[(i + 1) % 24]}:00`)} series={[{ key: 'declared', label: 'оголошено', color: ACCENT, values: data.declaredByHour }]} valueLabel="тривог оголошено" height={180} />
        </ChartCard>
        <ChartCard title="Найважчі дні" subtitle="годин під тривогою за добу (сума по областях); топ-10" empty={data.topDays.length === 0} table={{ head: ['День', 'Годин', 'Оголошено'], rows: data.topDays.map((d) => [dayTitle(d.day), Math.round(d.hours * 10) / 10, d.count]) }}>
          <HBars rows={data.topDays.map((d) => ({ key: d.day, label: dayTitle(d.day), value: d.hours, details: [{ value: num(d.count), label: 'тривог оголошено' }] }))} color={ACCENT} format={hoursText} showShare={false} labelWidth={120} ariaLabel="найважчі дні" />
        </ChartCard>
      </div>
    </>
  )
}
