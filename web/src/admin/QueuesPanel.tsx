import { useState } from 'react'
import { adminOps, type AlarmDto, type ControlAuditDto, type MessagingOpsDto, type QuarantineRowDto, type SubscriptionLaneOpsDto, type WorkerOpsDto } from '../api/adminOps'
import { Badge, Section } from '../components/settings/fields'
import { ago, fmtMs, fmtNum, fmtTime, Loading, Stat, usePolled } from './shared'

/** Seconds → a short Ukrainian age ("12 с", "3 хв", "1.5 год"). */
export function fmtAge(seconds?: number | null): string {
  if (seconds === null || seconds === undefined || Number.isNaN(seconds)) return '—'
  if (seconds < 90) return `${Math.round(seconds)} с`
  if (seconds < 5400) return `${Math.round(seconds / 60)} хв`
  if (seconds < 172800) return `${(seconds / 3600).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} год`
  return `${(seconds / 86400).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} д`
}

const SEVERITY: Record<string, { label: string; cls: string }> = {
  error: { label: 'ПОМИЛКА', cls: 'border-red-300 bg-red-50 text-red-800 dark:border-red-800 dark:bg-red-900/30 dark:text-red-200' },
  warn: { label: 'УВАГА', cls: 'border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-800 dark:bg-amber-900/30 dark:text-amber-200' },
  info: { label: 'ІНФО', cls: 'border-slate-300 bg-slate-50 text-slate-700 dark:border-slate-600 dark:bg-slate-800/60 dark:text-slate-200' },
}

const LANE_STATE: Record<string, string> = { active: 'активна', paused: 'пауза', draining: 'зливається' }
const LANE_VERB: Record<string, string> = { active: 'відновити', paused: 'призупинити', draining: 'злити' }

/** The exact scope of a lane command, spelled out in the confirmation (ADR-0012: never "the subscription", always "which lane of which subscription"). */
export function laneScope(subscription: string, lane: string, state: 'active' | 'paused' | 'draining', row?: SubscriptionLaneOpsDto | null): string {
  const what = state === 'paused' ? 'Призупинити' : state === 'draining' ? 'Злити (доробити чергу, потім пауза)' : 'Відновити'
  const pending = row ? `, pending ${fmtNum(row.pending)}, in-flight ${fmtNum(row.inFlight)}` : ''
  return `${what}: lane «${lane}» підписки «${subscription}»${pending}. Інші lanes цієї підписки та інші підписки не змінюються. Консюмери реагують протягом ≈5 с; in-flight доставки доробляються (ACK після commit).`
}

/**
 * Панель «Черги» (P13, §9, ADR-0012): підписка × lane з backlog/in-flight/retry/quarantine/віком/лагом/перцентилями і живими
 * консюмерами, alarms зверху (severity словом), broker/outbox/inbox/reconciliation, воркери з їхніми lane-консюмерами (stale/stuck словом),
 * дії pause/resume/drain per lane, retry/waive карантину, scale — кожна з actor + reason і точним scope у підтвердженні.
 */
