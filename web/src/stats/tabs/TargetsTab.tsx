import { useMemo, useState } from 'react'
import { api } from '../../api/client'
import type { StatsTargetsDto } from '../../api/types'
import AreaStack from '../charts/AreaStack'
import ChartCard, { LegendItem, StatTile } from '../charts/ChartCard'
import { routeMatrix } from '../charts/geometry'
import HBars from '../charts/HBars'
import Heatmap from '../charts/Heatmap'
import { ACCENT, MUTED, categoryColor } from '../palette'
import { HOURS, WEEKDAYS, bucketLabel, bucketTitle, compact, num, perBucket, type Period } from '../period'
import { SectionShell, useSection } from '../section'
import type { DataQuery } from '../../public/query'
import FilterMeta from '../FilterMeta'

/** "What flew": composition over time, classes, regions, routes, hour × weekday. */
export default function TargetsTab({ period, filter }: { period: Period; filter: DataQuery }) {
  const state = useSection(api.stats.targets, period, filter)
  return <SectionShell {...state}>{(data) => <Targets data={data} />}</SectionShell>
}

function Targets({ data }: { data: StatsTargetsDto }) {
  const [measure, setMeasure] = useState<'targets' | 'tracks'>('targets')
  const p = data.period
  const labels = useMemo(() => p.bucketStarts.map((b) => bucketLabel(b, p.bucket)), [p])
  const titles = useMemo(() => p.bucketStarts.map((b) => bucketTitle(b, p.bucket)), [p])
  const rows = measure === 'targets' ? data.targetsByBucket : data.tracksByBucket
  const categorySeries = useMemo(
    () => data.categories.map((c, j) => ({ key: c.code, label: c.name, color: categoryColor(c.code), values: rows.map((r) => r[j] ?? 0) })).filter((s) => s.values.some((v) => v > 0)),
    [data, rows],
  )
  const categoryName = useMemo(() => new Map(data.categories.map((c) => [c.code, c.name])), [data])
  const matrix = useMemo(() => routeMatrix(data.routes, 8), [data])
  const hwMax = Math.max(0, ...data.hourWeekday.flat())
  const empty = data.targets === 0 && data.tracks === 0
  const located = data.targets - data.unlocated
  return (
    <>
      <FilterMeta meta={data.filters} />
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <StatTile label="Фактів про цілі" value={compact(data.targets)} exactValue={data.targets.toLocaleString('uk-UA')} note="без повторів між джерелами" />
        <StatTile label="Окремих обʼєктів (треків)" value={compact(data.tracks)} exactValue={data.tracks.toLocaleString('uk-UA')} note="відкрито за період" />
        <StatTile label="Заявлено обʼєктів" value={compact(data.objectsDeclared)} exactValue={data.objectsDeclared.toLocaleString('uk-UA')} note="сума кількостей у повідомленнях" />
        <StatTile label="З прив’язкою до області" value={data.targets > 0 ? `${Math.round((located / data.targets) * 100)}%` : '—'} exactValue={`${located.toLocaleString('uk-UA')} з ${data.targets.toLocaleString('uk-UA')} фактів`} note={`${compact(located)} фактів`} />
      </div>

      <ChartCard
        title="Що летіло"
        subtitle={`${measure === 'targets' ? 'факти' : 'окремі обʼєкти (треки)'} ${perBucket(p.bucket)}, за категоріями`}
        empty={empty}
        legend={
          <>
            {categorySeries.map((s) => (
              <LegendItem key={s.key} color={s.color} label={s.label} />
            ))}
            <span className="ml-auto inline-flex overflow-hidden rounded border border-slate-300 dark:border-slate-600" role="group" aria-label="Міра">
              {(['targets', 'tracks'] as const).map((m) => (
                <button key={m} type="button" className={`px-2 py-0.5 ${measure === m ? 'bg-slate-200 dark:bg-slate-700' : ''}`} onClick={() => setMeasure(m)} aria-pressed={measure === m}>
                  {m === 'targets' ? 'факти' : 'обʼєкти'}
                </button>
              ))}
            </span>
          </>
        }
        table={{ head: ['Час', ...categorySeries.map((s) => s.label), 'Разом'], rows: titles.map((t, i) => [t, ...categorySeries.map((s) => s.values[i]), categorySeries.reduce((n, s) => n + s.values[i], 0)]) }}
      >
        <AreaStack labels={labels} titles={titles} series={categorySeries} valueLabel={measure === 'targets' ? 'фактів' : 'обʼєктів'} />
      </ChartCard>

      <div className="grid gap-3 lg:grid-cols-2">
        <ChartCard title="За класом" subtitle="факти; частка від усіх фактів" empty={data.targets === 0} table={{ head: ['Клас', 'Категорія', 'Фактів', 'Обʼєктів', 'Заявлено'], rows: data.byClass.map((c) => [c.name, categoryName.get(c.categoryCode) ?? c.categoryCode, c.targets, c.tracks, c.objectsDeclared]) }}>
          <HBars
            rows={data.byClass.slice(0, 14).map((c) => ({
              key: c.code,
              label: c.name,
              value: c.targets,
              swatch: categoryColor(c.categoryCode),
              details: [
                { value: num(c.tracks), label: 'окремих обʼєктів' },
                { value: num(c.objectsDeclared), label: 'заявлено у повідомленнях' },
                { value: categoryName.get(c.categoryCode) ?? c.categoryCode, label: 'категорія' },
              ],
            }))}
            color={ACCENT}
            total={data.targets}
            labelWidth={170}
            ariaLabel="факти за класом"
          />
        </ChartCard>
        <ChartCard
          title="Де фіксували"
          subtitle="область локації факту; топ-15 і решта"
          empty={data.byRegion.length === 0}
          table={{ head: ['Область', 'Фактів'], rows: [...data.byRegion.map((r) => [r.name, r.targets]), ['без прив’язки до області', data.unlocated]] }}
          note={`Без прив’язки до області (немає локації, лише напрямок, за кордоном): ${num(data.unlocated)}`}
        >
          <HBars rows={data.byRegion.map((r) => ({ key: String(r.id ?? 'other'), label: r.name, value: r.targets, color: r.id === undefined ? MUTED : undefined }))} color={ACCENT} total={data.targets} labelWidth={170} ariaLabel="факти за областями" />
        </ChartCard>
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        <ChartCard title="Звідки → куди" subtitle="факти з напрямком «з області А на область Б»; топ-8 × топ-8" empty={data.routes.length === 0} table={{ head: ['Звідки', 'Куди', 'Фактів'], rows: data.routes.slice(0, 60).map((r) => [r.fromName, r.toName, r.count]) }}>
          <Heatmap rows={matrix.rows.map((r) => short(r.name))} cols={matrix.cols.map((c) => short(c.name))} values={matrix.values} labelWidth={110} cellHeight={24} rotateCols title={(i, j) => `${matrix.rows[i].name} → ${matrix.cols[j].name}`} ariaLabel="маршрути між областями" />
          <ol className="mt-1 columns-2 text-[11px] text-slate-600 dark:text-slate-300">
            {data.routes.slice(0, 10).map((r) => (
              <li key={`${r.fromId}-${r.toId}`} className="truncate">
                <span className="tabular-nums text-slate-400">{num(r.count)}</span> {short(r.fromName)} → {short(r.toName)}
              </li>
            ))}
          </ol>
        </ChartCard>
        <ChartCard title="Година × день тижня" subtitle="факти за київським часом" empty={hwMax === 0} table={{ head: ['День', ...HOURS], rows: data.hourWeekday.map((row, i) => [WEEKDAYS[i], ...row]) }}>
          <Heatmap rows={WEEKDAYS} cols={HOURS} values={data.hourWeekday} labelWidth={30} cellHeight={22} title={(i, j) => `${WEEKDAYS[i]}, ${HOURS[j]}:00–${HOURS[(j + 1) % 24]}:00`} ariaLabel="факти за годиною і днем тижня" />
        </ChartCard>
      </div>
    </>
  )
}

/** "Сумська область" → "Сумська" for narrow headers; names without the word stay as they are. */
function short(name: string): string {
  return name.replace(/ область$/, '').replace(/^Автономна Республіка /, 'АР ')
}
