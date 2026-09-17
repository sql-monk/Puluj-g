import { useState } from 'react'
import { adminLifecycle, type LifecycleStatusDto } from '../api/adminLifecycle'
import { Badge, Section } from '../components/settings/fields'
import { Bars, fmtMs, fmtNum, fmtTime, Loading, Stat, usePolled } from './shared'
import { fmtAge } from './QueuesPanel'

const OUTCOME: Record<string, string> = { completed: 'з фактами / без', no_facts: 'без фактів', unsupported: 'не підтримується', needs_review: 'на ревʼю', failed: 'ПОМИЛКА', legacy: 'legacy (стара обробка)', pending: 'ще без розбору' }
const METHOD: Record<string, string> = { rules: 'правила', llm: 'LLM', legacy: 'legacy', pending: '—' }

/** «unavailable» is a word on the page, never a zero that pretends to be a measurement. */
export function unavailable(count: number, of: number): string {
  return count === 0 ? '' : ` (unavailable: ${fmtNum(count)} з ${fmtNum(of)})`
}

/** The share of a funnel step over its stated denominator — the denominator is always named next to it. */
export function share(part: number, whole: number): string {
  return whole === 0 ? '—' : `${((100 * part) / whole).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} % з ${fmtNum(whole)}`
}

/** Average flow over the selected calendar window, retained as decimal instead of rounding low-volume sources to zero. */
function throughput(value: number, hours: number): string {
  const n = (value / hours).toLocaleString('uk-UA', { maximumFractionDigits: 1 })
  const d = ((24 * value) / hours).toLocaleString('uk-UA', { maximumFractionDigits: 1 })
  return `${n}/год · ${d}/добу`
}

function percent(part: number, whole: number): string {
  return whole === 0 ? '—' : `${((100 * part) / whole).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} %`
}

/** A source-level conversion: numerator and denominator are both messages, never generated object counts. */
function Conversion({ value, total }: { value: number; total: number }) {
  return (
    <div className="font-mono" title={`${fmtNum(value)} з ${fmtNum(total)} повідомлень`}>
      <div>{fmtNum(value)}</div>
      <div className="text-[10px] text-slate-400">{percent(value, total)}</div>
    </div>
  )
}

function Dict({ values, label, map }: { values: Record<string, number>; label?: (k: string) => string; map?: Record<string, string> }) {
  const entries = Object.entries(values).sort((a, b) => b[1] - a[1])
  if (entries.length === 0) return <p className="text-xs text-slate-500">—</p>
  return (
    <ul className="text-xs">
      {entries.map(([k, v]) => (
        <li key={k} className="flex justify-between gap-2 border-b border-slate-100 dark:border-slate-800">
          <span className={k === 'failed' ? 'font-semibold text-red-700 dark:text-red-300' : ''}>{label ? label(k) : (map?.[k] ?? k)}</span>
          <span className="font-mono">{fmtNum(v)}</span>
        </li>
      ))}
    </ul>
  )
}

/**
 * Сторінка «Аналітика повідомлень» (P15, §10, ADR-0013): життєвий цикл кожного повідомлення з відомими знаменниками — джерела й
 * надходження, проходження конвеєра (funnel і час на переходах), розбори, якість, вартість, результати, історія змін; no-text і failed видимі,
 * невідомі timings/completion позначені «unavailable».
 */
