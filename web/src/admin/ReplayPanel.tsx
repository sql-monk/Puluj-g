import { useState } from 'react'
import { adminRuns, type RunAction, type RunDto } from '../api/adminRuns'
import { Badge, Section } from '../components/settings/fields'
import { fmtNum, fmtTime, Loading, usePolled } from './shared'

const STATE: Record<string, string> = {
  created: 'створено',
  running: 'виконується',
  paused: 'пауза',
  verified: 'перевірено',
  promoted: 'активовано',
  rolled_back: 'відкочено',
  cancelled: 'скасовано',
  failed: 'ПОМИЛКА',
  completed: 'завершено',
  superseded: 'замінено',
}

/** Which commands the state machine accepts in a state (mirrors RunService; the server is the authority — a refused command is a 409 shown as is). */
export function actionsFor(state: string): RunAction[] {
  switch (state) {
    case 'created':
      return ['start', 'cancel']
    case 'running':
      return ['pause', 'cancel', 'catchup', 'verify']
    case 'paused':
      return ['resume', 'cancel', 'catchup', 'verify']
    case 'failed':
      return ['resume', 'cancel']
    case 'verified':
      return ['promote', 'catchup', 'cancel']
    case 'promoted':
      return ['rollback']
    default:
      return []
  }
}

/** `datetime-local` value in the browser's zone (what `new Date(value)` parses back); the server stores UTC. */
function localInput(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}

const ACTION_LABEL: Record<RunAction, string> = { start: 'старт', pause: 'пауза', resume: 'продовжити', cancel: 'скасувати', catchup: 'дочитати дельту', verify: 'перевірити', promote: 'активувати', rollback: 'відкотити' }

/** The consequence of a command, spelled out in the confirmation — promote/rollback name the generations they switch between. */
export function confirmText(run: RunDto, action: RunAction, force = false): string {
  const gen = run.generationId ?? '?'
  const v = run.scope?.verification
  switch (action) {
    case 'promote':
      return `Активувати generation ${gen} для ВСІХ incidents (atomic switch): попередня active generation стає невидимою для карти й API; live-writers далі пишуть у ${gen}.${
        v ? ` Перевірка: у generation ${String(v.incidents_in_generation ?? '?')} incidents, в active у цьому вікні ${String(v.active_incidents_in_window ?? '?')}, поза scope ${String(v.active_incidents_outside_scope ?? '?')}, відсутні в generation ${String(v.active_incidents_missing_in_generation ?? '?')}.` : ''
      }${force ? ' FORCE: active incidents поза scope зникнуть з карти до наступного replay.' : ''} Відкат — кнопка «відкотити».`
    case 'rollback':
      return `Повернути active generation ${run.scope?.promoted_from ?? 'попередню (live)'}: generation ${gen} стане невидимою; incidents, які live записав у неї після активації, будуть невидимі до повторного replay цього вікна. Нічого не видаляється.`
    case 'cancel':
      return `Скасувати run ${run.runId}: publisher зупиняється, in-flight replay-доставки доробляються без ефекту (noop). Generation ${gen} лишається неактивною.`
    case 'catchup':
      return `Дочитати дельту: scope розширюється до watermark (зараз − lag), raw, що надійшли після старту run'а, буде опубліковано в replay lane. Лише до promote.`
    case 'verify':
      return `Перевірити run: усі replay-доставки термінальні, карантин порожній, кожен опублікований raw має аналіз; звіт буде показано. Нічого не перемикається.`
    case 'pause':
      return `Призупинити публікацію run'а (checkpoint зберігається; in-flight доставки доробляються).`
    case 'resume':
      return `Продовжити публікацію з checkpoint (${run.checkpoint ? `${fmtNum(run.checkpoint.published)}/${fmtNum(run.checkpoint.total)}` : '—'}).`
    case 'start':
      return `Запустити публікацію ${fmtNum(run.checkpoint?.total ?? 0)} raw у replay lane (generation ${gen}, ізольована; live не зупиняється).`
  }
}

