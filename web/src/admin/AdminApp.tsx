import { useCallback, useEffect, useState } from 'react'
import { admin, AdminError, getAdminToken, setAdminToken, type AdminSourceDto, type AdminStatusDto, type SettingDto } from '../api/admin'
import { Badge, Field, findSetting, Section, Select, Toggle, type Draft } from '../components/settings/fields'
import LlmUsagePanel from './LlmUsagePanel'
import SourcesEditor from '../components/settings/SourcesEditor'
import { CollectorsPanel, DbPanel, LogsPanel, OverviewPanel } from './OpsPanels'
import { WorkersPanel } from './WorkersPanel'
import { PipelinePanel } from './PipelinePanel'
import { CatalogPanel } from './CatalogPanel'

/** Where the public map lives (another service, another port); overridable at build time. */
// The admin build is used both locally (:5268 → map :5267) and through Docker
// (:8091 → map :8090). An explicit VITE_MAP_URL remains available for reverse proxies.
const defaultMapPort = window.location.port === '8091' ? '8090' : '5267'
const MAP_URL: string = (import.meta.env.VITE_MAP_URL as string | undefined) ?? `${window.location.protocol}//${window.location.hostname}:${defaultMapPort}/`

type SectionId = 'overview' | 'workers' | 'collectors' | 'pipeline' | 'db' | 'logs' | 'catalog' | 'sources' | 'alerts' | 'telegram' | 'llm' | 'system'

const NAV: { id: SectionId; label: string; group: string }[] = [
  { id: 'overview', label: 'Стан', group: 'Моніторинг' },
  { id: 'workers', label: 'Воркери', group: 'Моніторинг' },
  { id: 'collectors', label: 'Колектори', group: 'Моніторинг' },
  { id: 'pipeline', label: 'Конвеєр', group: 'Моніторинг' },
  { id: 'db', label: 'База даних', group: 'Моніторинг' },
  { id: 'logs', label: 'Логи', group: 'Моніторинг' },
  { id: 'catalog', label: 'Каталог подій', group: 'Дані' },
  { id: 'sources', label: 'Джерела', group: 'Налаштування' },
  { id: 'alerts', label: 'alerts.in.ua', group: 'Налаштування' },
  { id: 'telegram', label: 'Telegram', group: 'Налаштування' },
  { id: 'llm', label: 'LLM', group: 'Налаштування' },
  { id: 'system', label: 'Система', group: 'Налаштування' },
]