export function MessageAnalyticsPanel() {
  const [hours, setHours] = useState<24 | 168 | 720>(24)
  const { data, error, reload } = usePolled(() => adminLifecycle.report(hours), 60_000, [hours])
  const status = usePolled(() => adminLifecycle.status(), 30_000)
  const [actor, setActor] = useState('')
  const [reason, setReason] = useState('')
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const ready = actor.trim().length > 0 && reason.trim().length > 0
  const run = async (what: string, action: () => Promise<unknown>) => {
    setBusy(true)
    try {
      await action()
      setMessage({ ok: true, text: `${what}: виконано (${actor.trim()})` })
      reload()
      status.reload()
    } catch (e) {
      setMessage({ ok: false, text: `${what}: ${e instanceof Error ? e.message : String(e)}` })
    } finally {
      setBusy(false)
    }
  }
  if (!data) return <Loading error={error} empty />
  return (
    <div className="space-y-4 text-sm" data-testid="message-analytics">
      <Loading error={error} />
      <div className="flex flex-wrap items-center gap-2 text-xs">
        {([24, 168, 720] as const).map((h) => (
          <button key={h} className={`rounded px-2 py-1 ${hours === h ? 'bg-slate-800 text-white dark:bg-slate-100 dark:text-slate-900' : 'border border-slate-300 dark:border-slate-600'}`} onClick={() => setHours(h)}>
            {h === 24 ? '24 год' : h === 168 ? '7 д' : '30 д'}
          </button>
        ))}
        <span className="text-slate-500">
          {fmtTime(data.from)} → {fmtTime(data.to)} · bucket {data.bucket === 'hour' ? 'година (UTC)' : 'доба (Europe/Kyiv)'} · час у сховищі UTC
        </span>
      </div>

      <StatusRow status={status.data} />

      <Section title="Повідомлення та результат" badge={<Badge ok={data.funnel.stuckAnalysis + data.funnel.stuckDomain === 0} text={data.funnel.stuckAnalysis + data.funnel.stuckDomain === 0 ? 'без зависань' : `зависли: розбір ${fmtNum(data.funnel.stuckAnalysis)}, домен ${fmtNum(data.funnel.stuckDomain)}`} />}>
        <div className="grid gap-2 sm:grid-cols-4 xl:grid-cols-8" data-testid="funnel">
          <Stat label="Отримано" value={fmtNum(data.funnel.raw)} hint={`${throughput(data.funnel.raw, data.hours)} · постів ${fmtNum(data.funnel.posts)}`} />
          <Stat label="Розібрано" value={fmtNum(data.funnel.analyzed)} hint={share(data.funnel.analyzed, data.funnel.raw)} />
          <Stat label="З фактами" value={fmtNum(data.funnel.withFacts)} hint={share(data.funnel.withFacts, data.funnel.raw)} />
          <Stat label="З цілями" value={fmtNum(data.funnel.withTargets)} hint={share(data.funnel.withTargets, data.funnel.raw)} />
          <Stat label="З подіями" value={fmtNum(data.funnel.withEvents)} hint={share(data.funnel.withEvents, data.funnel.raw)} />
          <Stat label="З інцидентами" value={fmtNum(data.funnel.withIncidents)} hint={share(data.funnel.withIncidents, data.funnel.raw)} />
          <Stat label="З треками" value={fmtNum(data.funnel.withTracks)} hint={share(data.funnel.withTracks, data.funnel.raw)} />
          <Stat label="З тривогами" value={fmtNum(data.funnel.withAlerts)} hint={share(data.funnel.withAlerts, data.funnel.raw)} />
        </div>
        <details className="text-xs text-slate-600 dark:text-slate-300" data-testid="transitions">
          <summary className="cursor-pointer">Швидкість конвеєра: p50 — половина повідомлень вклалася в цей час; p95 — 95 % вклалися</summary>
          <p className="mt-1">
            Збережено → розібрано: p50 {fmtAge(data.funnel.storedToAnalyzedP50Seconds)}, p95 {fmtAge(data.funnel.storedToAnalyzedP95Seconds)}; розібрано → домен: p50 {fmtAge(data.funnel.analyzedToDomainP50Seconds)}, p95 {fmtAge(data.funnel.analyzedToDomainP95Seconds)}. Виміряно для {fmtNum(Math.max(0, data.funnel.raw - data.funnel.unavailableTimings))} з {fmtNum(data.funnel.raw)} повідомлень.
            {data.funnel.unavailableTimings > 0 && <span className="text-amber-700 dark:text-amber-300"> Для {fmtNum(data.funnel.unavailableTimings)} старих повідомлень етапи не записувалися, тому час відсутній.</span>}
          </p>
        </details>
      </Section>

      <Section title="Надходження й конверсія в часі" badge={<Badge ok={null} text={`у середньому ${throughput(data.funnel.raw, data.hours)}`} />}>
        <p className="mb-2 text-xs text-slate-500">Кожна смуга — {data.bucket === 'hour' ? 'година' : 'доба'}; усі значення — кількість повідомлень у відповідному стані.</p>
        <div className="flex flex-wrap gap-x-5 gap-y-3 text-xs" data-testid="timeline">
          <TimelineBars label="отримано" values={data.timeline.map((b) => b.raw)} timeline={data.timeline} />
          <TimelineBars label="розібрано" values={data.timeline.map((b) => b.analyzed)} color="bg-blue-500" timeline={data.timeline} />
          <TimelineBars label="з фактами" values={data.timeline.map((b) => b.withFacts)} color="bg-emerald-500" timeline={data.timeline} />
          <TimelineBars label="з цілями" values={data.timeline.map((b) => b.withTargets)} color="bg-violet-500" timeline={data.timeline} />
          <TimelineBars label="з подіями" values={data.timeline.map((b) => b.withEvents)} color="bg-amber-500" timeline={data.timeline} />
          <TimelineBars label="з інцидентами" values={data.timeline.map((b) => b.withIncidents)} color="bg-rose-500" timeline={data.timeline} />
        </div>
      </Section>

      <Section title="Конверсія за джерелами" badge={<Badge ok={null} text="у клітинці: повідомлення та частка від отриманих" />}>
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="sources">
            <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-1 pr-2">Джерело</th>
                <th className="pr-2 text-right">отримано</th>
                <th className="pr-2 text-right">розібрано</th>
                <th className="pr-2 text-right">з фактами</th>
                <th className="pr-2 text-right">з цілями</th>
                <th className="pr-2 text-right">з подіями</th>
                <th className="pr-2 text-right">з інцидентами</th>
                <th className="pr-2 text-right">з треками</th>
                <th className="pr-2 text-right">з тривогами</th>
                <th className="pr-2 text-right">фактів</th>
              </tr>
            </thead>
            <tbody>
              {data.sources.map((s) => (
                <tr key={s.sourceId} className="border-t border-slate-100 dark:border-slate-800" data-testid={`source-${s.code}`}>
                  <td className="py-1 pr-2 font-mono">{s.code}</td>
                  <td className="pr-2 text-right"><Conversion value={s.raw} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.analyzed} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withFacts} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withTargets} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withEvents} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withIncidents} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withTracks} total={s.raw} /></td>
                  <td className="pr-2 text-right"><Conversion value={s.withAlerts} total={s.raw} /></td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.facts)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="Розбори">
          <div className="grid gap-3 sm:grid-cols-2 text-xs">
            <div>
              <div className="text-[10px] uppercase text-slate-500">outcome (знаменник: розібрано {fmtNum(data.funnel.analyzed)} + очікують)</div>
              <div data-testid="outcomes">
                <Dict values={data.parse.outcomes} map={OUTCOME} />
              </div>
            </div>
            <div>
              <div className="text-[10px] uppercase text-slate-500">метод</div>
              <Dict values={data.parse.methods} map={METHOD} />
              <div className="mt-2 text-[10px] uppercase text-slate-500">факти</div>
              <p>
                усього {fmtNum(data.parse.totalFacts)}, multi-fact повідомлень {fmtNum(data.parse.multiFact)}, без локації {fmtNum(data.parse.unlocated)}
              </p>
            </div>
            <div>
              <div className="text-[10px] uppercase text-slate-500">версії правил</div>
              <Dict values={data.parse.ruleVersions} />
            </div>
            <div>
              <div className="text-[10px] uppercase text-slate-500">моделі</div>
              <Dict values={data.parse.modelVersions} />
            </div>
          </div>
        </Section>
        <Section title="Якість">
          <div className="text-xs space-y-2" data-testid="quality">
            <div>
              <div className="text-[10px] uppercase text-slate-500">review outcomes (ревізії оператора)</div>
              <Dict values={data.quality.reviewOutcomes} />
            </div>
            <p>
              Precision/recall: <span className="font-semibold">{data.quality.precisionRecall}</span>
            </p>
            <p>
              Rules vs LLM: <span className="font-semibold">{data.quality.rulesVsLlm}</span>
            </p>
            <p className="text-slate-500">Якість не підміняється кількістю фактів.</p>
          </div>
        </Section>
      </div>

      <Section title="Вартість і ресурси" badge={<Badge ok={null} text={`$${data.cost.costUsd.toLocaleString('en-US', { maximumFractionDigits: 4 })} · ${fmtNum(data.cost.calls)} викликів · ${fmtNum(data.cost.roots)} повідомлень`} />}>
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="cost">
            <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-1 pr-2">Модель</th>
                <th className="pr-2 text-right">виклики</th>
                <th className="pr-2 text-right">input</th>
                <th className="pr-2 text-right">cache</th>
                <th className="pr-2 text-right">output</th>
                <th className="pr-2 text-right">$</th>
                <th className="pr-2 text-right">latency p50/p95</th>
                <th className="pr-2 text-right">помилки</th>
                <th className="pr-2 text-right">пізні (оплачені)</th>
              </tr>
            </thead>
            <tbody>
              {data.cost.byModel.map((m) => (
                <tr key={m.model} className="border-t border-slate-100 dark:border-slate-800">
                  <td className="py-1 pr-2 font-mono">{m.model}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(m.calls)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(m.inputTokens)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(m.cacheTokens)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(m.outputTokens)}</td>
                  <td className="pr-2 text-right font-mono">{m.costUsd.toFixed(4)}</td>
                  <td className="pr-2 text-right font-mono">
                    {fmtMs(m.latencyP50Ms)} / {fmtMs(m.latencyP95Ms)}
                  </td>
                  <td className={`pr-2 text-right font-mono ${m.failures > 0 ? 'text-red-600' : ''}`}>{fmtNum(m.failures)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(m.late)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="text-xs text-slate-500">
          cache share {data.cost.cacheShare == null ? '—' : `${(100 * data.cost.cacheShare).toFixed(0)} %`}; за джерелами: {data.cost.bySource.map((s) => `${s.key} $${s.value.toFixed(3)}`).join(', ') || '—'}
        </p>
      </Section>

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="Результати">
          <div className="grid gap-3 sm:grid-cols-2 text-xs" data-testid="results">
            <div>
              <div className="text-[10px] uppercase text-slate-500">event kinds (факти)</div>
              <Dict values={data.results.eventKinds} />
            </div>
            <div>
              <div className="text-[10px] uppercase text-slate-500">точність локації incidents</div>
              <Dict values={data.results.incidentPrecision} />
              <p className="mt-2">
                incidents {fmtNum(data.results.incidents)} (active {fmtNum(data.results.activeIncidents)}, з provenance {fmtNum(data.results.incidentsWithProvenance)}), tracks {fmtNum(data.results.tracks)}, alerts {fmtNum(data.results.alerts)}
              </p>
            </div>
          </div>
        </Section>
        <Section title="Історія змін">
          <div className="text-xs space-y-2" data-testid="history">
            <table className="w-full">
              <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
                <tr>
                  <th className="py-1 pr-2">run</th>
                  <th className="pr-2">тип</th>
                  <th className="pr-2">pipeline</th>
                  <th className="pr-2 text-right">рядків</th>
                  <th>outcomes</th>
                </tr>
              </thead>
              <tbody>
                {data.history.runs.map((r) => (
                  <tr key={r.runId} className="border-t border-slate-100 dark:border-slate-800">
                    <td className="py-1 pr-2 font-mono" title={r.runId}>
                      {r.runId.slice(0, 8)}…
                    </td>
                    <td className="pr-2">{r.kind}</td>
                    <td className="pr-2 font-mono">{r.pipelineVersion ?? 'unavailable'}</td>
                    <td className="pr-2 text-right font-mono">{fmtNum(r.rows)}</td>
                    <td className="font-mono">
                      {Object.entries(r.outcomes)
                        .map(([k, v]) => `${k} ${v}`)
                        .join(', ')}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p>
              incidents за generation: {data.history.incidentsByGeneration.map((g) => `${g.key.slice(0, 8)}${g.key.includes('(active)') ? ' (active)' : ''}: ${fmtNum(g.value)}`).join('; ') || '—'} — replay diff = порівняння generation vs active
            </p>
          </div>
        </Section>
      </div>

      <Section title="Backfill і звірка лічильників">
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="text-xs">
            Хто (actor)
            <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={actor} onChange={(e) => setActor(e.target.value)} aria-label="actor" />
          </label>
          <label className="text-xs">
            Чому (reason)
            <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={reason} onChange={(e) => setReason(e.target.value)} aria-label="reason" />
          </label>
        </div>
        <div className="flex flex-wrap gap-2 text-xs">
          <button className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600" disabled={!ready || busy} onClick={() => void run('backfill', () => adminLifecycle.backfill(actor.trim(), reason.trim()))}>
            Продовжити backfill
          </button>
          <button className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600" disabled={!ready || busy} onClick={() => void run('звірка', () => adminLifecycle.reconcile(actor.trim(), reason.trim()))}>
            Звірити лічильники
          </button>
        </div>
        {message && (
          <p className={`text-xs ${message.ok ? 'text-emerald-600' : 'text-red-600'}`} role="status">
            {message.text}
          </p>
        )}
      </Section>
    </div>
  )
}

function TimelineBars({ label, values, timeline, color }: { label: string; values: number[]; timeline: { at: string }[]; color?: string }) {
  return (
    <div>
      <div className="text-[10px] uppercase text-slate-500">{label}</div>
      <Bars values={values} color={color} title={(i, value) => `${fmtTime(timeline[i].at)}: ${fmtNum(value)} повідомлень`} />
    </div>
  )
}

function StatusRow({ status }: { status: LifecycleStatusDto | null }) {
  if (!status) return null
  const r = status.reconciliation
  return (
    <div className="grid gap-3 sm:grid-cols-3" data-testid="lifecycle-status">
      <Stat
        label="Backfill"
        value={`${fmtNum(status.backfill.projectedRows)} / ${fmtNum(status.backfill.rawRows)}`}
        hint={`проєкційовано / усі повідомлення · ${status.backfill.caughtUp ? 'наздогнав' : 'ще триває'}${status.backfill.at ? ` · оновлено ${fmtTime(status.backfill.at)}` : ' · ще не запускався'}`}
        tone={status.backfill.caughtUp ? 'ok' : 'warn'}
      />
      <Stat
        label={`Звірка (${r ? `${r.windowHours} год` : '—'})`}
        value={r ? (r.missingRoots === 0 ? 'лічильники збігаються' : `бракує ${fmtNum(r.missingRoots)}`) : 'звіту ще немає'}
        hint={r ? `raw ${fmtNum(r.rawRows)} (пости ${fmtNum(r.posts)}, редакції ${fmtNum(r.edits)}) vs проєкція ${fmtNum(r.projectedRaw)} (пости ${fmtNum(r.projectedPosts)}) · ${fmtTime(r.at)}` : ''}
        tone={r ? (r.missingRoots === 0 ? 'ok' : 'bad') : undefined}
      />
      <Stat label="Пізні результати" value={r ? `${fmtNum(r.lateAnalyses + r.lateCompletions)} дописано` : '—'} hint={r ? `розбори ${fmtNum(r.lateAnalyses)}, домен ${fmtNum(r.lateCompletions)}; очікують: розбір ${fmtNum(r.pendingAnalysis)}, домен ${fmtNum(r.pendingDomain)}; unavailable timings ${fmtNum(r.unavailableTimings)}` : ''} />
    </div>
  )
}
