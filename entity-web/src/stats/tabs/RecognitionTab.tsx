import { useMemo } from 'react'
import { api } from '../../api/client'
import type { StatsRecognitionDto, StatsSliceDto } from '../../api/types'
import ChartCard, { StatTile } from '../charts/ChartCard'
import Columns from '../charts/Columns'
import HBars from '../charts/HBars'
import { ACCENT } from '../palette'
import { bucketLabel, bucketTitle, compact, num, pct, percentValue, perBucket, type Period } from '../period'
import { SectionShell, useSection } from '../section'
import type { DataQuery } from '../../public/query'
import FilterMeta from '../FilterMeta'

/** How the pipeline read the period: what kinds of events, how the facts were identified, how sure and how precise, and where nothing was found. */
export default function RecognitionTab({ period, filter }: { period: Period; filter: DataQuery }) {
  const state = useSection(api.stats.recognition, period, filter)
  return <SectionShell {...state}>{(data) => <Recognition data={data} />}</SectionShell>
}

const percent = percentValue

function Recognition({ data }: { data: StatsRecognitionDto }) {
  const p = data.period
  const labels = useMemo(() => p.bucketStarts.map((b) => bucketLabel(b, p.bucket)), [p])
  const titles = useMemo(() => p.bucketStarts.map((b) => bucketTitle(b, p.bucket)), [p])
  const without = data.processedByBucket.map((n, i) => n - (data.withTargetsByBucket[i] ?? 0))
  const share = data.processedByBucket.map((n, i) => (n > 0 ? (without[i] / n) * 100 : 0))
  const totalWithout = data.processed - data.withTargets
  return (
    <>
      <FilterMeta meta={data.filters} />
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <StatTile label="Фактів про цілі" value={compact(data.targets)} exactValue={data.targets.toLocaleString('uk-UA')} note="без повторів між джерелами" />
        <StatTile label="Оброблено повідомлень" value={compact(data.processed)} exactValue={data.processed.toLocaleString('uk-UA')} note="за часом публікації" />
        <StatTile label="З фактами" value={pct(data.withTargets, data.processed)} exactValue={`${data.withTargets.toLocaleString('uk-UA')} з ${data.processed.toLocaleString('uk-UA')}`} note={`${compact(data.withTargets)} повідомлень`} />
        <StatTile label="Без фактів" value={pct(totalWithout, data.processed)} exactValue={`${totalWithout.toLocaleString('uk-UA')} з ${data.processed.toLocaleString('uk-UA')}`} note={`${compact(totalWithout)} повідомлень`} />
      </div>

      <ChartCard
        title="Повідомлення без фактів"
        subtitle={`частка оброблених повідомлень, у яких не розпізнано жодного факту, ${perBucket(p.bucket)}`}
        empty={data.processed === 0}
        table={{ head: ['Час', 'Оброблено', 'Без фактів', 'Частка'], rows: titles.map((t, i) => [t, data.processedByBucket[i], without[i], percent(share[i])]) }}
      >
        <Columns
          labels={labels}
          titles={titles}
          series={[{ key: 'share', label: 'без фактів', color: ACCENT, values: share }]}
          valueLabel="% повідомлень без фактів"
          format={percent}
          height={200}
          extra={(i) => [
            { value: num(without[i]), label: 'без фактів' },
            { value: num(data.processedByBucket[i]), label: 'оброблено' },
          ]}
        />
      </ChartCard>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <SliceCard title="Типи подій" subtitle="усі події, не лише цілі" slices={data.eventTypes} />
        <SliceCard title="Метод ідентифікації" subtitle="факти" slices={data.methods} />
        <SliceCard title="Впевненість" subtitle="факти" slices={data.confidence} />
        <SliceCard title="Точність локації" subtitle="факти" slices={data.locationKinds} />
      </div>
    </>
  )
}

function SliceCard({ title, subtitle, slices }: { title: string; subtitle: string; slices: StatsSliceDto[] }) {
  const sorted = [...slices].sort((a, b) => b.count - a.count)
  return (
    <ChartCard title={title} subtitle={subtitle} empty={slices.length === 0} table={{ head: ['Значення', 'К-сть'], rows: sorted.map((s) => [s.label, s.count]) }}>
      <HBars rows={sorted.map((s) => ({ key: s.key, label: s.label, value: s.count }))} color={ACCENT} labelWidth={120} ariaLabel={title} />
    </ChartCard>
  )
}