function sectionFromHash(): SectionId {
  const id = window.location.hash.replace(/^#\/?/, '').replace(/\?.*$/, '') // `#/messages?raw=123` opens the explorer on a card
  return NAV.some((n) => n.id === id) ? (id as SectionId) : 'overview'
}

/**
 * The admin panel (its own service, port 5268): monitoring of every component (status, workers and containers,
 * collectors, pipeline, database, logs) and all the settings. Values go to the app_settings table through /api/admin/*; the Worker
 * picks them up within seconds and restarts its collectors — no process restart, no .env editing.
 */
export default function AdminApp() {
  const [section, setSectionState] = useState<SectionId>(sectionFromHash)
  const setSection = (id: SectionId) => {
    window.location.assign(`#/${id}`)
    setSectionState(id)
  }
  useEffect(() => {
    const onHash = () => setSectionState(sectionFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  const [settings, setSettings] = useState<SettingDto[]>([])
  const [status, setStatus] = useState<AdminStatusDto | null>(null)
  const [sources, setSources] = useState<AdminSourceDto[]>([])
  const [draft, setDraft] = useState<Draft>({})
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [authNeeded, setAuthNeeded] = useState(false)
  const [tokenInput, setTokenInput] = useState(getAdminToken())

  const load = useCallback(async () => {
    try {
      const [s, st, src] = await Promise.all([admin.settings(), admin.status(), admin.sources()])
      setSettings(s)
      setStatus(st)
      setSources(src)
      setAuthNeeded(false)
      // A poll can recover after a transient admin/API failure. Keep an explicit
      // success notification, but do not leave a stale error below a fresh table.
      setMessage((current) => (current?.ok === false ? null : current))
    } catch (e) {
      if (e instanceof AdminError && (e.status === 401 || e.status === 403)) setAuthNeeded(true)
      else setMessage({ ok: false, text: (e as Error).message })
    }
  }, [])

  useEffect(() => {
    void load()
    const id = window.setInterval(() => void load(), 10_000)
    return () => window.clearInterval(id)
  }, [load])

  const change = (key: string, value: string | null) => setDraft((d) => ({ ...d, [key]: value }))
  const s = (key: string) => findSetting(settings, key)
  const dirty = Object.keys(draft).length > 0

  const save = async () => {
    if (!dirty) return
    setBusy(true)
    try {
      await admin.saveSettings(draft)
      if (draft['Admin:Token']) setAdminToken(draft['Admin:Token'])
      setDraft({})
      setMessage({ ok: true, text: 'Збережено. Worker застосує зміни протягом кількох секунд.' })
      await load()
    } catch (e) {
      setMessage({ ok: false, text: (e as Error).message })
    } finally {
      setBusy(false)
    }
  }

  const props: TabProps = { status, draft, change, s, notify: setMessage, reload: load, sources }

  return (
    <div className="absolute inset-0 flex flex-col bg-slate-100 dark:bg-slate-950 dark:text-slate-100">
      <header className="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-2 text-sm dark:border-slate-700 dark:bg-slate-900">
        <a className="rounded px-2 py-1 hover:bg-slate-200 dark:hover:bg-slate-700" href={MAP_URL}>
          ← Карта
        </a>
        <span className="font-semibold">Puluj · Адміністрування</span>
        <span className="ml-auto flex items-center gap-2 text-xs">
          {status && <Badge ok={status.workerAlive} text={status.workerAlive ? 'Worker працює' : 'Worker не відповідає'} />}
          {status && <Badge ok={status.alertsConfigured} text={status.alertsConfigured ? 'alerts.in.ua ✓' : 'alerts.in.ua —'} />}
          {status && <Badge ok={telegramBadge(status).ok} text={telegramBadge(status).text} />}
        </span>
      </header>

      {authNeeded ? (
        <div className="m-auto w-full max-w-md space-y-3 rounded-xl bg-white p-5 text-sm shadow dark:bg-slate-900">
          <p>
            Сервер вимагає адмін-токен (<code>Admin:Token</code>), або сторінку відкрито не з localhost.
          </p>
          <input className="w-full rounded border border-slate-300 px-2 py-1 font-mono dark:border-slate-600 dark:bg-slate-800" type="password" placeholder="Admin token" value={tokenInput} onChange={(e) => setTokenInput(e.target.value)} />
          <button
            className="rounded bg-slate-800 px-3 py-1.5 text-white dark:bg-slate-100 dark:text-slate-900"
            onClick={() => {
              setAdminToken(tokenInput)
              void load()
            }}
          >
            Увійти
          </button>
        </div>
      ) : (
        <div className="flex min-h-0 flex-1">
          <nav className="w-44 shrink-0 border-r border-slate-200 bg-white p-2 text-sm dark:border-slate-700 dark:bg-slate-900">
            {['Моніторинг', 'Дані', 'Налаштування'].map((group) => (
              <div key={group} className="mb-2">
                <div className="px-3 pb-1 pt-2 text-[10px] uppercase tracking-wide text-slate-400">{group}</div>
                {NAV.filter((n) => n.group === group).map((n) => (
                  <button key={n.id} className={`block w-full rounded px-3 py-1.5 text-left ${section === n.id ? 'bg-slate-200 font-medium dark:bg-slate-700' : 'hover:bg-slate-100 dark:hover:bg-slate-800'}`} onClick={() => setSection(n.id)}>
                    {n.label}
                  </button>
                ))}
              </div>
            ))}
          </nav>

          <main className="flex min-w-0 flex-1 flex-col">
            <div className="flex-1 space-y-4 overflow-y-auto p-4">
              <div className="mx-auto max-w-5xl space-y-4">
                {section === 'overview' && <OverviewPanel />}
                {section === 'workers' && <WorkersPanel />}
                {section === 'collectors' && <CollectorsPanel />}
                {section === 'pipeline' && <PipelinePanel />}
                {section === 'db' && <DbPanel />}
                {section === 'logs' && <LogsPanel />}
                {section === 'catalog' && <CatalogPanel />}
                {section === 'sources' && <SourcesEditor sources={sources} reload={load} notify={setMessage} />}
                {section === 'alerts' && <AlertsSection {...props} />}
                {section === 'telegram' && <TelegramSection {...props} />}
                {section === 'llm' && <LlmSection {...props} />}
                {section === 'system' && <SystemSection {...props} />}
              </div>
            </div>
            {NAV.find((n) => n.id === section)?.group === 'Налаштування' && (
            <div className="flex items-center justify-between gap-3 border-t border-slate-200 bg-white px-4 py-3 text-sm dark:border-slate-700 dark:bg-slate-900">
              <span className={`min-h-5 text-xs ${message ? (message.ok ? 'text-emerald-600' : 'text-red-600') : 'text-slate-400'}`}>{message?.text ?? (dirty ? 'Є незбережені зміни' : '')}</span>
              <div className="flex gap-2">
                <button className="rounded border border-slate-300 px-3 py-1.5 dark:border-slate-600" onClick={() => setDraft({})} disabled={!dirty}>
                  Скасувати
                </button>
                <button className="rounded bg-slate-800 px-3 py-1.5 text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" onClick={() => void save()} disabled={!dirty || busy}>
                  {busy ? 'Зберігаю…' : 'Зберегти'}
                </button>
              </div>
            </div>
            )}
          </main>
        </div>
      )}
    </div>
  )
}

/** Runtime state wins over "credentials present": a configured but crashing collector is not a green check. */
function telegramBadge(status: AdminStatusDto): { ok: boolean | null; text: string } {
  const st = status.telegramStatus ?? ''
  if (st.startsWith('listening') || st.startsWith('logged_in')) return { ok: true, text: 'Telegram ✓' }
  if (st === 'waiting_code') return { ok: null, text: 'Telegram: потрібен код' }
  if (st.startsWith('error')) return { ok: false, text: 'Telegram: помилка' }
  if (st === 'connecting') return { ok: null, text: 'Telegram: підключення…' }
  return { ok: status.telegramConfigured ? null : false, text: status.telegramConfigured ? 'Telegram: запуск…' : 'Telegram —' }
}

interface TabProps {
  status: AdminStatusDto | null
  draft: Draft
  change: (key: string, value: string | null) => void
  s: (key: string) => SettingDto | undefined
  notify: (m: { ok: boolean; text: string }) => void
  reload: () => Promise<void>
  sources: AdminSourceDto[]
}

function AlertsSection({ status, draft, change, s, notify, reload, sources }: TabProps) {
  const [testing, setTesting] = useState(false)
  const [token, setToken] = useState('')
  const source = sources.find((x) => x.code === 'alerts_in_ua')
  const saveToken = async () => {
    if (!source || !token.trim()) return
    try {
      await admin.updateSource(source.id, { token: token.trim() })
      setToken('')
      notify({ ok: true, text: 'Токен збережено на джерелі alerts.in.ua.' })
      await reload()
    } catch (e) {
      notify({ ok: false, text: (e as Error).message })
    }
  }
  const test = async () => {
    setTesting(true)
    try {
      const r = await admin.testAlerts(token.trim() || undefined)
      notify({ ok: r.ok, text: `alerts.in.ua: ${r.message}` })
    } catch (e) {
      notify({ ok: false, text: (e as Error).message })
    } finally {
      setTesting(false)
    }
  }
  return (
    <Section title="alerts.in.ua" badge={<Badge ok={status?.alertsConfigured ?? null} text={status?.alertsConfigured ? 'налаштовано' : 'не налаштовано'} />}>
      <p className="text-xs text-slate-500">Офіційні повітряні тривоги по областях. Токен видається за запитом на alerts.in.ua/api-request і зберігається на джерелі «alerts.in.ua» у базі (як і інтервал опитування — вкладка Джерела).</p>
      <Toggle label="Увімкнути" setting={s('Collectors:AlertsInUa:Enabled')} draft={draft} onChange={change} />
      <label className="block text-sm">
        <span className="text-slate-600 dark:text-slate-300">Токен API {source?.hasToken ? '(збережено — введіть новий, щоб замінити)' : '(не задано)'}</span>
        <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 font-mono dark:border-slate-600 dark:bg-slate-800" type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} placeholder={source?.hasToken ? '••••••••' : ''} />
      </label>
      <div className="flex gap-2">
        <button className="rounded bg-slate-800 px-3 py-1 text-xs text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" onClick={() => void saveToken()} disabled={!token.trim() || !source}>
          Зберегти токен
        </button>
        <button className="rounded border border-slate-300 px-3 py-1 text-xs dark:border-slate-600" onClick={() => void test()} disabled={testing}>
          {testing ? 'Перевіряю…' : token.trim() ? 'Перевірити введений токен' : 'Перевірити збережений токен'}
        </button>
      </div>
      {!source && <p className="text-xs text-amber-700">Джерела «alerts.in.ua» немає в базі: додайте його на вкладці Джерела (тип REST API, код alerts_in_ua).</p>}
    </Section>
  )
}

function TelegramSection({ status, draft, change, s, notify, reload }: TabProps) {
  const [code, setCode] = useState('')
  const tgStatus = status?.telegramStatus ?? ''
  const waiting = tgStatus === 'waiting_code'
  const ok = tgStatus.startsWith('listening') || tgStatus.startsWith('logged_in')
  const enabled = (draft['Collectors:Telegram:Enabled'] ?? s('Collectors:Telegram:Enabled')?.value ?? 'false') === 'true'
  const label = waiting ? 'очікує код входу' : tgStatus.startsWith('listening') ? tgStatus : tgStatus.startsWith('error') ? 'помилка' : !enabled ? 'вимкнено' : tgStatus || 'не запущено'

  const sendCode = async () => {
    try {
      await admin.telegramCode(code.trim())
      setCode('')
      notify({ ok: true, text: 'Код передано Worker-у.' })
      await reload()
    } catch (e) {
      notify({ ok: false, text: (e as Error).message })
    }
  }

  return (
    <>
      <Section title="Telegram (MTProto)" badge={<Badge ok={waiting ? null : ok ? true : status?.telegramConfigured ? false : null} text={label} />}>
        <p className="text-xs text-slate-500">
          Це сесія звичайного акаунта, не бот: боти не бачать публічні канали. Заведіть окремий акаунт, отримайте <code>api_id</code>/<code>api_hash</code> на{' '}
          <a className="underline" href="https://my.telegram.org" target="_blank" rel="noreferrer">
            my.telegram.org
          </a>
          . Після збереження Worker попросить код із Telegram — поле для нього з'явиться тут. Канали додаються на вкладці «Джерела».
        </p>
        {tgStatus.startsWith('error') && <div className="rounded bg-red-50 p-2 text-xs text-red-700 dark:bg-red-900/30 dark:text-red-200">{tgStatus}</div>}
        {!tgStatus && status?.telegramConfigured && <div className="rounded bg-amber-50 p-2 text-xs text-amber-800 dark:bg-amber-900/30 dark:text-amber-200">Дані збережено, Worker ще не звітував про стан сесії. Якщо це триває довше хвилини — дивіться колонку «Стан» у Джерелах або лог Worker-а.</div>}
        <Toggle label="Увімкнути" setting={s('Collectors:Telegram:Enabled')} draft={draft} onChange={change} />
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="api_id" setting={s('Collectors:Telegram:ApiId')} draft={draft} onChange={change} type="number" />
          <Field label="api_hash" setting={s('Collectors:Telegram:ApiHash')} draft={draft} onChange={change} type="password" />
          <Field label="Телефон" setting={s('Collectors:Telegram:Phone')} draft={draft} onChange={change} placeholder="+380…" />
          <Field label="Пароль 2FA" setting={s('Collectors:Telegram:Password')} draft={draft} onChange={change} type="password" hint="лише якщо увімкнено двофакторний захист" />
          <Field label="Дочитати постів при старті" setting={s('Collectors:Telegram:BackfillLimit')} draft={draft} onChange={change} type="number" />
          <Field label="History workers" setting={s('Collectors:Telegram:HistoryWorkers')} draft={draft} onChange={change} type="number" hint="максимум 2; RPC все одно глобально послідовні" />
          <Field label="Пауза між history RPC" setting={s('Collectors:Telegram:HistoryRequestInterval')} draft={draft} onChange={change} hint="стартово 00:00:00.500; flood автоматично сповільнює темп" />
          <Field label="Мінімальна пауза" setting={s('Collectors:Telegram:HistoryMinimumInterval')} draft={draft} onChange={change} hint="нижня межа adaptive throttling" />
          <Field label="Максимальна пауза" setting={s('Collectors:Telegram:HistoryMaximumInterval')} draft={draft} onChange={change} hint="верхня межа після FLOOD_WAIT" />
          <Field label="RPC timeout" setting={s('Collectors:Telegram:RpcTimeout')} draft={draft} onChange={change} hint="після timeout сесія колектора перезапускається" />
        </div>
        <Toggle label="Підписуватись на канали автоматично" setting={s('Collectors:Telegram:AutoJoin')} draft={draft} onChange={change} hint="без підписки live-повідомлення не приходять" />
      </Section>
      {(waiting || tgStatus === 'connecting') && (
        <Section title="Код входу" badge={<Badge ok={null} text="потрібен зараз" />}>
          <div className="flex gap-2">
            <input className="w-40 rounded border border-slate-300 px-2 py-1 font-mono dark:border-slate-600 dark:bg-slate-800" placeholder="12345" value={code} onChange={(e) => setCode(e.target.value)} />
            <button className="rounded bg-slate-800 px-3 py-1 text-white dark:bg-slate-100 dark:text-slate-900" onClick={() => void sendCode()} disabled={!code.trim()}>
              Надіслати
            </button>
          </div>
          <p className="text-xs text-slate-500">Код прийшов у Telegram на цей акаунт (не SMS). Worker чекає до 10 хвилин.</p>
        </Section>
      )}
    </>
  )
}

const LLM_PROVIDERS = [
  { value: 'Anthropic', label: 'Anthropic (Claude)', model: 'claude-opus-5' },
  { value: 'OpenAI', label: 'OpenAI', model: 'gpt-5-mini' },
  { value: 'Ollama', label: 'Ollama (локально)', model: 'qwen3:8b' },
]

function LlmSection({ status, draft, change, s }: TabProps) {
  const selected = (draft['Llm:Provider'] ?? s('Llm:Provider')?.value ?? 'Anthropic').toLowerCase()
  const provider = LLM_PROVIDERS.find((p) => p.value.toLowerCase() === selected) ?? LLM_PROVIDERS[0]
  const providerLabel = provider.label
  return (
    <>
      <Section title={`LLM fallback (${providerLabel})`} badge={<Badge ok={status?.llmConfigured ?? null} text={status?.llmConfigured ? 'увімкнено' : 'вимкнено'} />}>
        <p className="text-xs text-slate-500">Викликається лише коли правила не знайшли нічого в тексті, схожому на повідомлення про загрозу. Модель може повертати лише коди з таксономії; невідомі місця відкидаються.</p>
        <Toggle label="Увімкнути" setting={s('Llm:Enabled')} draft={draft} onChange={change} />
        <Select label="Провайдер" setting={s('Llm:Provider')} draft={draft} onChange={change} options={LLM_PROVIDERS} hint="Перемикається без перезапуску; модель і ключ мають відповідати провайдеру." />
        <Field label="Модель" setting={s('Llm:Model')} draft={draft} onChange={change} placeholder={provider.model} />
        {provider.value !== 'Ollama' && (
          <Field label="API key" setting={s('Llm:ApiKey')} draft={draft} onChange={change} type="password" hint={`або змінна середовища ${provider.value === 'OpenAI' ? 'OPENAI_API_KEY' : 'ANTHROPIC_API_KEY'}`} />
        )}
        {provider.value !== 'Anthropic' && (
          <Field
            label="Base URL"
            setting={s('Llm:BaseUrl')}
            draft={draft}
            onChange={change}
            placeholder={provider.value === 'Ollama' ? 'http://localhost:11434/v1' : 'https://api.openai.com/v1'}
            hint={provider.value === 'Ollama' ? 'OpenAI-сумісний endpoint Ollama. З Docker: http://host.docker.internal:11434/v1' : 'порожньо = api.openai.com; або будь-який OpenAI-сумісний сервер'}
          />
        )}
        <Field label="Таймаут, с" setting={s('Llm:TimeoutSeconds')} draft={draft} onChange={change} type="number" hint={provider.value === 'Ollama' ? 'локальна модель відповідає повільніше — варто 60+' : undefined} />
        <Field label="Input, $ / млн токенів" setting={s('Llm:InputUsdPerMillionTokens')} draft={draft} onChange={change} type="number" />
        <Field label="Output, $ / млн токенів" setting={s('Llm:OutputUsdPerMillionTokens')} draft={draft} onChange={change} type="number" />
        <Field label="Cache write, $ / млн" setting={s('Llm:CacheWriteUsdPerMillionTokens')} draft={draft} onChange={change} type="number" />
        <Field label="Cache read, $ / млн" setting={s('Llm:CacheReadUsdPerMillionTokens')} draft={draft} onChange={change} type="number" hint="Зміна ціни впливає лише на наступні виклики." />
      </Section>
      <LlmUsagePanel />
    </>
  )
}

function SystemSection({ status, draft, change, s }: TabProps) {
  return (
    <>
      <Section title="Стан" badge={<Badge ok={status?.workerAlive ?? null} text={status?.workerAlive ? 'Worker працює' : 'Worker не відповідає'} />}>
        <div className="text-xs text-slate-500">
          Останній heartbeat: {status?.workerLastSeen ? new Date(status.workerLastSeen).toLocaleTimeString('uk-UA') : '—'}. Налаштування зберігаються в БД (<code>app_settings</code>) і перекривають appsettings/env; Worker перечитує їх кожні 5 с.
        </div>
      </Section>
      <Section title="Доступ" badge={<Badge ok={status?.adminTokenSet ?? null} text={status?.adminTokenSet ? 'токен задано' : 'лише localhost'} />}>
        <Field label="Адмін-токен" setting={s('Admin:Token')} draft={draft} onChange={change} type="password" hint="Поки не задано, ця сторінка доступна лише з localhost. Після збереження токен запам'ятовується у цьому браузері." />
      </Section>
      <Section title="Кореляція">
        <Field label="Поріг приєднання до треку (0–1)" setting={s('Correlation:AttachThreshold')} draft={draft} onChange={change} type="number" hint="0.6 типово; вище — більше окремих треків, нижче — агресивніше злиття" />
        <Field label="Вікно пошуку кандидатів (хв)" setting={s('Correlation:CandidateWindowMinutes')} draft={draft} onChange={change} type="number" hint="120 типово; межа часу для добору активних треків, перед точним оцінюванням" />
        <Field label="Запас переваги кандидата (0–1)" setting={s('Correlation:AmbiguityMargin')} draft={draft} onChange={change} type="number" hint="0.05 типово; якщо різниця між двома найкращими балами менша, створюється окремий трек" />
        <Field label="Просторовий запас (км)" setting={s('Correlation:SlackKm')} draft={draft} onChange={change} type="number" hint="8 типово; додається лише до швидкості класу × час, а не замінює фізичне обмеження" />
        <Field label="Межа грубої локації (км)" setting={s('Correlation:CoarseLocationAccuracyKm')} draft={draft} onChange={change} type="number" hint="80 типово; дві локації з такою або гіршою точністю не зливаються без конкретнішого факту" />
      </Section>
    </>
  )
}
