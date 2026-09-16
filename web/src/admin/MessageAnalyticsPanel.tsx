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
 * невідомі timings/completion позначені «unavailable». Copy-аналітика («Хто кого копіює») лишається окремою сторінкою — допоміжний розділ.
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
        <a className="ml-auto underline" href="#/analytics">
          Схожість повідомлень (хто кого копіює) →
        </a>
      </div>

      <StatusRow status={status.data} />

      <Section title="Проходження конвеєра" badge={<Badge ok={data.funnel.stuckAnalysis + data.funnel.stuckDomain === 0} text={data.funnel.stuckAnalysis + data.funnel.stuckDomain === 0 ? 'без зависань' : `зависли: розбір ${fmtNum(data.funnel.stuckAnalysis)}, домен ${fmtNum(data.funnel.stuckDomain)}`} />}>
        <div className="grid gap-2 sm:grid-cols-3 lg:grid-cols-6" data-testid="funnel">
          <Stat label="Отримано (raw)" value={fmtNum(data.funnel.raw)} hint={`постів ${fmtNum(data.funnel.posts)} (редакції окремо)`} />
          <Stat label="Збережено" value={fmtNum(data.funnel.stored)} hint={share(data.funnel.stored, data.funnel.raw)} />
          <Stat label="Розібрано" value={fmtNum(data.funnel.analyzed)} hint={share(data.funnel.analyzed, data.funnel.raw)} />
          <Stat label="З фактами" value={fmtNum(data.funnel.withFacts)} hint={share(data.funnel.withFacts, data.funnel.analyzed) + ' розібраних'} />
          <Stat label="Доменно оброблено" value={fmtNum(data.funnel.domainCompleted)} hint={share(data.funnel.domainCompleted, data.funnel.analyzed) + unavailable(data.funnel.unavailableCompletion, data.funnel.raw)} tone={data.funnel.unavailableCompletion > 0 ? 'warn' : undefined} />
          <Stat label="Видимі (incidents)" value={fmtNum(data.funnel.visible)} hint="incidents active generation" />
        </div>
        <p className="text-xs" data-testid="transitions">
          Час збережено → розібрано: p50 {fmtAge(data.funnel.storedToAnalyzedP50Seconds)}, p95 {fmtAge(data.funnel.storedToAnalyzedP95Seconds)}; розібрано → домен: p50 {fmtAge(data.funnel.analyzedToDomainP50Seconds)}, p95 {fmtAge(data.funnel.analyzedToDomainP95Seconds)}
          {data.funnel.unavailableTimings > 0 && <span className="ml-1 text-amber-700 dark:text-amber-300">— timings unavailable для {fmtNum(data.funnel.unavailableTimings)} повідомлень (оброблені до появи стадій; не вигадуємо)</span>}
        </p>
        <div className="flex flex-wrap gap-4 text-xs">
          <div>
            <div className="text-[10px] uppercase text-slate-500">отримано</div>
            <Bars values={data.timeline.map((b) => b.raw)} title={(i, v) => `${fmtTime(data.timeline[i].at)}: ${v}`} />
          </div>
          <div>
            <div className="text-[10px] uppercase text-slate-500">з фактами</div>
            <Bars values={data.timeline.map((b) => b.withFacts)} color="bg-emerald-500" title={(i, v) => `${fmtTime(data.timeline[i].at)}: ${v}`} />
          </div>
          <div>
            <div className="text-[10px] uppercase text-slate-500">помилки</div>
            <Bars values={data.timeline.map((b) => b.failed)} color="bg-red-500" title={(i, v) => `${fmtTime(data.timeline[i].at)}: ${v}`} />
          </div>
          <div>
            <div className="text-[10px] uppercase text-slate-500">без тексту</div>
            <Bars values={data.timeline.map((b) => b.noText)} color="bg-slate-400" title={(i, v) => `${fmtTime(data.timeline[i].at)}: ${v}`} />
          </div>
        </div>
      </Section>

      <Section title="Джерела та надходження">
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="sources">
            <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-1 pr-2">Джерело</th>
                <th className="pr-2 text-right">raw</th>
                <th className="pr-2 text-right">пости</th>
                <th className="pr-2 text-right">редакції</th>
                <th className="pr-2 text-right">без тексту</th>
                <th className="pr-2 text-right">payload</th>
                <th className="pr-2 text-right">факти</th>
                <th className="pr-2 text-right">довжина p50</th>
                <th className="pr-2 text-right">затримка збору p50/p95</th>
                <th className="pr-2 text-right">live/history</th>
                <th className="pr-2 text-right">макс. перерва</th>
              </tr>
            </thead>
            <tbody>
              {data.sources.map((s) => (
                <tr key={s.sourceId} className="border-t border-slate-100 dark:border-slate-800" data-testid={`source-${s.code}`}>
                  <td className="py-1 pr-2 font-mono">{s.code}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.raw)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.posts)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.edits)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.noText)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.withPayload)}</td>
                  <td className="pr-2 text-right font-mono">{fmtNum(s.facts)}</td>
                  <td className="pr-2 text-right font-mono">{s.textLengthP50 == null ? '—' : fmtNum(Math.round(s.textLengthP50))}</td>
                  <td className="pr-2 text-right font-mono">
                    {fmtAge(s.collectDelayP50Seconds)} / {fmtAge(s.collectDelayP95Seconds)}
                  </td>
                  <td className="pr-2 text-right font-mono">
                    {fmtNum(s.live)}/{fmtNum(s.history)}
                  </td>
                  <td className="pr-2 text-right font-mono">{fmtAge(s.maxGapSeconds)}</td>
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

function StatusRow({ status }: { status: LifecycleStatusDto | null }) {
  if (!status) return null
  const r = status.reconciliation
  return (
    <div className="grid gap-3 sm:grid-cols-3" data-testid="lifecycle-status">
      <Stat label="Backfill" value={status.backfill.caughtUp ? 'наздогнав' : `${fmtNum(status.backfill.cursor)} / ${fmtNum(status.backfill.maxRawMessageId)}`} hint={status.backfill.at ? `оновлено ${fmtTime(status.backfill.at)}` : 'ще не запускався'} tone={status.backfill.caughtUp ? 'ok' : 'warn'} />
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
