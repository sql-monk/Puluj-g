import { useMemo, useState } from 'react'
import { admin, type PipelineReportDto, type ProcessingErrorDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { Bars, Freshness, Loading, Stat, ago, bucketLabel, fmtMs, fmtNum, fmtPercent, fmtTime, usePolled } from './shared'
import { share, sortSources, type SourceSortKey } from './workers'

const PERIODS: { hours: 24 | 168 | 720; label: string }[] = [
  { hours: 24, label: '24 год' },
  { hours: 168, label: '7 д' },
  { hours: 720, label: '30 д' },
]

/**
 * What the pipeline did in the period: totals, per bucket, per source, per instance, current processing statuses and errors.
 * One request per period; polled every 15 s.
 */
export function PipelinePanel() {
  const [hours, setHours] = useState<24 | 168 | 720>(24)
  const { data, error, stale, loadedAt } = usePolled(() => admin.ops.pipeline(hours), 15_000, [hours])
  const shownPeriod = PERIODS.find((p) => data && Math.round((new Date(data.to).getTime() - new Date(data.from).getTime()) / 3_600_000) === p.hours)?.label
  const t = data?.totals
  const withTargets = data ? data.sources.reduce((s, x) => s + x.withTargets, 0) : 0
  return (
    <>
      <Section
        title="Конвеєр"
        badge={
          <span className="flex gap-1">
            {PERIODS.map((p) => (
              <button key={p.hours} aria-pressed={hours === p.hours} className={`rounded px-2 py-0.5 text-xs ${hours === p.hours ? 'bg-slate-800 text-white dark:bg-slate-100 dark:text-slate-900' : 'bg-slate-100 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700'}`} onClick={() => setHours(p.hours)}>
                {p.label}
              </button>
            ))}
          </span>
        }
      >
        <p className="text-xs text-slate-500">Скільки прийшло, скільки й за який час оброблено, що з цього вийшло. Час — Europe/Kyiv; p50/p90 — з raw_messages.processing_ms (лише успішні обробки).</p>
        <p className="text-xs text-slate-500">
          Кожна група рахується за своїм часом: «Прийнято» — за часом отримання; «Оброблено» і «З фактами» — за часом обробки, тож під час
          завантаження історії сюди входять і старі повідомлення, отримані раніше; «Цілей» і «Треків» — за часом події в повідомленні.
          «У черзі» — увесь поточний backlog, незалежно від періоду.
        </p>
        <Loading error={error} empty={!data && !error} slow="сервер агрегує raw_messages і targets за весь період; 7 і 30 днів рахуються довше" />
        {data && (
          <div className="flex flex-wrap items-center gap-2">
            <Freshness stale={stale} loadedAt={loadedAt} label={shownPeriod ? `період ${shownPeriod}` : undefined} />
          </div>
        )}
        {data && t && (
          <div className={stale ? 'space-y-3 opacity-50 transition-opacity' : 'space-y-3'} aria-busy={stale}>
            <div className="grid grid-cols-2 gap-2 text-xs sm:grid-cols-4 lg:grid-cols-5">
              <Stat label="Прийнято" value={fmtNum(t.received)} hint="за часом отримання" />
              <Stat label="Оброблено" value={fmtNum(t.processed)} hint={`за часом обробки · пропущено ${fmtNum(t.skipped)}, помилок ${fmtNum(t.failed)}`} />
              <Stat label="З фактами" value={fmtPercent(share(withTargets, t.processed))} hint={`${fmtNum(withTargets)} повідомлень`} />
              <Stat label="Цілей" value={fmtNum(t.targets)} hint={`за часом події · з них дублікатів ${fmtNum(t.duplicates)}`} />
              <Stat label="Треків" value={fmtNum(t.tracks)} />
              <Stat label="У черзі" value={fmtNum(t.pending + t.inProgress)} hint={`увесь backlog · очікує ${fmtNum(t.pending)}, у роботі ${fmtNum(t.inProgress)}`} tone={t.pending > 100 ? 'warn' : undefined} />
              <Stat label="Помилок" value={fmtNum(t.errors)} tone={t.errors === 0 ? undefined : t.errors < 20 ? 'warn' : 'bad'} />
              <Stat label="p50 обробки" value={fmtMs(t.p50Ms)} />
              <Stat label="p90 обробки" value={fmtMs(t.p90Ms)} />
              <Stat label="Середнє" value={fmtMs(t.meanMs)} />
            </div>
            <TimelineChart report={data} />
          </div>
        )}
      </Section>
      <div className={stale ? 'space-y-4 opacity-50' : 'space-y-4'}>
      {data && <SourcesTable report={data} />}
      {data && data.instances.length > 0 && <InstancesTable report={data} />}
      {data && (
        <Section title="Помилки" badge={<Badge ok={data.totals.errors === 0 ? true : data.totals.errors < 20 ? null : false} text={`${fmtNum(data.totals.errors)} за період`} />}>
          <div className="flex flex-wrap gap-2 text-xs">
            {Object.entries(data.processingStatuses).map(([k, v]) => (
              <span key={k} className="rounded bg-slate-100 px-2 py-0.5 dark:bg-slate-800">
                {k}: {fmtNum(v)}
              </span>
            ))}
          </div>
          {Object.keys(data.errorsByStage).length > 0 && (
            <div className="flex flex-wrap gap-2 text-xs">
              {Object.entries(data.errorsByStage).map(([stage, n]) => (
                <span key={stage} className="rounded bg-red-100 px-2 py-0.5 text-red-900 dark:bg-red-900/40 dark:text-red-100">
                  {stage}: {fmtNum(n)}
                </span>
              ))}
            </div>
          )}
          <ErrorList errors={data.recentErrors} />
        </Section>
      )}
      </div>
    </>
  )
}

const CHART = { w: 960, h: 200, padL: 44, padR: 44, padT: 8, padB: 22 }

/** Grouped columns per bucket (received / processed / targets) with the p90 processing time as a line on the right axis. */
function TimelineChart({ report }: { report: PipelineReportDto }) {
  const rows = report.timeline
  const unit = report.bucket
  const [hover, setHover] = useState<number | null>(null)
  const maxCount = Math.max(1, ...rows.map((r) => Math.max(r.received, r.processed, r.targets)))
  const maxMs = Math.max(1, ...rows.map((r) => r.p90Ms ?? 0))
  const innerW = CHART.w - CHART.padL - CHART.padR
  const innerH = CHART.h - CHART.padT - CHART.padB
  const slot = innerW / Math.max(1, rows.length)
  const bar = Math.max(1, (slot * 0.8) / 3)
  const y = (v: number) => CHART.padT + innerH - (v / maxCount) * innerH
  const yMs = (v: number) => CHART.padT + innerH - (v / maxMs) * innerH
  const ticks = niceTicks(maxCount)
  const line = rows
    .map((r, i) => (r.p90Ms === undefined || r.p90Ms === null ? null : `${(CHART.padL + i * slot + slot / 2).toFixed(1)},${yMs(r.p90Ms).toFixed(1)}`))
    .filter((p): p is string => p !== null)
    .join(' ')
  const labelEvery = rows.length > 16 ? Math.ceil(rows.length / 12) : 1
  const h = hover !== null ? rows[hover] : null
  return (
    <div className="space-y-1">
      <div className="flex flex-wrap gap-3 text-[11px] text-slate-500">
        <Legend color="bg-sky-500" text="прийнято" />
        <Legend color="bg-emerald-500" text="оброблено" />
        <Legend color="bg-amber-500" text="цілей" />
        <Legend color="bg-violet-500" text="p90 обробки, мс (права вісь)" />
        {h && (
          <span className="ml-auto font-mono text-slate-700 dark:text-slate-200">
            {bucketLabel(h.at, unit)}: прийнято {fmtNum(h.received)}, оброблено {fmtNum(h.processed)}, цілей {fmtNum(h.targets)}, помилок {fmtNum(h.errors)}
            {h.transient ? ` (тимчасових ${fmtNum(h.transient)})` : ''}, p50 {fmtMs(h.p50Ms)}, p90 {fmtMs(h.p90Ms)}
          </span>
        )}
      </div>
      <svg viewBox={`0 0 ${CHART.w} ${CHART.h}`} className="w-full text-[10px]" role="img" aria-label="Прийнято, оброблено, цілей за бакетами" onMouseLeave={() => setHover(null)}>
        {ticks.map((v) => (
          <g key={v}>
            <line x1={CHART.padL} x2={CHART.w - CHART.padR} y1={y(v)} y2={y(v)} className="stroke-slate-200 dark:stroke-slate-700" strokeWidth={1} />
            <text x={CHART.padL - 4} y={y(v) + 3} textAnchor="end" className="fill-slate-500">
              {compact(v)}
            </text>
          </g>
        ))}
        {[0, 0.5, 1].map((f) => (
          <text key={f} x={CHART.w - CHART.padR + 4} y={yMs(maxMs * f) + 3} className="fill-violet-500">
            {compact(Math.round(maxMs * f))}
          </text>
        ))}
        {rows.map((r, i) => {
          const x0 = CHART.padL + i * slot + slot * 0.1
          return (
            <g key={r.at} onMouseEnter={() => setHover(i)}>
              <rect x={CHART.padL + i * slot} y={CHART.padT} width={slot} height={innerH} fill="transparent" className={hover === i ? 'fill-slate-100 dark:fill-slate-800' : ''} />
              <rect x={x0} y={y(r.received)} width={bar} height={Math.max(0, innerH + CHART.padT - y(r.received))} className="fill-sky-500" />
              <rect x={x0 + bar} y={y(r.processed)} width={bar} height={Math.max(0, innerH + CHART.padT - y(r.processed))} className="fill-emerald-500" />
              <rect x={x0 + bar * 2} y={y(r.targets)} width={bar} height={Math.max(0, innerH + CHART.padT - y(r.targets))} className="fill-amber-500" />
              {r.errors > 0 && <circle cx={CHART.padL + i * slot + slot / 2} cy={CHART.padT + innerH + 4} r={2} className="fill-red-500" />}
              {i % labelEvery === 0 && (
                <text x={CHART.padL + i * slot + slot / 2} y={CHART.h - 6} textAnchor="middle" className="fill-slate-500">
                  {bucketLabel(r.at, unit)}
                </text>
              )}
            </g>
          )
        })}
        {line && <polyline points={line} fill="none" className="stroke-violet-500" strokeWidth={1.5} />}
      </svg>
    </div>
  )
}

function Legend({ color, text }: { color: string; text: string }) {
  return (
    <span className="flex items-center gap-1">
      <span className={`inline-block h-2 w-2 rounded-sm ${color}`} /> {text}
    </span>
  )
}

function niceTicks(max: number): number[] {
  const raw = max / 4
  const pow = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 5, 10].map((m) => m * pow).find((s) => s >= raw) ?? pow
  const ticks: number[] = []
  for (let v = step; v <= max; v += step) ticks.push(v)
  return ticks
}