/**
 * Панель «Replay» (P14, §11, ADR-0005): runs з generation/станом/checkpoint, створення replay run'а над scope (джерела, вікно), команди
 * state machine з actor + reason і підтвердженням, що називає generation; звіт verify перед promote. Кольори не єдиний носій — стан словом.
 */
export function ReplayPanel() {
  const { data, error, reload } = usePolled(() => adminRuns.list(), 10_000)
  const [actor, setActor] = useState('')
  const [reason, setReason] = useState('')
  const [from, setFrom] = useState(() => localInput(new Date(Date.now() - 24 * 3600_000)))
  const [to, setTo] = useState(() => localInput(new Date()))
  const [sources, setSources] = useState('')
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [report, setReport] = useState<{ runId: string; report: Record<string, unknown> } | null>(null)
  const [force, setForce] = useState(false)
  const ready = actor.trim().length > 0 && reason.trim().length > 0

  const run = async (what: string, action: () => Promise<unknown>) => {
    setBusy(true)
    setMessage(null)
    try {
      await action()
      setMessage({ ok: true, text: `${what}: виконано (${actor.trim()})` })
      reload()
    } catch (e) {
      setMessage({ ok: false, text: `${what}: ${e instanceof Error ? e.message : String(e)}` })
    } finally {
      setBusy(false)
    }
  }
  const act = async (r: RunDto, action: RunAction) => {
    const useForce = action === 'promote' && force
    if (!window.confirm(confirmText(r, action, useForce))) return
    await run(`${ACTION_LABEL[action]} ${r.runId.slice(0, 8)}`, async () => {
      const res = await adminRuns.act(r.runId, action, actor.trim(), reason.trim(), { force: useForce })
      if (action === 'verify' && res.report) setReport({ runId: r.runId, report: res.report })
    })
  }
  const create = async () => {
    const ids = sources
      .split(',')
      .map((s) => Number(s.trim()))
      .filter((n) => Number.isFinite(n) && n > 0)
    await run('створити replay run', () => adminRuns.createReplay(new Date(from).toISOString(), new Date(to).toISOString(), ids.length ? ids : null, actor.trim(), reason.trim()))
  }

  if (!data) return <Loading error={error} empty />
  const active = data.find((r) => r.generationActive)
  return (
    <div className="space-y-4 text-sm" data-testid="replay-panel">
      <Loading error={error} />
      <Section title="Дія оператора">
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
        <label className="flex items-center gap-1 text-xs">
          <input type="checkbox" checked={force} onChange={(e) => setForce(e.target.checked)} aria-label="force" />
          force promote: активувати generation, навіть якщо active incidents поза scope зникнуть з карти (звіт verify: active_incidents_outside_scope)
        </label>
        {message && (
          <p className={`text-xs ${message.ok ? 'text-emerald-600' : 'text-red-600'}`} role="status">
            {message.text}
          </p>
        )}
      </Section>

      <Section title="Новий replay run" badge={<span className="text-[11px] text-slate-500">один відкритий replay run одночасно</span>}>
        <div className="flex flex-wrap items-end gap-2 text-xs">
          <label>
            Від (локальний час)
            <input className="ml-1 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="від" />
          </label>
          <label>
            До (локальний час)
            <input className="ml-1 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} aria-label="до" />
          </label>
          <label>
            Джерела (id через кому, порожньо = усі)
            <input className="ml-1 w-40 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={sources} onChange={(e) => setSources(e.target.value)} aria-label="джерела" />
          </label>
          <button className="rounded bg-slate-800 px-3 py-1 text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" disabled={!ready || busy} onClick={() => void create()}>
            Створити
          </button>
        </div>
        <p className="text-[11px] text-slate-500">
          Replay публікує raw у lane <code>replay</code> в ізольовану generation: без NOTIFY, без треків/тривог, без LLM (parser поза live не викликає модель — LLM-only incidents після promote зникнуть; звіт verify показує «відсутні в generation»). Промоут — після verify; rollback повертає попередню generation.
        </p>
      </Section>

      <Section title="Runs" badge={active ? <Badge ok text={`active generation: ${active.generationId?.slice(0, 8)}… (${active.kind} run)`} /> : <Badge ok={null} text="active generation: live (без promote)" />}>
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="runs-table">
            <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-1 pr-2">Run</th>
                <th className="pr-2">Тип</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2">Generation</th>
                <th className="pr-2">Scope</th>
                <th className="pr-2 text-right">Checkpoint</th>
                <th className="pr-2">Створено</th>
                <th>Дії</th>
              </tr>
            </thead>
            <tbody>
              {data.map((r) => (
                <tr key={r.runId} className="border-t border-slate-100 align-top dark:border-slate-800" data-testid={`run-${r.runId}`} data-state={r.state}>
                  <td className="py-1 pr-2 font-mono" title={r.runId}>
                    {r.runId.slice(0, 8)}…
                  </td>
                  <td className="pr-2 font-mono">
                    {r.kind}/{r.lane}
                  </td>
                  <td className={`pr-2 ${r.state === 'failed' ? 'font-semibold text-red-600' : r.state === 'promoted' ? 'font-semibold text-emerald-700 dark:text-emerald-300' : ''}`}>
                    {STATE[r.state] ?? r.state}
                    {r.checkpoint?.error && <div className="whitespace-pre-wrap break-words font-mono text-[11px] text-red-700 dark:text-red-300">{r.checkpoint.error}</div>}
                  </td>
                  <td className="pr-2 font-mono" title={r.generationId ?? ''}>
                    {r.generationId ? `${r.generationId.slice(0, 8)}…` : '—'}
                    {r.generationActive ? <span className="ml-1 rounded bg-emerald-100 px-1 text-[10px] text-emerald-800 dark:bg-emerald-900/50 dark:text-emerald-200">active</span> : null}
                  </td>
                  <td className="pr-2">
                    {r.scope?.from ? `${fmtTime(r.scope.from)} → ${fmtTime(r.scope.to)}` : '—'}
                    {r.scope?.source_ids?.length ? <span className="text-slate-500"> · джерела {r.scope.source_ids.join(', ')}</span> : null}
                  </td>
                  <td className="pr-2 text-right font-mono">
                    {r.checkpoint ? `${fmtNum(r.checkpoint.published)}/${fmtNum(r.checkpoint.total)}${r.checkpoint.done ? ' ✓' : ''}` : '—'}
                  </td>
                  <td className="pr-2">
                    {fmtTime(r.createdAt)} <span className="text-slate-500">{r.createdBy}</span>
                  </td>
                  <td className="space-x-1 whitespace-nowrap">
                    {actionsFor(r.state).map((a) => (
                      <button
                        key={a}
                        className={`rounded border px-1.5 py-0.5 disabled:opacity-50 ${a === 'promote' ? 'border-emerald-300 text-emerald-700 dark:border-emerald-800 dark:text-emerald-300' : a === 'rollback' || a === 'cancel' ? 'border-red-300 text-red-700 dark:border-red-800 dark:text-red-300' : 'border-slate-300 dark:border-slate-600'}`}
                        disabled={!ready || busy}
                        onClick={() => void act(r, a)}
                      >
                        {ACTION_LABEL[a]}
                      </button>
                    ))}
                    {r.scope?.verification && (
                      <button className="rounded border border-slate-300 px-1.5 py-0.5 dark:border-slate-600" onClick={() => setReport({ runId: r.runId, report: r.scope!.verification! })}>
                        звіт
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      {report && (
        <Section title={`Звіт verify · ${report.runId.slice(0, 8)}…`} badge={<button className="text-xs underline" onClick={() => setReport(null)}>закрити</button>}>
          <dl className="grid gap-x-4 gap-y-1 text-xs sm:grid-cols-2" data-testid="verify-report">
            {Object.entries(report.report).map(([k, v]) => (
              <div key={k} className="flex justify-between gap-2 border-b border-slate-100 dark:border-slate-800">
                <dt className="font-mono text-slate-500">{k}</dt>
                <dd className="font-mono">{typeof v === 'object' && v !== null ? JSON.stringify(v) : String(v)}</dd>
              </div>
            ))}
          </dl>
        </Section>
      )}
    </div>
  )
}
