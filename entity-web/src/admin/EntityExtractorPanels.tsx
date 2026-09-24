/* oxlint-disable react/set-state-in-effect -- initial loads synchronize the admin panels with EE HTTP state. */
import { Fragment, useEffect, useState } from 'react'
import { entityAdmin, type EeDefinition, type EeDelivery, type EeDeliveryStatus, type EeExtractor, type EeField, type EeMapConfig, type EeRun, type EeSetting } from '../api/entityAdmin'
import { Badge, Section } from '../components/settings/fields'
import { changedSettings, deliveryState, extractionOffNote, resultLabel, runState, SETTING_SOURCE } from './ee'
import PythonEditor, { type PythonDiagnostic } from './PythonEditor'
import EntityIcon from '../components/EntityIcon'
import { entityIconChoices, entityIconSvg, entityLabel, isRetiredEntity } from '../entities/presentation'
import { Freshness, Loading, PagerButtons, Stat, ago, fmtNum, fmtTime, usePaged, usePolled } from './shared'

const defaultCode = `def extract(message, write):\n    text = message.get("text", "")\n    # write("explosions", {"occurredAt": message.get("publishedAt"), "place": "Київ"})\n`
const fieldTypes = ['text', 'integer', 'decimal', 'boolean', 'datetime', 'json', 'point', 'line', 'polygon']
function JsonBlock({ value }: { value: unknown }) { return <pre className="max-h-72 overflow-auto rounded bg-slate-950 p-3 text-xs text-slate-100">{JSON.stringify(value, null, 2)}</pre> }
function Button({ children, ...props }: React.ButtonHTMLAttributes<HTMLButtonElement>) { return <button {...props} className={`rounded bg-slate-800 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900 ${props.className ?? ''}`}>{children}</button> }

const input = 'rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800'
const card = 'rounded-xl bg-white p-4 shadow dark:bg-slate-900'

/**
 * Entity Extractor operations: its state in plain words (reachable, processing, failures by age, LLM switched off on purpose),
 * its settings with the applied values, and the delivery queue / processing runs as filterable, pageable tables.
 * The raw rows stay one click away ("Деталі").
 */