function compact(v: number): string {
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`
  if (v >= 10_000) return `${Math.round(v / 1000)}k`
  if (v >= 1000) return `${(v / 1000).toFixed(1)}k`
  return String(v)
}

const SOURCE_COLUMNS: { key: SourceSortKey; label: string; title?: string }[] = [
  { key: 'name', label: 'Джерело' },
  { key: 'received', label: 'Прийнято' },
  { key: 'processed', label: 'Оброблено' },
  { key: 'skipped', label: 'Пропущено' },
  { key: 'failed', label: 'Помилок' },
  { key: 'pending', label: 'У черзі' },
  { key: 'withTargetsShare', label: 'З фактами', title: 'частка оброблених повідомлень, з яких вийшла хоча б одна ціль' },
  { key: 'targets', label: 'Цілей' },
  { key: 'tracks', label: 'Треків', title: 'треки, відкриті ціллю цього джерела' },
  { key: 'medianLagSeconds', label: 'Затримка', title: 'медіана received − published (лише < 6 год)' },
  { key: 'p50Ms', label: 'p50' },
  { key: 'p90Ms', label: 'p90' },
]

function SourcesTable({ report }: { report: PipelineReportDto }) {
  const [sort, setSort] = useState<{ key: SourceSortKey; asc: boolean }>({ key: 'received', asc: false })
  const rows = useMemo(() => sortSources(report.sources, sort.key, sort.asc), [report.sources, sort])
  const click = (key: SourceSortKey) => setSort((s) => (s.key === key ? { key, asc: !s.asc } : { key, asc: key === 'name' }))
  const starts = report.bucketStarts
  return (
    <Section title="За джерелами" badge={<Badge ok={null} text={`${rows.length}`} />}>
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead className="text-left text-slate-500">
            <tr>
              {SOURCE_COLUMNS.map((c) => (
                <th key={c.key} className={`cursor-pointer select-none py-1 pr-2 ${c.key === 'name' ? '' : 'text-right'}`} title={c.title} onClick={() => click(c.key)}>
                  {c.label}
                  {sort.key === c.key ? (sort.asc ? ' ↑' : ' ↓') : ''}
                </th>
              ))}
              <th className="pr-2">Прийнято за бакетами</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((s) => (
              <tr key={s.sourceId} className={`border-t border-slate-100 dark:border-slate-800 ${s.enabled ? '' : 'opacity-50'}`}>
                <td className="py-1.5 pr-2">
                  {s.name} <span className="text-slate-400">{s.code}</span>
                </td>
                <td className="pr-2 text-right font-mono">{fmtNum(s.received)}</td>
                <td className="pr-2 text-right font-mono">{fmtNum(s.processed)}</td>
                <td className="pr-2 text-right font-mono text-slate-500">{fmtNum(s.skipped)}</td>
                <td className={`pr-2 text-right font-mono ${s.failed > 0 ? 'text-red-600' : 'text-slate-500'}`}>{fmtNum(s.failed)}</td>
                <td className="pr-2 text-right font-mono text-slate-500">{fmtNum(s.pending)}</td>
                <td className="pr-2 text-right font-mono" title={`${fmtNum(s.withTargets)} повідомлень`}>
                  {fmtPercent(share(s.withTargets, s.processed), 0)}
                </td>
                <td className="pr-2 text-right font-mono">{fmtNum(s.targets)}</td>
                <td className="pr-2 text-right font-mono">{fmtNum(s.tracks)}</td>
                <td className="pr-2 text-right font-mono text-slate-500">{s.medianLagSeconds === undefined || s.medianLagSeconds === null ? '—' : `${fmtNum(Math.round(s.medianLagSeconds))} с`}</td>
                <td className="pr-2 text-right font-mono">{fmtMs(s.p50Ms)}</td>
                <td className="pr-2 text-right font-mono text-slate-500">{fmtMs(s.p90Ms)}</td>
                <td className="pr-2">
                  <Bars values={s.series} width="w-1" title={(i, v) => `${bucketLabel(starts[i], report.bucket)}: ${fmtNum(v)}`} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  )
}

function InstancesTable({ report }: { report: PipelineReportDto }) {
  const total = report.instances.reduce((s, i) => s + i.processed, 0)
  return (
    <Section title="За інстансами" badge={<Badge ok={null} text={`${report.instances.length}`} />}>
      <p className="text-xs text-slate-500">Хто скільки обробив за період — за raw_messages.claimed_by.</p>
      <table className="w-full text-xs">
        <thead className="text-left text-slate-500">
          <tr>
            <th className="py-1 pr-2">Інстанс</th>
            <th className="pr-2 text-right">Оброблено</th>
            <th className="pr-2 text-right">Частка</th>
            <th className="pr-2 text-right">p50</th>
            <th className="pr-2 text-right">p90</th>
            <th className="pr-2">Востаннє</th>
          </tr>
        </thead>
        <tbody>
          {report.instances.map((i) => (
            <tr key={i.instance} className="border-t border-slate-100 dark:border-slate-800">
              <td className="py-1.5 pr-2 font-mono">{i.instance}</td>
              <td className="pr-2 text-right font-mono">{fmtNum(i.processed)}</td>
              <td className="pr-2 text-right font-mono text-slate-500">{fmtPercent(share(i.processed, total), 0)}</td>
              <td className="pr-2 text-right font-mono">{fmtMs(i.p50Ms)}</td>
              <td className="pr-2 text-right font-mono text-slate-500">{fmtMs(i.p90Ms)}</td>
              <td className="pr-2 text-slate-500" title={fmtTime(i.lastAt)}>
                {ago(i.lastAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Section>
  )
}

function ErrorList({ errors }: { errors: ProcessingErrorDto[] }) {
  const [open, setOpen] = useState<number | null>(null)
  if (errors.length === 0) return <div className="text-xs text-slate-500">Помилок не зафіксовано.</div>
  return (
    <table className="w-full text-xs">
      <thead className="text-left text-slate-500">
        <tr>
          <th className="py-1 pr-2">Коли</th>
          <th className="pr-2">Етап</th>
          <th className="pr-2">Повідомлення</th>
          <th className="pr-2">Джерело</th>
        </tr>
      </thead>
      <tbody>
        {errors.map((e) => (
          <tr key={e.id} className="cursor-pointer border-t border-slate-100 align-top hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50" onClick={() => setOpen(open === e.id ? null : e.id)}>
            <td className="whitespace-nowrap py-1 pr-2 text-slate-500">{fmtTime(e.occurredAt)}</td>
            <td className="pr-2">{e.stage}</td>
            <td className="pr-2">
              <div className={open === e.id ? 'whitespace-pre-wrap break-all font-mono' : 'max-w-xl truncate'}>{open === e.id && e.exception ? `${e.message}\n\n${e.exception}` : e.message}</div>
            </td>
            <td className="pr-2 text-slate-500">
              {e.sourceId ?? '—'}
              {e.rawMessageId ? ` · msg ${e.rawMessageId}` : ''}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
