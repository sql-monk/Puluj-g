import { useCallback, useEffect, useMemo, useState } from 'react'
import { admin, type CollectorStatusDto, type DbQueryResultDto, type DbReportDto, type LogFileDto, type LogTailDto, type OpsOverviewDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { Bars, Stat, ago, fmtBytes, fmtNum, fmtPercent, fmtTime, usePolled } from './shared'

const SERVICE_LABEL: Record<string, string> = {
  worker: 'Worker (збір і обробка)',
  'worker:worker': 'Worker (усе в одному процесі)',
  'worker:processor': 'Processor (обробка повідомлень)',
  'worker:collector-telegram': 'Колектор Telegram',
  'worker:collector-alerts': 'Колектор alerts.in.ua',
  'worker:analytics': 'Analytics (аналітика джерел)',
  api: 'Api (карта, публічна частина)',
  admin: 'Admin (ця панель)',
  collectors: 'Колектори джерел',
  telegram: 'Telegram-сесія',
  postgres: 'PostgreSQL / PostGIS',
}

/** Scaled processor replicas are `worker:processor-<container id>`: labelled by the prefix, the id kept as detail. */
function serviceLabel(name: string): string {
  if (SERVICE_LABEL[name]) return SERVICE_LABEL[name]
  const m = /^worker:(processor|collector-telegram|collector-alerts|analytics|worker)-(.+)$/.exec(name)
  return m ? `${SERVICE_LABEL[`worker:${m[1]}`]} · ${m[2]}` : name
}

function statusOk(status: string): boolean | null {
  return status === 'ok' ? true : status === 'unknown' ? null : false
}

export function OverviewPanel() {
  const { data, error } = usePolled(() => admin.ops.overview(), 10_000)
  return (
    <Section title="Стан компонентів" badge={data && <Badge ok={data.services.every((s) => s.status === 'ok') ? true : data.services.some((s) => s.status === 'down') ? false : null} text={`оновлено ${fmtTime(data.generatedAt)}`} />}>
      {error && <div className="text-xs text-red-600">{error}</div>}
      {!data && !error && <div className="text-xs text-slate-500">Завантаження…</div>}
      {data && (
        <>
          <table className="w-full text-xs">
            <thead className="text-left text-slate-500">
              <tr>
                <th className="py-1 pr-2">Компонент</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2">Деталі</th>
                <th className="pr-2">Востаннє</th>
              </tr>
            </thead>
            <tbody>
              <tr className="border-t border-slate-100 dark:border-slate-800">
                <td className="py-1.5 pr-2">Процесори повідомлень</td>
                <td className="pr-2">
                  <Badge ok={data.processorCount > 0 ? true : false} text={data.processorCount > 0 ? 'ok' : 'down'} />
                </td>
                <td className="pr-2 text-slate-600 dark:text-slate-300">
                  {fmtNum(data.processorCount)} {plural(data.processorCount, 'репліка', 'репліки', 'реплік')} з живим heartbeat ·{' '}
                  <a className="underline" href="#/workers">
                    Воркери
                  </a>
                </td>
                <td className="pr-2 text-slate-500">—</td>
              </tr>
              {data.services.map((s) => (
                <tr key={s.name} className="border-t border-slate-100 dark:border-slate-800">
                  <td className="py-1.5 pr-2">{serviceLabel(s.name)}</td>
                  <td className="pr-2">
                    <Badge ok={statusOk(s.status)} text={s.status} />
                  </td>
                  <td className="pr-2 text-slate-600 dark:text-slate-300">{s.detail ?? '—'}</td>
                  <td className="pr-2 text-slate-500" title={s.lastSeen ? fmtTime(s.lastSeen) : undefined}>
                    {ago(s.lastSeen)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="grid grid-cols-2 gap-2 text-xs sm:grid-cols-4">
            <Stat label="Розмір БД" value={fmtBytes(data.db.sizeBytes)} />
            <Stat label="З’єднань" value={String(data.db.connections)} />
            <Stat label="Міграцій" value={String(data.db.migrationCount)} />
            <Stat label="Остання міграція" value={data.db.lastMigration?.replace(/^\d+_/, '') ?? '—'} />
          </div>
        </>
      )}
    </Section>
  )
}

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10
  const m100 = n % 100
  if (m10 === 1 && m100 !== 11) return one
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few
  return many
}

export function CollectorsPanel() {
  const { data, error } = usePolled(() => admin.ops.collectors(), 15_000)
  const list: CollectorStatusDto[] = data ?? []
  return (
    <Section title="Колектори" badge={<Badge ok={list.length ? list.filter((c) => c.enabled).every((c) => c.consecutiveFailures === 0) : null} text={`${list.filter((c) => c.enabled).length} увімкнених`} />}>
      <p className="text-xs text-slate-500">Кожне джерело окремо: коли опитано, коли останній успіх і повідомлення, помилки поспіль, і скільки повідомлень прийшло за кожну з останніх 24 годин.</p>
      {error && <div className="text-xs text-red-600">{error}</div>}
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead className="text-left text-slate-500">
            <tr>
              <th className="py-1 pr-2">Джерело</th>
              <th className="pr-2">Тип</th>
              <th className="pr-2">Стан</th>
              <th className="pr-2">Опитано</th>
              <th className="pr-2">Успіх</th>
              <th className="pr-2">Повідомлення</th>
              <th className="pr-2">За 24 год</th>
              <th className="pr-2">Помилка</th>
            </tr>
          </thead>
          <tbody>
            {list.map((c) => (
              <tr key={c.sourceId} className={`border-t border-slate-100 dark:border-slate-800 ${c.enabled ? '' : 'opacity-50'}`}>
                <td className="py-1.5 pr-2">
                  {c.name} <span className="text-slate-400">{c.code}</span>
                </td>
                <td className="pr-2">{c.type}</td>
                <td className="pr-2">
                  <Badge ok={!c.enabled ? null : c.consecutiveFailures > 0 ? false : c.lastSuccessAt ? true : null} text={!c.enabled ? 'вимкнено' : c.consecutiveFailures > 0 ? `${c.consecutiveFailures} помилок поспіль` : c.lastSuccessAt ? 'ok' : 'ще не опитано'} />
                </td>
                <td className="pr-2 text-slate-500">{ago(c.lastPolledAt)}</td>
                <td className="pr-2 text-slate-500">{ago(c.lastSuccessAt)}</td>
                <td className="pr-2 text-slate-500">{ago(c.lastMessageAt)}</td>
                <td className="pr-2">
                  <div className="flex items-center gap-2">
                    <Bars values={c.perHour} title={(i, v) => `${23 - i} год тому: ${v}`} />
                    <span className="font-mono">{fmtNum(c.messages24h)}</span>
                  </div>
                </td>
                <td className="max-w-xs truncate pr-2 text-red-600" title={c.lastError}>
                  {c.lastError ?? ''}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  )
}

export function DbPanel() {
  const { data, error, reload } = usePolled(() => admin.ops.db(), 15_000)
  const d: DbReportDto | null = data
  const [selectedTable, setSelectedTable] = useState<string | null>(null)
  const [tableRows, setTableRows] = useState<DbQueryResultDto | null>(null)
  const [tableError, setTableError] = useState<string | null>(null)
  const [loadingTable, setLoadingTable] = useState(false)
  const [sql, setSql] = useState('')
  const [queryResult, setQueryResult] = useState<DbQueryResultDto | null>(null)
  const [queryError, setQueryError] = useState<string | null>(null)
  const [querying, setQuerying] = useState(false)
  const [confirmation, setConfirmation] = useState('')
  const [reprocessing, setReprocessing] = useState(false)
  const [clearConfirmation, setClearConfirmation] = useState('')
  const [clearing, setClearing] = useState(false)
  const [actionMessage, setActionMessage] = useState<string | null>(null)

  const openTable = async (name: string) => {
    setSelectedTable(name)
    setTableRows(null)
    setTableError(null)
    setLoadingTable(true)
    try {
      setTableRows(await admin.ops.dbTableRows(name))
    } catch (e) {
      setTableError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoadingTable(false)
    }
  }

  const runQuery = async () => {
    setQueryError(null)
    setQueryResult(null)
    setQuerying(true)
    try {
      setQueryResult(await admin.ops.dbQuery(sql))
    } catch (e) {
      setQueryError(e instanceof Error ? e.message : String(e))
    } finally {
      setQuerying(false)
    }
  }

  const reprocess = async () => {
    if (confirmation !== 'REPROCESS') return
    if (!window.confirm('Очистити всі похідні дані, зберегти raw_messages і поставити їх у чергу на повторну обробку? Цю дію не можна скасувати.')) return
    setReprocessing(true)
    setActionMessage(null)
    try {
      const result = await admin.ops.reprocess()
      setActionMessage(`У чергу поставлено ${fmtNum(result.queued)} повідомлень${result.analyticsReset ? '; аналітику також очищено.' : '.'}`)
      setConfirmation('')
      reload()
    } catch (e) {
      setActionMessage(`Помилка: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setReprocessing(false)
    }
  }

  const clearOperationalData = async () => {
    if (clearConfirmation !== 'DELETE ALL DATA') return
    if (!window.confirm('Зупинити writers і назавжди видалити всі робочі дані з БД? Схема, налаштування, джерела й довідники залишаться. Після цього writers лишаться зупиненими, доки ви не запустите їх вручну.')) return
    setClearing(true)
    setActionMessage(null)
    try {
      const result = await admin.ops.clearOperationalData()
      setActionMessage(`Видалено робочі дані з ${fmtNum(result.tables)} таблиць; зупинено контейнерів: ${fmtNum(result.stoppedContainers)}.`)
      setClearConfirmation('')
      reload()
    } catch (e) {
      setActionMessage(`Помилка: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setClearing(false)
    }
  }

  return (
    <>
      <Section title="База даних" badge={d && <Badge ok={true} text={fmtBytes(d.sizeBytes)} />}>
        {error && <div className="text-xs text-red-600">{error}</div>}
        {d && (
          <>
            <div className="text-xs text-slate-500">{d.version}</div>
            <div className="flex flex-wrap gap-2 text-xs">
              {d.connections.map((c) => (
                <span key={c.role} className="rounded bg-slate-100 px-2 py-0.5 dark:bg-slate-800">
                  {c.role}: {c.connections} з’єдн.
                </span>
              ))}
            </div>
            <div className="grid grid-cols-2 gap-2 text-xs sm:grid-cols-3 lg:grid-cols-6">
              <Stat label="Активні" value={fmtNum(d.monitoring.activeConnections)} hint={`idle: ${fmtNum(d.monitoring.idleConnections)}`} tone={d.monitoring.activeConnections > 20 ? 'warn' : 'ok'} />
              <Stat label="Кеш PostgreSQL" value={fmtPercent(d.monitoring.cacheHitRatio * 100)} tone={d.monitoring.cacheHitRatio < 0.9 ? 'warn' : 'ok'} />
              <Stat label="Мертві рядки" value={fmtNum(d.monitoring.deadRows)} tone={d.monitoring.deadRows > 100_000 ? 'warn' : undefined} />
              <Stat label="Транзакції" value={fmtNum(d.monitoring.transactionsCommitted)} hint={`rollback: ${fmtNum(d.monitoring.transactionsRolledBack)}`} />
              <Stat label="Таблиць" value={fmtNum(d.tables.length)} />
              <Stat label="Оновлення" value="15 с" hint="автоматично" />
            </div>
            <p className="text-[11px] text-slate-500">Лічильники записів і транзакцій — накопичувальні з моменту останнього скидання статистики PostgreSQL. Три риски в «Активності» — insert / update / delete.</p>
            <div className="overflow-x-auto">
            <table className="w-full text-xs">
              <thead className="text-left text-slate-500">
                <tr>
                  <th className="py-1 pr-2">Таблиця</th>
                  <th className="pr-2">Дані</th>
                  <th className="pr-2">Активність</th>
                  <th className="pr-2">Обслуговування</th>
                  <th className="pr-2" />
                </tr>
              </thead>
              <tbody>
                {d.tables.map((t) => (
                  <tr key={t.name} className="border-t border-slate-100 dark:border-slate-800">
                    <td className="py-1 pr-2 font-mono">{t.name}</td>
                    <td className="min-w-32 pr-2 font-mono">
                      <div>{fmtNum(t.rows)} рядків</div>
                      <InlineMeter value={t.bytes} max={Math.max(...d.tables.map((x) => x.bytes), 1)} label={fmtBytes(t.bytes)} />
                    </td>
                    <td className="min-w-28 pr-2">
                      <div className="flex items-center gap-2">
                        <Bars values={[t.inserts, t.updates, t.deletes]} height={20} width="w-2" title={(i, v) => `${['insert', 'update', 'delete'][i]}: ${fmtNum(v)}`} />
                        <span className="font-mono text-[10px] text-slate-500">{fmtNum(t.inserts)} / {fmtNum(t.updates)} / {fmtNum(t.deletes)}</span>
                      </div>
                    </td>
                    <td className="min-w-32 pr-2 text-[10px] text-slate-500">
                      <div>dead: {fmtNum(t.deadRows)}</div>
                      <div title={t.lastVacuumAt}>{t.lastVacuumAt ? `vacuum ${ago(t.lastVacuumAt)}` : 'vacuum —'}</div>
                      <div title={t.lastAnalyzeAt}>{t.lastAnalyzeAt ? `analyze ${ago(t.lastAnalyzeAt)}` : 'analyze —'}</div>
                    </td>
                    <td className="pr-2 text-right"><button className="rounded border border-slate-300 px-2 py-0.5 text-[11px] dark:border-slate-600" onClick={() => void openTable(t.name)}>Дані</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            </div>
          </>
        )}
      </Section>
      {selectedTable && (
        <Section title={`Дані: ${selectedTable}`} badge={tableRows && <Badge ok={true} text={`${fmtNum(tableRows.rows.length)} рядків`} />}>
          <p className="text-xs text-slate-500">Перші 50 рядків. Значення секретних полів приховано.</p>
          {loadingTable && <div className="text-xs text-slate-500">Завантаження…</div>}
          {tableError && <div className="text-xs text-red-600">{tableError}</div>}
          {tableRows && <QueryResult result={tableRows} />}
        </Section>
      )}
      <Section title="SQL-консоль (лише читання)" badge={<Badge ok={null} text="SELECT / WITH" />}>
        <p className="text-xs text-slate-500">Виконує один <code>SELECT</code> або <code>WITH … SELECT</code> у read-only транзакції, максимум 200 рядків і 10 секунд. Коментарі, системні pg_* функції та секретні поля заблоковані.</p>
        <textarea className="min-h-28 w-full rounded border border-slate-300 p-2 font-mono text-xs dark:border-slate-600 dark:bg-slate-800" spellCheck={false} placeholder="SELECT raw_message_id, source_id, published_at, processing_status FROM raw_messages ORDER BY raw_message_id DESC LIMIT 50" value={sql} onChange={(e) => setSql(e.target.value)} />
        <div className="flex items-center gap-2">
          <button className="rounded bg-slate-800 px-3 py-1 text-xs text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" onClick={() => void runQuery()} disabled={querying || !sql.trim()}>{querying ? 'Виконую…' : 'Виконати SELECT'}</button>
          {queryError && <span className="text-xs text-red-600">{queryError}</span>}
        </div>
        {queryResult && <QueryResult result={queryResult} />}
      </Section>
      <Section title="Повторна обробка повідомлень" badge={<Badge ok={null} text="не запускається автоматично" />}>
        <p className="text-xs text-slate-500">Збереже <code>raw_messages</code>, а похідні дані (цілі, треки, тривоги, зв’язки, помилки обробки, статистику та аналітику) очистить. Усі збережені повідомлення повернуться в чергу для обробки за часом публікації. Джерела, налаштування та довідники не змінюються.</p>
        <label className="block text-xs">Введіть <code>REPROCESS</code> для розблокування дії
          <input className="ml-2 rounded border border-red-300 px-2 py-1 font-mono dark:border-red-800 dark:bg-slate-800" value={confirmation} onChange={(e) => setConfirmation(e.target.value)} />
        </label>
        <div className="flex items-center gap-2">
          <button className="rounded border border-red-300 px-3 py-1 text-xs text-red-700 disabled:opacity-50 dark:border-red-800 dark:text-red-300" disabled={confirmation !== 'REPROCESS' || reprocessing} onClick={() => void reprocess()}>{reprocessing ? 'Очищаю й ставлю в чергу…' : 'Очистити похідні дані та перепроцесити'}</button>
          {actionMessage && <span className={`text-xs ${actionMessage.startsWith('Помилка:') ? 'text-red-600' : 'text-emerald-600'}`}>{actionMessage}</span>}
        </div>
      </Section>
      <Section title="Небезпечна зона" badge={<Badge ok={false} text="безповоротно" />}>
        <p className="text-xs text-slate-500">Зупиняє колектори, processors, messaging та analytics, а потім видаляє всі робочі дані: повідомлення, цілі, треки, тривоги, інциденти, стан обробки, черги й аналітику. Схема БД, міграції, налаштування, доступ адміна, джерела та довідники залишаються.</p>
        <label className="mt-2 block text-xs">Введіть <code>DELETE ALL DATA</code> для розблокування дії
          <input className="ml-2 rounded border border-red-300 px-2 py-1 font-mono dark:border-red-800 dark:bg-slate-800" value={clearConfirmation} onChange={(e) => setClearConfirmation(e.target.value)} />
        </label>
        <div className="mt-2 flex items-center gap-2">
          <button className="rounded bg-red-700 px-3 py-1 text-xs text-white disabled:opacity-50 dark:bg-red-600" disabled={clearConfirmation !== 'DELETE ALL DATA' || clearing} onClick={() => void clearOperationalData()}>{clearing ? 'Зупиняю й видаляю…' : 'Видалити все з бази'}</button>
          {actionMessage && <span className={`text-xs ${actionMessage.startsWith('Помилка:') ? 'text-red-600' : 'text-emerald-600'}`}>{actionMessage}</span>}
        </div>
      </Section>
      {d && (
        <Section title="Міграції" badge={<Badge ok={null} text={`${d.migrations.length}`} />}>
          <ol className="list-inside list-decimal text-xs text-slate-600 dark:text-slate-300">
            {d.migrations.map((m) => (
              <li key={m} className="font-mono">
                {m}
              </li>
            ))}
          </ol>
        </Section>
      )}
    </>
  )
}

function InlineMeter({ value, max, label }: { value: number; max: number; label: string }) {
  const percent = Math.max(2, Math.round((value / max) * 100))
  return <div className="flex items-center gap-1.5" title={label}><div className="h-1.5 w-16 overflow-hidden rounded bg-slate-200 dark:bg-slate-700"><div className="h-full rounded bg-violet-500" style={{ width: `${percent}%` }} /></div><span className="text-[10px]">{label}</span></div>
}

function QueryResult({ result }: { result: DbQueryResultDto }) {
  return (
    <div className="space-y-1">
      <div className="text-[11px] text-slate-500">{fmtNum(result.rows.length)} рядків · {fmtNum(result.elapsedMs)} мс{result.truncated ? ' · показано перші рядки' : ''}</div>
      <div className="max-h-96 overflow-auto rounded border border-slate-200 dark:border-slate-700">
        <table className="w-full text-left text-[11px]">
          <thead className="sticky top-0 bg-slate-50 text-slate-500 dark:bg-slate-800">
            <tr>{result.columns.map((c) => <th key={c} className="whitespace-nowrap px-2 py-1 font-mono">{c}</th>)}</tr>
          </thead>
          <tbody>
            {result.rows.map((row, i) => <tr key={i} className="border-t border-slate-100 dark:border-slate-800">{row.map((value, j) => <td key={j} className="max-w-80 truncate px-2 py-1 font-mono" title={value ?? 'NULL'}>{value ?? <span className="text-slate-400">NULL</span>}</td>)}</tr>)}
          </tbody>
        </table>
      </div>
      {result.rows.length === 0 && <div className="text-xs text-slate-500">Рядків немає.</div>}
    </div>
  )
}

const LEVELS = ['', 'Warning', 'Error']

export function LogsPanel() {
  const [files, setFiles] = useState<LogFileDto[]>([])
  const [file, setFile] = useState<string>('')
  const [filter, setFilter] = useState('')
  const [level, setLevel] = useState('')
  const [lines, setLines] = useState(200)
  const [tail, setTail] = useState<LogTailDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [auto, setAuto] = useState(true)

  useEffect(() => {
    admin.ops
      .logFiles()
      .then((f) => {
        setFiles(f)
        setFile((cur) => cur || f[0]?.name || '')
      })
      .catch((e: Error) => setError(e.message))
  }, [])

  const load = useCallback(() => {
    if (!file) return
    admin.ops
      .logTail(file, lines, filter, level)
      .then((t) => {
        setTail(t)
        setError(null)
      })
      .catch((e: Error) => setError(e.message))
  }, [file, lines, filter, level])

  useEffect(() => {
    load()
    if (!auto) return
    const id = window.setInterval(load, 5_000)
    return () => window.clearInterval(id)
  }, [load, auto])

  const byService = useMemo(() => {
    const m = new Map<string, LogFileDto[]>()
    for (const f of files) m.set(f.service, [...(m.get(f.service) ?? []), f])
    return m
  }, [files])

  return (
    <Section title="Логи" badge={tail && <Badge ok={null} text={`${tail.lines.length} рядків · ${fmtBytes(tail.bytes)}`} />}>
      <p className="text-xs text-slate-500">Кожен сервіс пише свій файл на день у спільну теку logs/. Тут — хвіст файлу; фільтр шукає підрядок у записі, рівень — за позначкою [WRN] / [ERR].</p>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <select className="rounded border border-slate-300 bg-white px-1 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={file} onChange={(e) => setFile(e.target.value)}>
          {[...byService.entries()].map(([svc, fs]) => (
            <optgroup key={svc} label={svc}>
              {fs.map((f) => (
                <option key={f.name} value={f.name}>
                  {f.name} · {fmtBytes(f.bytes)}
                </option>
              ))}
            </optgroup>
          ))}
          {files.length === 0 && <option value="">(файлів немає)</option>}
        </select>
        <input className="w-48 rounded border border-slate-300 px-2 py-0.5 dark:border-slate-600 dark:bg-slate-800" placeholder="фільтр…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <select className="rounded border border-slate-300 bg-white px-1 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={level} onChange={(e) => setLevel(e.target.value)}>
          {LEVELS.map((l) => (
            <option key={l} value={l}>
              {l || 'усі рівні'}
            </option>
          ))}
        </select>
        <select className="rounded border border-slate-300 bg-white px-1 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={lines} onChange={(e) => setLines(Number(e.target.value))}>
          {[100, 200, 500, 1000, 2000].map((n) => (
            <option key={n} value={n}>
              {n} рядків
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> авто-оновлення
        </label>
        <button className="rounded bg-slate-200 px-2 py-0.5 dark:bg-slate-700" onClick={load}>
          Оновити
        </button>
      </div>
      {error && <div className="text-xs text-red-600">{error}</div>}
      <pre className="max-h-[70vh] overflow-auto rounded-lg bg-slate-900 p-3 text-[11px] leading-snug text-slate-100">
        {tail?.lines.map((l, i) => (
          <div key={i} className={l.includes('[ERR]') || l.includes('[FTL]') ? 'text-red-300' : l.includes('[WRN]') ? 'text-amber-200' : l.includes('[DBG]') || l.includes('[VRB]') ? 'text-slate-400' : ''}>
            {l}
          </div>
        ))}
        {tail && tail.lines.length === 0 && <span className="text-slate-400">порожньо</span>}
      </pre>
    </Section>
  )
}

export type { OpsOverviewDto }