export function EeOperationsPanel() {
  const overview = usePolled(() => entityAdmin.overview(), 10_000)
  const o = overview.data
  const q = o?.queue
  return (
    <div className="space-y-4">
      <Section title="Entity Extractor" badge={o ? <Badge ok={statusOk(o.status.status)} text={STATUS_TEXT[o.status.status] ?? o.status.status} /> : <Badge ok={null} text="завантаження…" />}>
        <Loading error={overview.error} empty={!o} />
        {o && q && (
          <>
            <div className="grid grid-cols-1 gap-2 text-xs min-[420px]:grid-cols-2 sm:grid-cols-4">
              <Stat label="Сервіс" value={o.extractor.available ? 'доступний' : 'недоступний'} hint={o.extractor.available ? `health → ${o.extractor.statusCode}` : o.extractor.error ?? `HTTP ${o.extractor.statusCode ?? '—'}`} tone={o.extractor.available ? 'ok' : 'bad'} />
              <Stat label="У черзі" value={fmtNum(q.queued)} hint={q.oldestQueuedAt ? `найстаріше ${ago(q.oldestQueuedAt)}` : 'черга порожня'} />
              <Stat label="Обробляється" value={fmtNum(q.inProgress)} hint={`остання успішна доставка ${ago(q.lastSuccessAt)}`} />
              <Stat label="LLM" value={o.llmEnabled ? 'увімкнено' : 'вимкнено'} hint={o.llmEnabled ? 'розділ «LLM»' : 'навмисно, у розділі «LLM»'} />
              <Stat label="Помилок за годину" value={fmtNum(q.failedLastHour)} tone={q.failedLastHour > 0 ? 'bad' : 'ok'} />
              <Stat label="Помилок за 24 год" value={fmtNum(q.failedLast24h)} tone={q.failedLast24h > 0 ? 'warn' : undefined} />
              <Stat label="Невдалих усього" value={fmtNum(q.failed)} hint={q.lastFailureAt ? `остання ${ago(q.lastFailureAt)}` : 'не було'} />
              <Stat label="Активні" value={`${fmtNum(o.extractors)} екстр. · ${fmtNum(o.definitions)} сут.`} hint="увімкнені екстрактори та сутності" />
            </div>
            <p className="text-xs text-slate-500">{o.status.detail}</p>
            {extractionOffNote(o) && <div className="rounded bg-amber-50 p-2 text-xs text-amber-900 dark:bg-amber-900/30 dark:text-amber-100" role="note">{extractionOffNote(o)}</div>}
            {q.lastError && (
              <div className={`rounded p-2 text-xs ${q.failedLastHour > 0 ? 'bg-red-50 text-red-800 dark:bg-red-900/30 dark:text-red-200' : 'bg-slate-50 text-slate-600 dark:bg-slate-800 dark:text-slate-300'}`}>
                <div className="font-medium">
                  Остання невдала доставка: {q.lastFailureAt ? `${fmtTime(q.lastFailureAt)} (${ago(q.lastFailureAt)})` : '—'}
                  {q.failedLastHour === 0 && ' — історична, нових помилок за останню годину немає'}
                </div>
                <div className="break-words font-mono">{q.lastError}</div>
              </div>
            )}
            <p className="text-[11px] text-slate-500">Невдала доставка не повторюється автоматично: лічильник «усього» — історія. Поточну проблему видно за помилками останньої години. Повторно відправити повідомлення можна нижче за його ID.</p>
          </>
        )}
        <div className="flex justify-end">
          <Freshness stale={false} loadedAt={overview.loadedAt} />
        </div>
      </Section>
      <EeSettingsSection />
      <section className={card}>
        <h2 className="font-semibold">Відправити raw_message</h2>
        <EnqueueForm onDone={overview.reload} />
      </section>
      <EeDeliveries />
      <EeRuns />
    </div>
  )
}

const STATUS_TEXT: Record<string, string> = { ok: 'працює', warn: 'є помилки', down: 'недоступний', unknown: 'невідомо' }
function statusOk(status: string): boolean | null {
  return status === 'ok' ? true : status === 'down' ? false : null
}

function EnqueueForm({ onDone }: { onDone: () => void }) {
  const [rawId, setRawId] = useState('')
  const [message, setMessage] = useState<{ ok: boolean; text: string }>()
  const enqueue = async () => {
    try {
      await entityAdmin.enqueue(rawId)
      setMessage({ ok: true, text: `Повідомлення #${rawId} поставлено в чергу.` })
      setRawId('')
      onDone()
    } catch (reason) {
      setMessage({ ok: false, text: (reason as Error).message })
    }
  }
  return (
    <div className="mt-2 flex flex-wrap items-center gap-2">
      <input className={input} inputMode="numeric" aria-label="ID повідомлення" placeholder="raw_message_id" value={rawId} onChange={(event) => setRawId(event.target.value)} />
      <Button disabled={!/^\d+$/.test(rawId)} onClick={() => void enqueue()}>
        Поставити в чергу
      </Button>
      {message && (
        <span className={`text-xs ${message.ok ? 'text-emerald-600' : 'text-red-600'}`} role="status">
          {message.text}
        </span>
      )}
    </div>
  )
}

