import { useMemo } from 'react'
import { api } from '../../api/client'
import type { StatsSourcesDto } from '../../api/types'
import ChartCard, { LegendItem, StatTile } from '../charts/ChartCard'
import Columns from '../charts/Columns'
import { foldSeries } from '../charts/geometry'
import { MUTED, SERIES_SLOTS, seriesColor } from '../palette'
import { bucketLabel, bucketTitle, compact, pct, perBucket, type Period } from '../period'
import { SectionShell, useSection } from '../section'
import SourcesTable from '../SourcesTable'
import type { DataQuery } from '../../public/query'
import FilterMeta from '../FilterMeta'

/** Who reported: the table of sources and their messages over time. */
export default function SourcesTab({ period, filter }: { period: Period; filter: DataQuery }) {
  const state = useSection(api.stats.sources, period, filter)
  return <SectionShell {...state}>{(data) => <Sources data={data} />}</SectionShell>
}

function Sources({ data }: { data: StatsSourcesDto }) {
  const p = data.period
  const labels = useMemo(() => p.bucketStarts.map((b) => bucketLabel(b, p.bucket)), [p])
  const titles = useMemo(() => p.bucketStarts.map((b) => bucketTitle(b, p.bucket)), [p])
  // Busiest sources first (the server's order); beyond the palette they fold into one grey "other" series.
  const series = useMemo(
    () =>
      foldSeries(
        data.sources.map((s, i) => ({ key: s.code, label: s.name, color: seriesColor(i), values: s.series })),
        SERIES_SLOTS,
        (values) => ({ key: 'other', label: 'інші', color: MUTED, values }),
      ),
    [data],
  )
  const empty = data.messages === 0
  return (
    <>
      <FilterMeta meta={data.filters} />
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <StatTile label="Повідомлень" value={compact(data.messages)} exactValue={`${data.messages.toLocaleString('uk-UA')} ревізій за publishedAt`} note={`з ${data.sources.length} джерел`} />
        <StatTile label="Оброблено" value={pct(data.processed, data.messages)} exactValue={`${data.processed.toLocaleString('uk-UA')} з ${data.messages.toLocaleString('uk-UA')} ревізій за publishedAt`} note={`${compact(data.processed)} повідомлень`} />
        <StatTile label="З розпізнаними фактами" value={pct(data.withTargets, data.processed)} exactValue={`${data.withTargets.toLocaleString('uk-UA')} з ${data.processed.toLocaleString('uk-UA')} ревізій за publishedAt`} note={`${compact(data.withTargets)} з оброблених`} />
        <StatTile label="Фактів про цілі" value={compact(data.targets)} exactValue={`${data.targets.toLocaleString('uk-UA')} канонічних фактів за ${data.factFilters.timeBasis}`} note={`без повторів; популяція: ${data.factFilters.population}`} />
      </div>

      <ChartCard title="Джерела" subtitle="raw-ревізії за publishedAt; факти в окремій observedAt-колонці" empty={empty}>
        <SourcesTable sources={data.sources} />
      </ChartCard>

      <ChartCard
        title="Повідомлення за джерелами"
        subtitle={`повідомлень ${perBucket(p.bucket)}, за часом публікації`}
        empty={empty}
        legend={series.map((s) => (
          <LegendItem key={s.key} color={s.color} label={s.label} />
        ))}
        table={{ head: ['Час', ...series.map((s) => s.label), 'Разом'], rows: titles.map((t, i) => [t, ...series.map((s) => s.values[i] ?? 0), series.reduce((n, s) => n + (s.values[i] ?? 0), 0)]) }}
      >
        <Columns labels={labels} titles={titles} series={series} valueLabel="повідомлень" height={240} />
      </ChartCard>
    </>
  )
}
