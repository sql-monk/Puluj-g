import { useState } from 'react'
import { admin, AdminError, type ContainerActionResultDto, type ContainerDto, type ContainersDto, type WorkerInstanceDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { ConfirmButton, Loading, Stat, ago, fmtBytes, fmtDuration, fmtMs, fmtNum, fmtPercent, fmtTime, secondsSince, usePolled } from './shared'
import { KIND_LABEL, SERVICE_NOTE, STAGES, instanceHealth, processorBadge, processorSummary, timingBars } from './workers'

const CONTAINER_STATE_LABEL: Record<string, string> = {
  running: 'працює',
  exited: 'зупинено',
  restarting: 'перезапуск',
  paused: 'пауза',
  created: 'створено',
  dead: 'мертвий',
}

/**
 * Every running instance (heartbeat + its own status document), the processors block with scaling, and the containers
 * of the compose stack with restart / stop. Polls every 5 s; container buttons appear only when the panel runs in Docker.
 */
export function WorkersPanel() {
  const workers = usePolled(() => admin.ops.workers(), 5_000)
  const containers = usePolled(() => admin.ops.containers(), 5_000)
  const [result, setResult] = useState<ContainerActionResultDto | null>(null)
  const refresh = () => {
    workers.reload()
    containers.reload()
  }
  const act = async (c: ContainerDto, verb: 'restart' | 'stop' | 'start') => {
    setResult(null)
    try {
      setResult(await admin.ops.containerAction(c.id, verb))
    } catch (e) {
      setResult(errorResult(e))
    } finally {
      refresh()
    }
  }
  const list = workers.data ?? []
  return (
    <>
      <ProcessorsBlock workers={workers.data} workersError={workers.error} containers={containers.data} containersError={containers.error} />
      <Section title="Інстанси" badge={workers.data && <Badge ok={list.length === 0 ? null : list.every((w) => w.alive)} text={`${list.filter((w) => w.alive).length} з ${list.length} живі`} />}>
        <p className="text-xs text-slate-500">Кожен процес Worker / Analytics пише heartbeat (30 с) і статус про себе (10 с) у app_settings. Оброблено за 24 год — з бази за claimed_by; таймінги — за останні 5 хв самого інстансу.</p>
        <Loading error={workers.error} empty={!workers.data} />
        {workers.data && list.length === 0 && <div className="text-xs text-slate-500">Жоден інстанс ще не записав heartbeat.</div>}
        <div className="grid gap-3 lg:grid-cols-2">
          {list.map((w) => (
            <InstanceCard key={w.name} w={w} container={containerOf(w, containers.data)} docker={containers.data?.available ?? false} onAct={act} />
          ))}
        </div>
      </Section>
      <ContainersBlock data={containers.data} error={containers.error} onAct={act} />
      {result && <ActionResult result={result} onClose={() => setResult(null)} />}
    </>
  )
}
function containerOf(w: WorkerInstanceDto, containers: ContainersDto | null): ContainerDto | undefined {
  if (!w.containerId || !containers) return undefined
  return containers.containers.find((c) => c.id === w.containerId)
}

function errorResult(e: unknown): ContainerActionResultDto {
  if (e instanceof AdminError) {
    const body = e.body as Partial<ContainerActionResultDto> | undefined
    return { ok: false, message: body?.message ?? e.message, output: body?.output ?? '' }
  }
  return { ok: false, message: (e as Error).message, output: '' }
}

function ActionResult({ result, onClose }: { result: ContainerActionResultDto; onClose: () => void }) {
  return (
    <div className={`rounded-lg border p-3 text-xs ${result.ok ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-900 dark:bg-emerald-900/20' : 'border-red-200 bg-red-50 dark:border-red-900 dark:bg-red-900/20'}`}>
      <div className="flex items-start justify-between gap-2">
        <span className={result.ok ? 'text-emerald-800 dark:text-emerald-200' : 'text-red-800 dark:text-red-200'}>{result.message}</span>
        <button className="text-slate-400 hover:text-slate-600" onClick={onClose} title="Сховати">
          ✕
        </button>
      </div>
      {result.output && <pre className="mt-2 max-h-60 overflow-auto whitespace-pre-wrap break-all rounded bg-slate-900 p-2 text-[11px] text-slate-100">{result.output}</pre>}
    </div>
  )
}

/**
 * The single processor container and its bounded internal workers. Every value is shown only from a received answer:
 * while a request is pending the cards say "…", never "ні" / 0 / "Docker недоступний".
 */
function ProcessorsBlock({ workers, workersError, containers, containersError }: { workers: WorkerInstanceDto[] | null; workersError: string | null; containers: ContainersDto | null; containersError: string | null }) {
  const badge = processorBadge(workers, workersError)
  const summary = workers ? processorSummary(workers) : null
  const pending = workersError ? 'невідомо' : '…'
  const containersPending = containersError ? 'невідомо' : '…'
  return (
    <Section title="Процесор повідомлень" badge={<Badge ok={badge.ok} text={badge.text} />}>
      <div className="grid grid-cols-1 gap-2 text-xs min-[420px]:grid-cols-2 sm:grid-cols-4">
        <Stat label="Живий" value={summary ? (summary.alive > 0 ? 'так' : 'ні') : pending} />
        <Stat label="Внутрішніх workers" value={summary ? (summary.concurrency ? String(summary.concurrency) : '—') : pending} hint="Processing:Concurrency" />
        <Stat label="Повідомлень/хв" value={summary ? fmtNum(Math.round(summary.perMinute * 10) / 10) : pending} hint="за останні 5 хв" />
        <Stat label="Контейнерів processor" value={containers ? (containers.available ? String(containers.processorReplicas) : '—') : containersPending} hint={!containers ? 'завантаження…' : containers.available ? 'запущених у compose' : 'Docker недоступний'} />
      </div>
      {workersError && <div className="text-xs text-red-600">{workersError}</div>}
      {containers && !containers.available && <div className="text-xs text-slate-500">{containers.unavailable ?? 'Керування контейнерами недоступне.'}</div>}
    </Section>
  )
}
function InstanceCard({ w, container, docker, onAct }: { w: WorkerInstanceDto; container?: ContainerDto; docker: boolean; onAct: (c: ContainerDto, verb: 'restart' | 'stop' | 'start') => Promise<void> }) {
  const health = instanceHealth(w)
  const s = w.status
  const p = s?.processing
  return (
    <div className={`space-y-2 rounded-lg border p-3 text-xs ${w.alive ? 'border-slate-200 dark:border-slate-700' : 'border-amber-300 bg-amber-50/40 dark:border-amber-800 dark:bg-amber-900/10'}`}>
      <div className="flex items-start justify-between gap-2">
        <div>
          <div className="font-mono text-sm font-semibold">{w.name}</div>
          <div className="text-slate-500">{KIND_LABEL[w.kind] ?? w.kind}</div>
        </div>
        <Badge ok={health.ok} text={health.text} />
      </div>
      <div className="flex flex-wrap gap-x-3 gap-y-1 text-slate-600 dark:text-slate-300">
        <span title={s ? `зібрано ${fmtTime(s.builtAt)}` : undefined}>
          версія <b className="font-mono">{s?.version ?? '—'}</b>
          {s && <span className="text-slate-400"> · {fmtTime(s.builtAt)}</span>}
        </span>
        <span>
          uptime <b className="font-mono">{s ? fmtDuration(s.startedAt) : '—'}</b>
        </span>
        <span>
          heartbeat <b className="font-mono">{ago(w.heartbeatAt)}</b>
        </span>
        {s && (
          <span>
            ролі <b className="font-mono">{s.roles.join(', ') || '—'}</b>
          </span>
        )}
      </div>
      {s && (
        <div className="flex flex-wrap gap-x-3 gap-y-1 text-slate-500">
          <span>host {s.host}</span>
          <span>pid {s.pid}</span>
          <span>cpu {fmtPercent(s.cpuPercent)}</span>
          <span>rss {fmtBytes(s.workingSetBytes)}</span>
          <span>потоків {s.threads}</span>
        </div>
      )}
      {p && (
        <>
          <div className="grid grid-cols-1 gap-2 min-[420px]:grid-cols-2 sm:grid-cols-4">
            <Stat label="Воркерів" value={String(p.concurrency)} />
            <Stat label="Оброблено / 24 год" value={fmtNum(w.processed24h)} hint={`з запуску: ${fmtNum(p.processed)}`} />
            <Stat label="за хв (1 / 5)" value={`${p.perMinute1.toLocaleString('uk-UA', { maximumFractionDigits: 1 })} / ${p.perMinute5.toLocaleString('uk-UA', { maximumFractionDigits: 1 })}`} />
            <Stat label="Пропущено / помилок" value={`${fmtNum(p.skipped)} / ${fmtNum(p.failed)}`} hint={`повторів ${fmtNum(p.retried)}, тимчасових ${fmtNum(p.retriedTransient)}`} tone={p.failed > 0 ? 'warn' : undefined} />
          </div>
          <Timings p={p} />
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-slate-500">у роботі ({w.inProgress}):</span>
            {p.claims.length === 0 && <span className="text-slate-400">нічого</span>}
            {p.claims.map((c) => (
              <span key={c.rawMessageId} className="rounded bg-slate-100 px-1.5 py-0.5 font-mono dark:bg-slate-800" title={`з ${fmtTime(c.since)}`}>
                #{c.rawMessageId} · {secondsSince(c.since)} с
              </span>
            ))}
          </div>
          {p.lastProcessedAt && (
            <div className="text-slate-500">
              останнє: #{p.lastRawMessageId} {ago(p.lastProcessedAt)}
            </div>
          )}
        </>
      )}
      {s?.llm && (
        <div className="text-slate-600 dark:text-slate-300">
          LLM: {s.llm.enabled ? <b>{s.llm.model}</b> : 'вимкнено'}
          {s.llm.enabled && ` · викликів ${fmtNum(s.llm.calls)}, невдач ${fmtNum(s.llm.failures)}`}
          {s.llm.pausedUntil && new Date(s.llm.pausedUntil).getTime() > Date.now() && (
            <span className="ml-1 text-amber-700 dark:text-amber-300">
              · пауза до {fmtTime(s.llm.pausedUntil)}
              {s.llm.pauseReason ? ` (${s.llm.pauseReason})` : ''}
            </span>
          )}
        </div>
      )}
      {s?.paused && <PauseNotice reason={s.pause?.reason ?? s.paused} sourceStatus={s.pause?.sourceStatus} />}
      {(w.containerName || container) && (
        <div className="flex flex-wrap items-center gap-2 border-t border-slate-100 pt-2 dark:border-slate-800">
          <span className="text-slate-500">контейнер</span>
          <span className="font-mono">{w.containerName}</span>
          <Badge ok={w.containerState === 'running' ? true : w.containerState ? false : null} text={CONTAINER_STATE_LABEL[w.containerState ?? ''] ?? w.containerState ?? '?'} />
          {w.cpuPercent !== undefined && <span className="text-slate-500">cpu {fmtPercent(w.cpuPercent)}</span>}
          {w.memoryBytes !== undefined && <span className="text-slate-500">mem {fmtBytes(w.memoryBytes)}</span>}
          {docker && container?.controllable && <ContainerButtons c={container} onAct={onAct} />}
        </div>
      )}
    </div>
  )
}
function PauseNotice({ reason, sourceStatus }: { reason: string; sourceStatus?: string }) {
  const history = /^history load: (\d+) channel\(s\) since (\d{4})-(\d{2})-(\d{2})$/.exec(reason)
  if (!history) {
    return (
      <div className="rounded bg-amber-50 p-2 text-amber-800 dark:bg-amber-900/30 dark:text-amber-200">
        <div className="font-medium">Обробку тимчасово призупинено</div>
        <div>Причина: {reason}</div>
        {sourceStatus && <div className={sourceStatus.startsWith('error:') ? 'mt-1 text-red-700 dark:text-red-300' : 'mt-1'}>Стан джерела: {sourceStatus}</div>}
      </div>
    )
  }
  const [, count, year, month, day] = history
  const channels = Number(count) === 1 ? 'каналу' : 'каналів'
  const current = sourceStatus?.startsWith('history: ') ? sourceStatus.slice('history: '.length) : undefined
  const error = sourceStatus?.startsWith('error: ') ? sourceStatus.slice('error: '.length) : undefined
  return (
    <div className="rounded bg-amber-50 p-2 text-amber-800 dark:bg-amber-900/30 dark:text-amber-200">
      <div className="font-medium">Обробку тимчасово призупинено для історії Telegram</div>
      <div>Дочитується історія {count} {channels} від {day}.{month}.{year}, щоб потім обробити всі повідомлення у правильному порядку.</div>
      {current && <div className="mt-1">Зараз: {current}</div>}
      {error && <div className="mt-1 text-red-700 dark:text-red-300">Помилка Telegram: {error}</div>}
      {!error && <div className="mt-1">Після завершення обробка та live-повідомлення відновляться автоматично.</div>}
    </div>
  )
}

function Timings({ p }: { p: NonNullable<NonNullable<WorkerInstanceDto['status']>['processing']> }) {
  const bars = timingBars({ parse: p.parse, lock: p.lock, store: p.store, total: p.total })
  return (
    <table className="w-full">
      <tbody>
        {STAGES.map(({ key, label }) => {
          const t = p[key]
          return (
            <tr key={key}>
              <td className="w-12 py-0.5 pr-2 text-slate-500">{label}</td>
              <td className="py-0.5 pr-2">
                <div className="relative h-3 w-full rounded bg-slate-100 dark:bg-slate-800" title={`p50 ${fmtMs(t.p50Ms)} · p90 ${fmtMs(t.p90Ms)} · max ${fmtMs(t.maxMs)} · ${t.samples} вимірів`}>
                  <div className="absolute inset-y-0 left-0 rounded bg-sky-200 dark:bg-sky-900" style={{ width: `${bars[key].p90 * 100}%` }} />
                  <div className="absolute inset-y-0 left-0 rounded bg-sky-500" style={{ width: `${bars[key].p50 * 100}%` }} />
                </div>
              </td>
              <td className="w-32 whitespace-nowrap py-0.5 text-right font-mono text-slate-600 dark:text-slate-300">
                {fmtMs(t.p50Ms)} <span className="text-slate-400">/ {fmtMs(t.p90Ms)}</span>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

function ContainerButtons({ c, onAct }: { c: ContainerDto; onAct: (c: ContainerDto, verb: 'restart' | 'stop' | 'start') => Promise<void> }) {
  const note = SERVICE_NOTE[c.service] ?? 'сервіс буде недоступний, поки контейнер не запуститься знову'
  if (c.state !== 'running') {
    return <ConfirmButton label="Запустити" confirm={`Запустити контейнер ${c.name}?`} onClick={() => onAct(c, 'start')} />
  }
  return (
    <>
      <ConfirmButton label="Перезапустити" confirm={`Перезапустити ${c.name}? На кілька секунд ${note}.`} onClick={() => onAct(c, 'restart')} />
      <ConfirmButton label="Зупинити" confirm={`Зупинити ${c.name}? Поки контейнер зупинено, ${note}. Запустити знову можна тут же кнопкою «Запустити».`} onClick={() => onAct(c, 'stop')} danger />
    </>
  )
}

function ContainersBlock({ data, error, onAct }: { data: ContainersDto | null; error: string | null; onAct: (c: ContainerDto, verb: 'restart' | 'stop' | 'start') => Promise<void> }) {
  return (
    <Section title="Контейнери" badge={data && <Badge ok={data.available ? (data.containers.every((c) => c.state === 'running' || c.service === 'migrate') ? true : null) : null} text={data.available ? `${data.containers.filter((c) => c.state === 'running').length} з ${data.containers.length} працюють · проєкт ${data.project}` : 'недоступно'} />}>
      <Loading error={error} empty={!data && !error} />
      {data && !data.available && <div className="text-xs text-slate-500">{data.unavailable}</div>}
      {data?.available && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-left text-slate-500">
              <tr>
                <th className="py-1 pr-2">Сервіс</th>
                <th className="pr-2">Контейнер</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2">CPU</th>
                <th className="pr-2">Пам’ять</th>
                <th className="pr-2">Створено</th>
                <th className="pr-2">Образ</th>
                <th className="pr-2">Дії</th>
              </tr>
            </thead>
            <tbody>
              {data.containers.map((c) => (
                <tr key={c.id} className={`border-t border-slate-100 dark:border-slate-800 ${c.state === 'running' ? '' : 'opacity-70'}`}>
                  <td className="py-1.5 pr-2">
                    {c.service || '—'}
                    {c.replicaNumber !== undefined && c.service === 'processor' && <span className="text-slate-400"> #{c.replicaNumber}</span>}
                  </td>
                  <td className="pr-2 font-mono" title={c.id}>
                    {c.name}
                  </td>
                  <td className="pr-2">
                    <Badge ok={c.state === 'running' ? true : c.service === 'migrate' && c.state === 'exited' ? null : false} text={`${CONTAINER_STATE_LABEL[c.state] ?? c.state} · ${c.status}`} />
                  </td>
                  <td className="pr-2 font-mono">{fmtPercent(c.cpuPercent)}</td>
                  <td className="pr-2 font-mono" title={c.memoryLimitBytes ? `ліміт ${fmtBytes(c.memoryLimitBytes)}` : undefined}>
                    {fmtBytes(c.memoryBytes)}
                  </td>
                  <td className="pr-2 text-slate-500" title={fmtTime(c.startedAt)}>
                    {ago(c.startedAt)}
                  </td>
                  <td className="pr-2 font-mono text-slate-500">{c.image}</td>
                  <td className="whitespace-nowrap pr-2">
                    {c.controllable ? (
                      <span className="flex gap-1">
                        <ContainerButtons c={c} onAct={onAct} />
                      </span>
                    ) : (
                      <span className="text-slate-400">керується лише з консолі</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  )
}
