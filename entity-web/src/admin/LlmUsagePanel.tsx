import { useState } from 'react'
import { admin, type LlmOutcomeFilter, type LlmRequestDetailDto, type LlmRequestDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { messagesHash } from './messages'
import { Bars, Freshness, Loading, PagerButtons, Stat, fmtMs, fmtNum, fmtTime, fmtUsd, usePaged, usePolled } from './shared'

const PERIODS: { hours: 24 | 168 | 720; label: string }[] = [
  { hours: 24, label: '24 год' },
  { hours: 168, label: '7 днів' },
  { hours: 720, label: '30 днів' },
]

const OUTCOMES: { value: LlmOutcomeFilter; label: string }[] = [
  { value: 'all', label: 'усі результати' },
  { value: 'failures', label: 'лише помилки' },
  { value: 'facts', label: 'з фактами' },
  { value: 'success', label: 'успішні (EE)' },
  { value: 'empty', label: 'порожні' },
  { value: 'refusal', label: 'відмови' },
]

function outcome(row: LlmRequestDto) {
  if (row.outcome === 'facts') return <Badge ok text={`фактів: ${row.factsCount}`} />
  if (row.outcome === 'success') return <Badge ok text="успішно" />
  if (row.outcome === 'empty') return <Badge ok={null} text="порожньо" />
  if (row.outcome === 'refusal') return <Badge ok={null} text="відмова" />
  return <Badge ok={false} text={row.statusCode ? `HTTP ${row.statusCode}` : row.outcome || 'помилка'} />
}

/** Token/cost audit. Full prompt and output bodies are deliberately fetched only after an operator opens one row. */
export default function LlmUsagePanel() {
  const [hours, setHours] = useState<24 | 168 | 720>(168)
  const { data, error, stale, loadedAt } = usePolled(() => admin.ops.llm(hours), 30_000, [hours])
  const period = PERIODS.find((p) => p.hours === hours)!.label
  return (
    <Section title="Використання й вартість" badge={data && <Badge ok={data.failures === 0 ? true : false} text={`${fmtNum(data.calls)} викликів`} />}>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {PERIODS.map((p) => (
          <button key={p.hours} aria-pressed={hours === p.hours} className={`rounded border px-2 py-1 text-xs ${hours === p.hours ? 'border-sky-500 bg-sky-50 text-sky-700 dark:bg-sky-950' : 'border-slate-300 dark:border-slate-600'}`} onClick={() => setHours(p.hours)}>
            {p.label}
          </button>
        ))}
        <span className="text-xs text-slate-500">оновлюється кожні 30 с; сума — оцінка за тарифом у момент виклику</span>
        <Freshness stale={stale} loadedAt={loadedAt} />
      </div>
      <Loading error={error} empty={!data} />
      {data && (
        <div className={stale ? 'opacity-50' : undefined} aria-busy={stale}>
          <div className="grid grid-cols-1 gap-2 text-xs min-[420px]:grid-cols-2 sm:grid-cols-4">
            <Stat label="Вартість" value={fmtUsd(data.estimatedCostUsd)} hint="USD, без податків і кредитів" />
            <Stat label="Виклики" value={fmtNum(data.calls)} hint={`фактів ${fmtNum(data.withFacts)} · порожньо ${fmtNum(data.empty)} · помилок ${fmtNum(data.failures)}`} />
            <Stat label="Токени input" value={fmtNum(data.inputTokens + data.cacheWriteTokens + data.cacheReadTokens)} hint={`звичайні ${fmtNum(data.inputTokens)} · cache read ${fmtNum(data.cacheReadTokens)}`} />
            <Stat label="Токени output" value={fmtNum(data.outputTokens)} hint={`середня тривалість ${fmtMs(data.meanDurationMs)}`} />
          </div>
          {data.timeline.length > 0 && (
            <div className="mt-3 flex items-end gap-3 text-xs text-slate-500">
              <Bars values={data.timeline.map((x) => x.estimatedCostUsd)} color="bg-violet-500" height={36} width="w-2" title={(i, v) => `${fmtTime(data.timeline[i].at)} · ${fmtUsd(v)}`} />
              <span>витрати за днями</span>
            </div>
          )}
        </div>
      )}
      <LlmHistory hours={hours} period={period} />
    </Section>
  )
}

/** Every request of the period, 100 per page, newest first: filter by result, search by request / message id or source code. */
function LlmHistory({ hours, period }: { hours: 24 | 168 | 720; period: string }) {
  const [filter, setFilter] = useState<LlmOutcomeFilter>('all')
  const [search, setSearch] = useState('')
  const [applied, setApplied] = useState('')
  const [detail, setDetail] = useState<LlmRequestDetailDto | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const page = usePaged<LlmRequestDto, number>(async (beforeId) => {
    const r = await admin.ops.llmRequests(hours, filter, applied, beforeId)
    return { items: r.requests, next: r.nextBeforeId, total: r.total }
  }, [hours, filter, applied])
  const open = async (id: number) => {
    setDetailError(null)
    try {
      setDetail(await admin.ops.llmRequest(id))
    } catch (e) {
      setDetailError((e as Error).message)
    }
  }
  return (
    <div className="mt-4 space-y-2">
      <div className="flex flex-wrap items-end gap-2 text-xs">
        <b className="mr-auto text-sm">Історія викликів · {period}</b>
        <select className="rounded border border-slate-300 bg-white px-2 py-1 dark:border-slate-600 dark:bg-slate-800" aria-label="Результат виклику" value={filter} onChange={(e) => setFilter(e.target.value as LlmOutcomeFilter)}>
          {OUTCOMES.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        <form
          className="flex gap-1"
          onSubmit={(e) => {
            e.preventDefault()
            setApplied(search.trim())
          }}
        >
          <input className="w-44 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" aria-label="Пошук викликів" placeholder="ID запиту / повідомлення, джерело" value={search} onChange={(e) => setSearch(e.target.value)} />
          <button className="rounded border border-slate-300 px-2 py-1 dark:border-slate-600">Знайти</button>
          {applied && (
            <button
              type="button"
              className="rounded border border-slate-300 px-2 py-1 dark:border-slate-600"
              onClick={() => {
                setSearch('')
                setApplied('')
              }}
            >
              Очистити
            </button>
          )}
        </form>
      </div>
      <Loading error={page.error} empty={page.items === null} />
      {page.items && (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="text-slate-500">
              <tr>
                <th className="py-1 pr-2">Час</th>
                <th className="pr-2">ID</th>
                <th className="pr-2">Джерело</th>
                <th className="pr-2">Результат</th>
                <th className="pr-2">Токени</th>
                <th className="pr-2">Вартість</th>
                <th className="pr-2">Час відповіді</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {page.items.map((r) => (
                <tr key={r.id} className="border-t border-slate-100 dark:border-slate-800" data-testid="llm-request">
                  <td className="whitespace-nowrap py-1.5 pr-2" title={r.model}>
                    {fmtTime(r.occurredAt)}
                  </td>
                  <td className="whitespace-nowrap pr-2 font-mono">
                    #{r.id}
                    {r.rawMessageId && (
                      <a className="ml-1 text-slate-500 underline" href={messagesHash({ rawMessageId: r.rawMessageId })} title="Відкрити повідомлення">
                        msg {r.rawMessageId}
                      </a>
                    )}
                  </td>
                  <td className="pr-2 font-mono">{r.sourceCode}</td>
                  <td className="pr-2">{outcome(r)}</td>
                  <td className="pr-2">{fmtNum((r.inputTokens ?? 0) + (r.cacheWriteTokens ?? 0) + (r.cacheReadTokens ?? 0) + (r.outputTokens ?? 0))}</td>
                  <td className="pr-2 font-mono">{fmtUsd(r.estimatedCostUsd)}</td>
                  <td className="pr-2">{fmtMs(r.durationMs)}</td>
                  <td>
                    <button className="rounded border border-slate-300 px-2 py-0.5 text-xs dark:border-slate-600" onClick={() => void open(r.id)}>
                      Деталі
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {page.items.length === 0 && <p className="py-3 text-xs text-slate-500">{filter === 'all' && !applied ? 'За цей період LLM ще не викликався. Старі виклики до появи аудиту тут не відновлюються.' : 'Немає викликів за цим фільтром.'}</p>}
        </div>
      )}
      <PagerButtons busy={page.busy} hasMore={page.hasMore} count={page.items?.length} total={page.total} onMore={page.more} onRefresh={page.refresh} />
      {detailError && <p className="mt-3 text-xs text-red-600">{detailError}</p>}
      {detail && (
        <div className="mt-4 rounded border border-slate-200 p-3 text-xs dark:border-slate-700">
          <div className="mb-2 flex items-center justify-between">
            <b>
              Запит #{detail.request.id} · {detail.request.model}
            </b>
            <button className="text-slate-500 underline" onClick={() => setDetail(null)}>
              Закрити
            </button>
          </div>
          <AuditText title="Надісланий текст" value={detail.requestText} />
          <AuditText title="System prompt" value={detail.systemPrompt} />
          <AuditText title="Відповідь моделі" value={detail.responseText ?? detail.request.error ?? '—'} />
        </div>
      )}
    </div>
  )
}

function AuditText({ title, value }: { title: string; value: string }) {
  return (
    <details className="mb-2">
      <summary className="cursor-pointer text-slate-600 dark:text-slate-300">{title}</summary>
      <pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap rounded bg-slate-50 p-2 text-[11px] dark:bg-slate-800">{value}</pre>
    </details>
  )
}