/** The five EntityExtractor:* settings with the value that applies now, where it comes from, its unit and range. */
function EeSettingsSection() {
  const [settings, setSettings] = useState<EeSetting[] | null>(null)
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ ok: boolean; text: string }>()
  const [busy, setBusy] = useState(false)
  const load = async () => {
    try {
      setSettings(await entityAdmin.settings())
    } catch (reason) {
      setMessage({ ok: false, text: `Не вдалося завантажити налаштування: ${(reason as Error).message}` })
    }
  }
  useEffect(() => {
    void load()
  }, [])
  const changes = settings ? changedSettings(settings, draft) : {}
  const dirty = Object.keys(changes).length > 0
  const save = async () => {
    setBusy(true)
    try {
      await entityAdmin.saveSettings(changes)
      setDraft({})
      setMessage({ ok: true, text: 'Збережено. Processor застосує нові значення без перезапуску.' })
      await load()
    } catch (reason) {
      setMessage({ ok: false, text: (reason as Error).message })
    } finally {
      setBusy(false)
    }
  }
  return (
    <Section title="Налаштування доставки в Entity Extractor" badge={dirty ? <Badge ok={null} text="є незбережені зміни" /> : undefined}>
      <p className="text-xs text-slate-500">Порожнє поле означає «не задано тут»: тоді діє значення з конфігурації сервісу або типове — його показано під полем.</p>
      {!settings && !message && <div className="text-xs text-slate-500">Завантаження…</div>}
      {settings && (
        <div className="grid gap-3 sm:grid-cols-2">
          {settings.map((s) => (
            <label key={s.key} className="block text-sm">
              <span className="mb-0.5 flex flex-wrap items-baseline justify-between gap-1">
                <span className="font-medium">{s.label}</span>
                <span className="font-mono text-[10px] text-slate-400">{s.key}</span>
              </span>
              <input className={`${input} w-full font-mono`} value={draft[s.key] ?? s.value ?? ''} placeholder={s.effective} onChange={(event) => setDraft((current) => ({ ...current, [s.key]: event.target.value }))} autoComplete="off" />
              <span className="block text-[11px] text-slate-500">
                Діє зараз: <b className="font-mono">{s.effective}</b> · {SETTING_SOURCE[s.source]}
                {s.source !== 'default' && (
                  <>
                    {' '}
                    · типово <span className="font-mono">{s.default}</span>
                  </>
                )}
              </span>
              <span className="block text-[11px] text-slate-500">
                Формат: {s.format}. {s.hint}
              </span>
            </label>
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <Button disabled={!dirty || busy} onClick={() => void save()}>
          {busy ? 'Зберігаю…' : 'Зберегти налаштування'}
        </Button>
        <button className="rounded border border-slate-300 px-3 py-1.5 text-sm disabled:opacity-50 dark:border-slate-600" disabled={!dirty || busy} onClick={() => setDraft({})}>
          Скасувати
        </button>
        {message && (
          <span className={`text-xs ${message.ok ? 'text-emerald-600' : 'text-red-600'}`} role="status">
            {message.text}
          </span>
        )}
      </div>
    </Section>
  )
}

const DELIVERY_FILTERS: { value: EeDeliveryStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'усі' },
  { value: 'failed', label: 'помилки' },
  { value: 'pending', label: 'у черзі' },
  { value: 'in_progress', label: 'обробляються' },
  { value: 'succeeded', label: 'доставлені' },
]

function EeDeliveries() {
  const [status, setStatus] = useState<EeDeliveryStatus | 'all'>('all')
  const [open, setOpen] = useState<string | null>(null)
  const page = usePaged<EeDelivery, string>(async (cursor) => {
    const r = await entityAdmin.deliveries(status, cursor)
    return { items: r.items, next: r.nextCursor }
  }, [status])
  return (
    <Section
      title="Черга доставок"
      badge={
        <span className="flex flex-wrap gap-1">
          {DELIVERY_FILTERS.map((f) => (
            <button key={f.value} className={`rounded px-2 py-0.5 text-xs ${status === f.value ? 'bg-slate-800 text-white dark:bg-slate-100 dark:text-slate-900' : 'bg-slate-100 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700'}`} aria-pressed={status === f.value} onClick={() => setStatus(f.value)}>
              {f.label}
            </button>
          ))}
        </span>
      }
    >
      <p className="text-xs text-slate-500">Від найновіших. «Без сутностей» — повідомлення оброблено, але записувати не було чого; це не помилка. Час — місцевий.</p>
      <Loading error={page.error} empty={page.items === null} />
      {page.items && page.items.length === 0 && <div className="text-xs text-slate-500">Доставок із таким статусом немає.</div>}
      {page.items && page.items.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-left text-slate-500">
              <tr>
                <th className="py-1 pr-2">Поставлено</th>
                <th className="pr-2">Джерело</th>
                <th className="pr-2">Повідомлення</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2">Результат</th>
                <th className="pr-2">Спроб</th>
                <th className="pr-2">Помилка</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {page.items.map((d) => {
                const state = deliveryState(d)
                const problem = d.last_error ?? d.run_error
                return (
                  <Fragment key={d.delivery_id}>
                    <tr className="border-t border-slate-100 align-top dark:border-slate-800" data-testid="ee-delivery">
                      <td className="whitespace-nowrap py-1.5 pr-2" title={d.enqueued_at}>
                        {fmtTime(d.enqueued_at)}
                      </td>
                      <td className="pr-2 font-mono">{d.source_code}</td>
                      <td className="pr-2">
                        <a className="underline" href={`#/messages?rawMessageId=${d.raw_message_id}`}>
                          #{d.raw_message_id}
                        </a>
                      </td>
                      <td className="pr-2">
                        <Badge ok={state.ok} text={state.text} />
                      </td>
                      <td className="pr-2">{resultLabel(d.result)}</td>
                      <td className="pr-2 font-mono">{d.attempts}</td>
                      <td className="max-w-xs truncate pr-2 text-red-600" title={problem}>
                        {problem ?? ''}
                      </td>
                      <td>
                        <button className="rounded border border-slate-300 px-2 py-0.5 dark:border-slate-600" aria-expanded={open === d.delivery_id} onClick={() => setOpen(open === d.delivery_id ? null : d.delivery_id)}>
                          Деталі
                        </button>
                      </td>
                    </tr>
                    {open === d.delivery_id && (
                      <tr>
                        <td colSpan={8}>
                          <JsonBlock value={d} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <PagerButtons busy={page.busy} hasMore={page.hasMore} count={page.items?.length} onMore={page.more} onRefresh={page.refresh} />
    </Section>
  )
}

function EeRuns() {
  const [open, setOpen] = useState<number | null>(null)
  const page = usePaged<EeRun, number>(async (cursor) => {
    const r = await entityAdmin.runs(cursor)
    return { items: r.items, next: r.nextBeforeId }
  }, [])
  return (
    <Section title="Запуски обробки">
      <p className="text-xs text-slate-500">Кожен запуск — одна доставка всередині Entity Extractor: Python-екстрактори, за потреби LLM, запис сутностей.</p>
      <Loading error={page.error} empty={page.items === null} />
      {page.items && page.items.length === 0 && <div className="text-xs text-slate-500">Запусків ще не було.</div>}
      {page.items && page.items.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-left text-slate-500">
              <tr>
                <th className="py-1 pr-2">Почато</th>
                <th className="pr-2">Джерело</th>
                <th className="pr-2">Повідомлення</th>
                <th className="pr-2">Стан</th>
                <th className="pr-2">Результат</th>
                <th className="pr-2">Помилка</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {page.items.map((r) => {
                const state = runState(r)
                return (
                  <Fragment key={r.processing_run_id}>
                    <tr className="border-t border-slate-100 align-top dark:border-slate-800">
                      <td className="whitespace-nowrap py-1.5 pr-2" title={r.started_at}>
                        {fmtTime(r.started_at)}
                      </td>
                      <td className="pr-2 font-mono">{r.source_code}</td>
                      <td className="pr-2">
                        <a className="underline" href={`#/messages?rawMessageId=${r.raw_message_id}`}>
                          #{r.raw_message_id}
                        </a>
                      </td>
                      <td className="pr-2">
                        <Badge ok={state.ok} text={state.text} />
                      </td>
                      <td className="pr-2">{resultLabel(r.result)}</td>
                      <td className="max-w-xs truncate pr-2 text-red-600" title={r.error}>
                        {r.error ?? ''}
                      </td>
                      <td>
                        <button className="rounded border border-slate-300 px-2 py-0.5 dark:border-slate-600" aria-expanded={open === r.processing_run_id} onClick={() => setOpen(open === r.processing_run_id ? null : r.processing_run_id)}>
                          Деталі
                        </button>
                      </td>
                    </tr>
                    {open === r.processing_run_id && (
                      <tr>
                        <td colSpan={7}>
                          <JsonBlock value={r} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <PagerButtons busy={page.busy} hasMore={page.hasMore} count={page.items?.length} onMore={page.more} onRefresh={page.refresh} />
    </Section>
  )
}

export function EeExtractorsPanel() {
  const [items, setItems] = useState<EeExtractor[]>([]); const [selected, setSelected] = useState<EeExtractor>(); const [name, setName] = useState(''); const [code, setCode] = useState(defaultCode); const [enabled, setEnabled] = useState(true); const [order, setOrder] = useState(0); const [timeout, setTimeoutValue] = useState(5000); const [testText, setTestText] = useState(''); const [result, setResult] = useState<unknown>(); const [diagnostics, setDiagnostics] = useState<PythonDiagnostic[]>([]); const [busy, setBusy] = useState(false)
  const load = async () => setItems(await entityAdmin.extractors())
  useEffect(() => { void load() }, [])
  const choose = (item?: EeExtractor) => { setSelected(item); setName(item?.name ?? ''); setCode(item?.code ?? defaultCode); setEnabled(item?.enabled ?? true); setOrder(item?.execution_order ?? 0); setTimeoutValue(item?.timeout_ms ?? 5000); setDiagnostics([]); setResult(undefined) }
  const validate = async () => { const response = await entityAdmin.validate(code); const issues = Array.isArray(response.diagnostics) ? response.diagnostics as PythonDiagnostic[] : []; setDiagnostics(issues); setResult(response); return response.valid === true }
  const save = async () => { setBusy(true); try { if (!await validate()) return; await entityAdmin.saveExtractor({ extractorId: selected?.extractor_id, name, code, enabled, executionOrder: order, timeoutMs: timeout }); await load(); setResult({ ok: true, message: 'Екстрактор збережено й буде використаний без перебудови контейнера.' }) } catch (reason) { setResult({ error: (reason as Error).message }) } finally { setBusy(false) } }
  const test = async () => { setBusy(true); try { setResult(await entityAdmin.test(code, testText, timeout)) } catch (reason) { setResult({ error: (reason as Error).message }) } finally { setBusy(false) } }
  return <div className="grid gap-4 lg:grid-cols-[16rem_1fr]"><aside className="rounded-xl bg-white p-3 shadow dark:bg-slate-900"><div className="flex items-center"><h2 className="font-semibold">Python-екстрактори</h2><button className="ml-auto text-xl" title="Новий" onClick={() => choose()}>＋</button></div>{items.map((item) => <button key={item.extractor_id} onClick={() => choose(item)} className={`mt-2 block w-full rounded p-2 text-left text-sm ${selected?.extractor_id === item.extractor_id ? 'bg-slate-200 dark:bg-slate-700' : 'hover:bg-slate-100 dark:hover:bg-slate-800'}`}><b>{item.name}</b><br /><span className="text-xs text-slate-500">{item.enabled ? 'увімкнено' : 'вимкнено'} · {item.timeout_ms} мс</span></button>)}</aside>
    <section className="space-y-3 rounded-xl bg-white p-4 shadow dark:bg-slate-900"><div className="grid gap-2 sm:grid-cols-4"><label className="sm:col-span-2">Назва<input className="block w-full rounded border px-2 py-1 dark:bg-slate-800" value={name} onChange={(event) => setName(event.target.value)} /></label><label>Порядок<input className="block w-full rounded border px-2 py-1 dark:bg-slate-800" type="number" value={order} onChange={(event) => setOrder(Number(event.target.value))} /></label><label>Timeout, мс<input className="block w-full rounded border px-2 py-1 dark:bg-slate-800" type="number" value={timeout} onChange={(event) => setTimeoutValue(Number(event.target.value))} /></label></div><label className="flex gap-2"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Увімкнений</label><PythonEditor value={code} onChange={setCode} diagnostics={diagnostics} /><div className="flex gap-2"><Button disabled={busy || !name.trim()} onClick={() => void save()}>Зберегти</Button><Button disabled={busy} onClick={() => void validate()}>Перевірити синтаксис</Button></div><label>Тестовий текст<textarea className="mt-1 block min-h-24 w-full rounded border p-2 dark:bg-slate-800" value={testText} onChange={(event) => setTestText(event.target.value)} /></label><Button disabled={busy} onClick={() => void test()}>Тестовий запуск без запису</Button>{result !== undefined && <JsonBlock value={result} />}</section></div>
}

const emptyMap: EeMapConfig = { enabled: false, renderer: 'point' }
const newFields = (): EeField[] => [{ name: 'occurredAt', type: 'datetime', required: true }]

/**
 * Entity definitions. Two explicit modes: creating a new entity (name + fields + map → a new table), and editing only the map
 * presentation of an existing one — its schema is shown read-only there, because the table already exists.
 */
export function EeDefinitionsPanel() {
  const [definitions, setDefinitions] = useState<EeDefinition[]>([])
  const [editing, setEditing] = useState<EeDefinition | null>(null)
  const [entityName, setEntityName] = useState('')
  const [fields, setFields] = useState<EeField[]>(newFields)
  const [map, setMap] = useState<EeMapConfig>(emptyMap)
  const [result, setResult] = useState<unknown>()
  const load = async () => setDefinitions(await entityAdmin.definitions())
  useEffect(() => {
    void load()
  }, [])
  const updateField = (index: number, patch: Partial<EeField>) => setFields((current) => current.map((field, at) => (at === index ? { ...field, ...patch } : field)))
  const startNew = () => {
    setEditing(null)
    setEntityName('')
    setFields(newFields())
    setMap(emptyMap)
    setResult(undefined)
  }
  const editMap = (definition: EeDefinition) => {
    setEditing(definition)
    setMap(definition.map_settings ?? emptyMap)
    setResult(undefined)
  }
  const create = async () => {
    try {
      setResult(await entityAdmin.createDefinition({ entityName, fields, map, enabled: true }))
      setEntityName('')
      await load()
    } catch (reason) {
      setResult({ error: (reason as Error).message })
    }
  }
  const saveMap = async () => {
    if (!editing) return
    try {
      await entityAdmin.updateDefinitionMap(editing.entity_definition_id, map)
      setResult({ ok: true, message: `Параметри мапи «${editing.entity_name}» збережено.` })
      setEditing(null)
      await load()
    } catch (reason) {
      setResult({ error: (reason as Error).message })
    }
  }
  const text = 'block w-full rounded border px-2 py-1 dark:bg-slate-800'
  return (
    <div className="space-y-4">
      <section className="rounded-xl bg-white p-4 shadow dark:bg-slate-900" aria-label={editing ? `Мапа: ${editing.entity_name}` : 'Нова конкретна сутність'}>
        {editing ? (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="font-semibold">Мапа: {editing.entity_name}</h2>
              <code className="text-xs text-slate-500">{editing.table_name}</code>
              <button className="ml-auto rounded border border-slate-300 px-3 py-1 text-sm dark:border-slate-600" onClick={startNew}>
                Скасувати
              </button>
            </div>
            <p className="mt-1 text-xs text-slate-500">Змінюється лише відображення на мапі. Схема таблиці вже створена й тут не редагується.</p>
            <h3 className="mt-3 font-medium">Поля (лише перегляд)</h3>
            <ul className="mt-1 flex flex-wrap gap-2 text-xs" aria-label="Поля сутності">
              {editing.fields.map((field) => (
                <li key={field.name} className="rounded bg-slate-100 px-2 py-0.5 font-mono dark:bg-slate-800">
                  {field.name}: {field.type}
                  {field.required ? ' *' : ''}
                </li>
              ))}
            </ul>
          </>
        ) : (
          <>
            <h2 className="font-semibold">Нова конкретна сутність</h2>
            <label className="mt-2 block">
              Назва, однина
              <input className="block w-full max-w-md rounded border px-2 py-1 font-mono dark:bg-slate-800" placeholder="explosion" value={entityName} onChange={(event) => setEntityName(event.target.value)} />
            </label>
            <h3 className="mt-4 font-medium">Поля</h3>
            {fields.map((field, index) => (
              <div className="mt-2 flex flex-wrap gap-2" key={index}>
                <input aria-label="Назва поля" className="min-w-0 flex-1 rounded border px-2 py-1 font-mono dark:bg-slate-800" value={field.name} onChange={(event) => updateField(index, { name: event.target.value })} />
                <select aria-label="Тип поля" className="rounded border px-2 py-1 dark:bg-slate-800" value={field.type} onChange={(event) => updateField(index, { type: event.target.value })}>
                  {fieldTypes.map((type) => (
                    <option key={type}>{type}</option>
                  ))}
                </select>
                <label className="flex items-center gap-1">
                  <input type="checkbox" checked={field.required} onChange={(event) => updateField(index, { required: event.target.checked })} /> обов’язкове
                </label>
                <button aria-label="Видалити поле" onClick={() => setFields((current) => current.filter((_, at) => at !== index))}>
                  ×
                </button>
              </div>
            ))}
            <button className="mt-2 underline" onClick={() => setFields((current) => [...current, { name: '', type: 'text', required: false }])}>
              ＋ поле
            </button>
          </>
        )}
        <h3 className="mt-4 font-medium">Мапа</h3>
        <div className="grid gap-2 sm:grid-cols-3">
          <label>
            <input type="checkbox" checked={map.enabled} onChange={(event) => setMap({ ...map, enabled: event.target.checked })} /> показувати
          </label>
          <label>
            Вигляд
            <select className={text} value={map.renderer} onChange={(event) => setMap({ ...map, renderer: event.target.value as EeMapConfig['renderer'] })}>
              {['point', 'icon', 'line', 'polygon'].map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </label>
          <label>
            Поле geometry
            <input className={text} value={map.geometryField ?? ''} onChange={(event) => setMap({ ...map, geometryField: event.target.value || undefined })} />
          </label>
          <label>
            Latitude
            <input className={text} value={map.latitudeField ?? ''} onChange={(event) => setMap({ ...map, latitudeField: event.target.value || undefined })} />
          </label>
          <label>
            Longitude
            <input className={text} value={map.longitudeField ?? ''} onChange={(event) => setMap({ ...map, longitudeField: event.target.value || undefined })} />
          </label>
          <label>
            Підпис
            <input className={text} value={map.labelField ?? ''} onChange={(event) => setMap({ ...map, labelField: event.target.value || undefined })} />
          </label>
          <label>
            Час
            <input className={text} value={map.timeField ?? ''} onChange={(event) => setMap({ ...map, timeField: event.target.value || undefined })} />
          </label>
          <label>
            Статус
            <input className={text} value={map.statusField ?? ''} onChange={(event) => setMap({ ...map, statusField: event.target.value || undefined })} />
          </label>
          <label>
            Ключ стану
            <input className={text} title="Поле, що називає предмет стану (напр. район тривоги): на карті лише останній запис кожного ключа" value={map.keyField ?? ''} onChange={(event) => setMap({ ...map, keyField: event.target.value || undefined })} />
          </label>
          <label>
            Колір
            <input className={text} value={map.color ?? ''} onChange={(event) => setMap({ ...map, color: event.target.value || undefined })} />
          </label>
          <label>
            Час життя, хв
            <input type="number" min="1" className={text} value={map.lifetimeMinutes ?? ''} onChange={(event) => setMap({ ...map, lifetimeMinutes: event.target.value ? Number(event.target.value) : undefined })} />
          </label>
          <label>
            Товщина
            <input type="number" min="0" step="0.5" className={text} value={map.width ?? ''} onChange={(event) => setMap({ ...map, width: event.target.value ? Number(event.target.value) : undefined })} />
          </label>
          <label>
            Прозорість
            <input type="number" min="0" max="1" step="0.05" className={text} value={map.opacity ?? ''} onChange={(event) => setMap({ ...map, opacity: event.target.value ? Number(event.target.value) : undefined })} />
          </label>
          <label>
            Лінія
            <select className={text} value={map.dash ?? ''} onChange={(event) => setMap({ ...map, dash: event.target.value || undefined })}>
              <option value="">суцільна</option>
              <option value="dashed">штрихова</option>
              <option value="dotted">пунктирна</option>
            </select>
          </label>
        </div>
        <fieldset className="mt-4 rounded-lg border border-slate-200 p-3 dark:border-slate-700">
          <legend className="px-1 text-sm font-medium">Піктограма сутності</legend>
          <div className="flex items-center gap-3">
            <EntityIcon entity={editing?.entity_name ?? entityName} svg={map.svg} className="h-10 w-10 shrink-0" />
            <p className="text-xs text-slate-500">Типова піктограма визначається назвою сутності. Для показу на мапі виберіть вигляд «icon»; власний SVG має пріоритет.</p>
          </div>
          <div className="mt-3 flex flex-wrap gap-2">
            <button type="button" className="rounded-lg border px-3 py-2 text-xs focus-visible:outline-2 focus-visible:outline-sky-500" onClick={() => setMap({ ...map, renderer: 'icon', svg: undefined })}>Типова піктограма</button>
            {entityIconChoices.map(name => <button key={name} type="button" aria-label={`Піктограма: ${entityLabel(name)}`} aria-pressed={map.renderer === 'icon' && map.svg === entityIconSvg(name)} className="flex items-center gap-1.5 rounded-lg border px-2 py-1.5 text-xs hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-sky-500 dark:hover:bg-slate-800" onClick={() => setMap({ ...map, renderer: 'icon', svg: entityIconSvg(name) })}><EntityIcon entity={name} />{entityLabel(name)}</button>)}
          </div>
        </fieldset>
        <label className="mt-2 block">
          SVG-піктограма
          <textarea aria-label="SVG-піктограма" className="block min-h-28 w-full rounded border p-2 font-mono text-xs dark:bg-slate-800" value={map.svg ?? ''} onChange={(event) => setMap({ ...map, svg: event.target.value || undefined })} />
        </label>
        {editing ? (
          <div className="mt-3 flex gap-2">
            <Button onClick={() => void saveMap()}>Зберегти мапу</Button>
            <button className="rounded border border-slate-300 px-3 py-1.5 text-sm dark:border-slate-600" onClick={startNew}>
              Скасувати
            </button>
          </div>
        ) : (
          <Button className="mt-3" disabled={!entityName || fields.length === 0 || fields.some((field) => !field.name)} onClick={() => void create()}>
            Створити таблицю
          </Button>
        )}
        {result !== undefined && <JsonBlock value={result} />}
      </section>
      <section className="rounded-xl bg-white p-4 shadow dark:bg-slate-900">
        <div className="flex items-center">
          <h2 className="font-semibold">Зареєстровані сутності</h2>
          {editing && (
            <button className="ml-auto underline" onClick={startNew}>
              ＋ Нова сутність
            </button>
          )}
        </div>
        <div className="mt-2 space-y-2">
          {definitions.map((definition) => (
            <div key={definition.entity_definition_id} className={`flex flex-wrap items-center gap-2 rounded border p-2 ${editing?.entity_definition_id === definition.entity_definition_id ? 'border-sky-400 bg-sky-50 dark:bg-sky-950' : 'border-slate-200 dark:border-slate-700'}`}>
              <EntityIcon entity={definition.entity_name} svg={definition.map_settings?.svg} />
              <span>
                <b>{definition.entity_name}</b> · <code>{definition.table_name}</code>
                {isRetiredEntity(definition.entity_name, definition.table_name) && <span className="ml-2 text-xs text-slate-500">Треки вимкнено · історію збережено</span>}
              </span>
              <button className="ml-auto underline" onClick={() => editMap(definition)}>
                Налаштувати мапу
              </button>
            </div>
          ))}
        </div>
      </section>
    </div>
  )
}