export function QueuesPanel() {
  const { data, error, reload } = usePolled(() => adminOps.messaging(), 10_000)
  const [actor, setActor] = useState('')
  const [reason, setReason] = useState('')
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [quarantine, setQuarantine] = useState<QuarantineRowDto[] | null>(null)
  const [audit, setAudit] = useState<ControlAuditDto[] | null>(null)
  const [scaleService, setScaleService] = useState<'processor' | 'messaging'>('messaging')
  const [replicas, setReplicas] = useState(1)
  const [showAll, setShowAll] = useState(false)

  const ready = actor.trim().length > 0 && reason.trim().length > 0
  const run = async (what: string, action: () => Promise<unknown>) => {
    setBusy(true)
    setMessage(null)
    try {
      await action()
      setMessage({ ok: true, text: `${what}: виконано (${actor.trim()})` })
      reload()
      if (quarantine) setQuarantine(await adminOps.quarantine())
      if (audit) setAudit(await adminOps.audit())
    } catch (e) {
      setMessage({ ok: false, text: `${what}: ${e instanceof Error ? e.message : String(e)}` })
    } finally {
      setBusy(false)
    }
  }
  const lane = async (row: SubscriptionLaneOpsDto, state: 'active' | 'paused' | 'draining') => {
    if (!window.confirm(laneScope(row.subscription, row.lane, state, row))) return
    await run(`${LANE_VERB[state]} ${row.subscription}/${row.lane}`, () => adminOps.setLane(row.subscription, row.lane, state, actor.trim(), reason.trim()))
  }

  if (!data) return <Loading error={error} empty />
  const rows = showAll ? data.subscriptions : data.subscriptions.filter((s) => s.registryStatus !== 'planned')
  const errors = data.alarms.filter((a) => a.severity === 'error').length
  const warns = data.alarms.filter((a) => a.severity === 'warn').length

  return (
    <div className="space-y-4 text-sm" data-testid="queues-panel">
      <Loading error={error} />
      <Section
        title="Сигнали"
        badge={<Badge ok={errors === 0 ? (warns === 0 ? true : null) : false} text={errors > 0 ? `${errors} помилок, ${warns} попереджень` : warns > 0 ? `${warns} попереджень` : 'без сигналів'} />}
      >
        {data.alarms.length === 0 ? (
          <p className="text-xs text-slate-500">Усе в межах SLO (топологія v{data.topologyVersion}, знімок {fmtTime(data.at)}).</p>
        ) : (
          <ul className="space-y-1" data-testid="alarms">
            {data.alarms.map((a) => (
              <AlarmRow key={`${a.code}@${a.scope}`} alarm={a} />
            ))}
          </ul>
        )}
      </Section>

      <Section title="Дія оператора">
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="text-xs">
            Хто (actor)
            <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={actor} onChange={(e) => setActor(e.target.value)} placeholder="ім’я або служба" aria-label="actor" />
          </label>
          <label className="text-xs">
            Чому (reason)
            <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="причина — потрапляє в аудит" aria-label="reason" />
          </label>
        </div>
        <p className="text-[11px] text-slate-500">Кожна дія потребує actor і reason, показує точний scope у підтвердженні і лишає рядок у control_audit. Реакція консюмерів ≈ 5 с (poll) плюс in-flight.</p>
        {message && (
          <p className={`text-xs ${message.ok ? 'text-emerald-600' : 'text-red-600'}`} role="status">
            {message.text}
          </p>
        )}
      </Section>

      <Section
        title="Підписки × lanes"
        badge={
          <label className="text-[11px] text-slate-500">
            <input type="checkbox" checked={showAll} onChange={(e) => setShowAll(e.target.checked)} /> показати planned
          </label>
        }
      >
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="lanes-table">
            <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-1 pr-2">Підписка</th>
                <th className="pr-2">Lane</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2 text-right">Pending</th>
                <th className="pr-2 text-right">In-flight</th>
                <th className="pr-2 text-right">Retry/год</th>
                <th className="pr-2 text-right">DLQ</th>
                <th className="pr-2 text-right">Найстаріша</th>

                <th className="pr-2 text-right">Завершено/год</th>

                <th>Дії</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((s) => (
                <LaneRow key={`${s.subscription}/${s.lane}`} row={s} slo={data.slo} ready={ready} busy={busy} onLane={lane} />
              ))}
            </tbody>
          </table>
        </div>
        <p className="text-[11px] text-slate-500">
          Джерело лічильників — квитанції в БД (processing.deliveries/attempts/quarantine). «Брокер» — ready/unacked з management API, коли він увімкнений
          {data.broker.management ? (data.broker.management.available ? ' (доступний)' : ` (недоступний: ${data.broker.management.reason ?? '—'})`) : ' (не налаштовано — Messaging:Broker:ManagementUrl)'}.
          Черга з in-flight &gt; 0 ніколи не показується як порожня.
        </p>
      </Section>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Повідомлення/год" value={fmtNum(data.roots.receivedHour)} hint={`завершено ${fmtNum(data.roots.completedHour)}, у роботі ${fmtNum(data.roots.pending)}, потребують уваги ${fmtNum(data.roots.needsAttention)}`} tone={data.roots.needsAttention > 0 ? 'warn' : undefined} />
        <Stat
          label="Брокер"
          value={data.broker.connected === null || data.broker.connected === undefined ? 'немає даних' : data.broker.connected ? 'підключено' : 'НЕ підключено'}
          hint={`${data.broker.connectedWorkers.join(', ') || '—'}${data.broker.disconnectedWorkers.length ? `; без зʼєднання: ${data.broker.disconnectedWorkers.join(', ')}` : ''}${data.broker.management?.available ? `; вузли: ${data.broker.management.nodes.map((n) => `${n.name}${n.running ? '' : ' (down)'}${n.memAlarm ? ' mem!' : ''}${n.diskAlarm ? ' disk!' : ''}`).join(', ')}` : ''}`}
          tone={data.broker.connected === false ? 'bad' : undefined}
        />
        <Stat
          label="Outbox"
          value={`${fmtNum(data.outbox.unconfirmed)} непідтв.`}
          hint={`найстаріший ${fmtAge(data.outbox.oldestAgeSeconds)}, retry ${fmtNum(data.outbox.relayRetries)}, unroutable ${fmtNum(data.outbox.unroutable)}, confirm p50/p95 ${fmtMs(data.outbox.confirmP50Ms)}/${fmtMs(data.outbox.confirmP95Ms)}, опубліковано/год ${fmtNum(data.outbox.publishedHour)}`}
          tone={data.outbox.unroutable > 0 || (data.outbox.oldestAgeSeconds ?? 0) > data.slo.outboxUnconfirmedSeconds ? 'bad' : undefined}
        />
        <Stat label="Inbox / reconciliation" value={`${fmtNum(data.inbox.rowsHour)}/год`} hint={`дублікати придушено ${fmtNum(data.inbox.supersededHour)}, processing ${fmtNum(data.inbox.processing)}; reconciliation ${data.reconciliation ? `${ago(data.reconciliation.at)}: overdue ${fmtNum(data.reconciliation.overdueCount)}, quarantine ${fmtNum(data.reconciliation.quarantineOpen)}` : 'звіту ще немає'}`} />
      </div>

      <Section title="Воркери та їхні консюмери">
        {data.workers.length === 0 ? (
          <p className="text-xs text-slate-500">Жоден воркер не записав heartbeat.</p>
        ) : (
          <ul className="space-y-2" data-testid="workers">
            {data.workers.map((w) => (
              <WorkerRow key={w.name} w={w} />
            ))}
          </ul>
        )}
      </Section>

      <Section title="Карантин (DLQ)" badge={<button className="text-xs underline" onClick={() => void adminOps.quarantine().then(setQuarantine)}>{quarantine ? 'оновити' : 'показати'}</button>}>
        {quarantine && (
          <div className="overflow-x-auto">
            {quarantine.length === 0 ? (
              <p className="text-xs text-slate-500">Відкритих рядків карантину немає.</p>
            ) : (
              <table className="w-full text-xs" data-testid="quarantine-table">
                <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="py-1 pr-2">#</th>
                    <th className="pr-2">Підписка/lane</th>
                    <th className="pr-2">Подія</th>
                    <th className="pr-2">Причина</th>
                    <th className="pr-2">Помилка</th>
                    <th className="pr-2">Коли</th>
                    <th>Дії</th>
                  </tr>
                </thead>
                <tbody>
                  {quarantine.map((q) => (
                    <tr key={q.quarantineId} className="border-t border-slate-100 align-top dark:border-slate-800">
                      <td className="py-1 pr-2 font-mono">{q.quarantineId}</td>
                      <td className="pr-2 font-mono">
                        {q.subscriptionId}/{q.lane}
                      </td>
                      <td className="pr-2 font-mono">
                        {q.eventType ?? '—'}
                        {q.rawMessageId ? (
                          <>
                            {' '}
                            <a className="underline" href={`#/messages?raw=${q.rawMessageId}`}>
                              raw #{q.rawMessageId}
                            </a>
                          </>
                        ) : null}
                      </td>
                      <td className="pr-2">{q.reason}</td>
                      <td className="max-w-md whitespace-pre-wrap break-words pr-2 font-mono text-[11px]">{q.error ?? '—'}</td>
                      <td className="pr-2">{fmtTime(q.quarantinedAt)}</td>
                      <td className="space-x-1 whitespace-nowrap">
                        <button
                          className="rounded border border-slate-300 px-2 py-0.5 disabled:opacity-50 dark:border-slate-600"
                          disabled={!ready || busy}
                          onClick={() => {
                            if (!window.confirm(`Повторити доставку #${q.quarantineId} (${q.subscriptionId}/${q.lane}, подія ${q.eventType ?? '?'}) — лише ця доставка, лише ця підписка. Attempts починаються заново, envelope береться з карантину.`)) return
                            void run(`retry #${q.quarantineId}`, () => adminOps.retry(q.quarantineId, actor.trim(), reason.trim()))
                          }}
                        >
                          retry
                        </button>
                        <button
                          className="rounded border border-red-300 px-2 py-0.5 text-red-700 disabled:opacity-50 dark:border-red-800 dark:text-red-300"
                          disabled={!ready || busy}
                          onClick={() => {
                            if (!window.confirm(`Списати (waive) доставку #${q.quarantineId}: підписка ${q.subscriptionId} отримає термінальну квитанцію «waived» лише для цієї події. Дію видно в аудиті.`)) return
                            void run(`waive #${q.quarantineId}`, () => adminOps.waive(q.quarantineId, actor.trim(), reason.trim()))
                          }}
                        >
                          waive
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </Section>

      <Section title="Масштабування">
        <div className="flex flex-wrap items-end gap-2 text-xs">
          <label>
            Сервіс
            <select className="ml-1 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={scaleService} onChange={(e) => setScaleService(e.target.value as 'processor' | 'messaging')} aria-label="service">
              <option value="messaging">messaging (консюмери шини)</option>
              <option value="processor">processor (legacy)</option>
            </select>
          </label>
          <label>
            Реплік
            <input className="ml-1 w-16 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" type="number" min={0} max={8} value={replicas} onChange={(e) => setReplicas(Number(e.target.value))} aria-label="replicas" />
          </label>
          <button
            className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600"
            disabled={!ready || busy}
            onClick={() => {
              if (!window.confirm(`docker compose up --scale ${scaleService}=${replicas}: змінюється лише сервіс «${scaleService}». Поза Docker дія відхиляється (нічого не імітується).`)) return
              void run(`scale ${scaleService}=${replicas}`, () => adminOps.scale(scaleService, replicas, actor.trim(), reason.trim()))
            }}
          >
            Застосувати
          </button>
        </div>
      </Section>

      <Section title="Backfill / джерела" badge={<span className="text-[11px] text-slate-500">read-only; керування replay — P14</span>}>
        <table className="w-full text-xs">
          <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
            <tr>
              <th className="py-1 pr-2">Джерело</th>
              <th className="pr-2">Останнє повідомлення</th>
              <th className="pr-2">Checkpoint</th>
              <th className="pr-2">Успіх</th>
              <th>Помилки</th>
            </tr>
          </thead>
          <tbody>
            {data.backfill.map((b) => (
              <tr key={b.source} className="border-t border-slate-100 dark:border-slate-800">
                <td className="py-1 pr-2 font-mono">{b.source}</td>
                <td className="pr-2 font-mono">{b.status ?? '—'}</td>
                <td className="max-w-xs truncate pr-2 font-mono text-[11px]" title={b.checkpoint ?? ''}>
                  {b.checkpoint ?? '—'}
                </td>
                <td className="pr-2">{ago(b.lastSuccessAt)}</td>
                <td className={b.consecutiveFailures > 0 ? 'text-red-600' : ''}>{b.consecutiveFailures > 0 ? `${b.consecutiveFailures} поспіль: ${b.lastError ?? ''}` : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title="Аудит контролів" badge={<button className="text-xs underline" onClick={() => void adminOps.audit().then(setAudit)}>{audit ? 'оновити' : 'показати'}</button>}>
        {audit && (
          <ul className="space-y-0.5 text-xs" data-testid="audit">
            {audit.length === 0 && <li className="text-slate-500">Порожньо.</li>}
            {audit.map((a) => (
              <li key={a.auditId} className="font-mono">
                {fmtTime(a.at)} · {a.action} · {a.subscriptionId ?? '—'}
                {a.lane ? `/${a.lane}` : ''} · {a.actor}: <span className="font-sans">{a.reason}</span>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  )
}

function AlarmRow({ alarm }: { alarm: AlarmDto }) {
  const sev = SEVERITY[alarm.severity] ?? SEVERITY.info
  return (
    <li className={`flex flex-wrap items-baseline gap-2 rounded border px-2 py-1 text-xs ${sev.cls}`} data-severity={alarm.severity} data-code={alarm.code}>
      <span className="font-semibold">{sev.label}</span>
      <span className="font-mono">{alarm.code}</span>
      <span className="font-mono text-[11px]">{alarm.scope}</span>
      <span>{alarm.message}</span>
    </li>
  )
}

const SUBSCRIPTION_HELP: Record<string, string> = {
  'raw-writer': 'Надійно зберігає вхідні повідомлення як raw-дані та публікує подію raw.stored для наступних етапів.',
  normalizer: 'Нормалізує збережене повідомлення: готує текст і метадані для розпізнавання.',
  parser: 'Розпізнає нормалізований текст за правилами та, коли потрібно, створює запит до LLM.',
  'llm-worker': 'Виконує запити до LLM для складного розпізнавання й повертає результат або фінальну помилку.',
  finalizer: 'Збирає результат розпізнавання, записує observations і завершує аналіз повідомлення.',
  'track-worker': 'Оновлює агрегати рухомих цілей і обробляє запити на закриття застарілих треків.',
  'alert-worker': 'Оновлює стани тривог та обробляє запити watchdog на завершення інтервалів.',
  'incident-worker': 'Створює й оновлює інциденти на основі зафіксованих observations.',
  projection: 'Передає зміни треків, тривог та інцидентів у проєкції й realtime-оновлення для клієнтів.',
  'message-analytics': 'Будує життєвий цикл повідомлення для аналітики: етапи, очікувані гілки та завершення.',
  archive: 'Зберігає незмінний архів транспортних подій; це обов’язкова гілка для відтворення та аудиту.',
}

function subscriptionHelp(subscription: string): string {
  return SUBSCRIPTION_HELP[subscription] ?? `Обробник підписки «${subscription}»: отримує події своєї черги для цього lane.`
}
function LaneRow({ row, slo, ready, busy, onLane }: { row: SubscriptionLaneOpsDto; slo: MessagingOpsDto['slo']; ready: boolean; busy: boolean; onLane: (row: SubscriptionLaneOpsDto, state: 'active' | 'paused' | 'draining') => Promise<void> }) {
  const [helpOpen, setHelpOpen] = useState(false)
  const state = row.laneState.state
  const over = (row.oldestPendingAgeSeconds ?? 0) > (slo.oldestAgeSeconds[row.lane] ?? 300)
  const stateText = `${LANE_STATE[state] ?? state}${state !== 'active' ? ` (${row.laneState.actor ?? '?'}: ${row.laneState.reason ?? '—'})` : ''}`
  const notEmpty = row.pending > 0 || row.inFlight > 0
  return (
    <tr className="border-t border-slate-100 dark:border-slate-800" data-testid={`lane-${row.subscription}-${row.lane}`} data-state={state}>
      <td className="relative py-1 pr-2 font-mono">
        <span>{row.subscription}</span>
        {row.required ? '' : ' (opt)'}
        {row.registryStatus !== 'active' ? ` [${row.registryStatus}]` : ''}
        <button
          type="button"
          className="ml-1 inline-flex size-4 items-center justify-center rounded-full border border-slate-400 font-sans text-[10px] font-bold text-slate-600 hover:border-slate-600 hover:text-slate-900 dark:border-slate-500 dark:text-slate-300 dark:hover:border-slate-300 dark:hover:text-white"
          aria-label={`Призначення черги ${row.subscription}/${row.lane}`}
          aria-expanded={helpOpen}
          onClick={() => setHelpOpen((open) => !open)}
        >
          ?
        </button>
        {helpOpen && (
          <div className="absolute left-0 top-full z-20 mt-1 w-72 rounded border border-slate-300 bg-white p-2 font-sans text-[11px] leading-snug text-slate-700 shadow-lg dark:border-slate-600 dark:bg-slate-800 dark:text-slate-200" role="tooltip">
            <span className="font-semibold">{row.subscription}/{row.lane}</span>: {subscriptionHelp(row.subscription)}
          </div>
        )}
      </td>
      <td className="pr-2 font-mono">{row.lane}</td>
      <td className={`pr-2 ${state !== 'active' ? 'text-amber-700 dark:text-amber-300' : ''}`} title={row.laneState.changedAt ? fmtTime(row.laneState.changedAt) : ''}>
        {stateText}
      </td>
      <td className={`pr-2 text-right font-mono ${over ? 'text-red-600' : ''}`}>{fmtNum(row.pending)}</td>
      <td className="pr-2 text-right font-mono" title={row.oldestRunningAttemptAgeSeconds ? `найстаріший running ${fmtAge(row.oldestRunningAttemptAgeSeconds)}` : ''}>
        {fmtNum(row.inFlight)}
      </td>
      <td className="pr-2 text-right font-mono" title={`admin retry ${fmtNum(row.adminRetryHour)}`}>
        {fmtNum(row.retryHour)}
      </td>
      <td className={`pr-2 text-right font-mono ${row.quarantined > 0 ? 'text-red-600' : ''}`}>{fmtNum(row.quarantined)}</td>
      <td className={`pr-2 text-right ${over ? 'text-red-600' : ''}`}>{notEmpty ? fmtAge(row.oldestPendingAgeSeconds) : 'порожня'}</td>

      <td className="pr-2 text-right font-mono" title={`noop ${fmtNum(row.noopHour)}, quarantined ${fmtNum(row.failedHour)}, очікувалось ${fmtNum(row.expectedHour)}`}>
        {fmtNum(row.completedHour)}
      </td>

      <td className="space-x-1 whitespace-nowrap">
        {state !== 'paused' && (
          <button className="rounded border border-slate-300 px-1.5 py-0.5 disabled:opacity-50 dark:border-slate-600" disabled={!ready || busy} onClick={() => void onLane(row, 'paused')}>
            пауза
          </button>
        )}
        {state === 'active' && (
          <button className="rounded border border-slate-300 px-1.5 py-0.5 disabled:opacity-50 dark:border-slate-600" disabled={!ready || busy} onClick={() => void onLane(row, 'draining')}>
            злити
          </button>
        )}
        {state !== 'active' && (
          <button className="rounded border border-emerald-300 px-1.5 py-0.5 text-emerald-700 disabled:opacity-50 dark:border-emerald-800 dark:text-emerald-300" disabled={!ready || busy} onClick={() => void onLane(row, 'active')}>
            відновити
          </button>
        )}
      </td>
    </tr>
  )
}

function WorkerRow({ w }: { w: WorkerOpsDto }) {
  const tone = w.stuck ? 'text-red-600 dark:text-red-400' : w.stale ? 'text-amber-600 dark:text-amber-400' : 'text-emerald-600 dark:text-emerald-400'
  const label = w.stuck ? 'ЗАВИС' : w.stale ? 'застарів' : 'живий'
  return (
    <li className="rounded border border-slate-200 px-2 py-1 text-xs dark:border-slate-700" data-testid={`worker-${w.name}`} data-stuck={w.stuck}>
      <div className="flex flex-wrap items-baseline gap-2">
        <span className="font-mono font-semibold">{w.name}</span>
        <span className={`font-semibold ${tone}`}>{label}</span>
        <span className="text-slate-500">heartbeat {ago(w.heartbeatAt)}</span>
        {w.runningAttempts > 0 && <span className="text-slate-500">running attempts {w.runningAttempts}</span>}
        <span className="text-slate-500">завершено/год {fmtNum(w.completedHour)}</span>
        {w.broker && <span className={w.broker.connected ? 'text-slate-500' : 'text-red-600'}>брокер {w.broker.connected ? 'підключено' : 'НЕ підключено'}</span>}
        {w.llm?.pausedUntil && <span className="text-amber-600">LLM пауза до {fmtTime(w.llm.pausedUntil)}</span>}
        {w.roles.length > 0 && <span className="text-slate-400">{w.roles.join(', ')}</span>}
      </div>
      {w.lastError && (
        <div className="mt-0.5 whitespace-pre-wrap break-words font-mono text-[11px] text-red-700 dark:text-red-300">
          остання помилка {fmtTime(w.lastErrorAt)}: {w.lastError}
        </div>
      )}
      {w.consumers.length > 0 && (
        <div className="mt-1 flex flex-wrap gap-1">
          {w.consumers.map((c) => (
            <span key={c.consumerTag} className={`rounded px-1.5 py-0.5 font-mono text-[11px] ${c.consuming ? 'bg-slate-100 dark:bg-slate-800' : 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-200'}`} title={`tag ${c.consumerTag}, prefetch ${c.prefetch}, delivered ${c.delivered}, duplicates ${c.duplicates}, requeued ${c.requeued}`}>
              {c.subscription}/{c.lane}: {c.consuming ? 'споживає' : LANE_STATE[c.state] ?? c.state}
              {c.inFlight > 0 ? ` (${c.inFlight} in-flight)` : ''}
            </span>
          ))}
        </div>
      )}
    </li>
  )
}
