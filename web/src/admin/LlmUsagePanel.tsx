import { useState } from 'react'
import { admin, type LlmRequestDetailDto, type LlmRequestDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { Bars, Loading, Stat, fmtMs, fmtNum, fmtTime, fmtUsd, usePolled } from './shared'

const PERIODS: { hours: 24 | 168 | 720; label: string }[] = [
  { hours: 24, label: '24 год' },
  { hours: 168, label: '7 днів' },
  { hours: 720, label: '30 днів' },
]

function outcome(row: LlmRequestDto) {
  if (row.outcome === 'facts') return <Badge ok text={`фактів: ${row.factsCount}`} />
  if (row.outcome === 'empty') return <Badge ok={null} text="порожньо" />
  if (row.outcome === 'refusal') return <Badge ok={null} text="відмова" />
  return <Badge ok={false} text={row.statusCode ? `HTTP ${row.statusCode}` : 'помилка'} />
}

/** Token/cost audit. Full prompt and output bodies are deliberately fetched only after an operator opens one row. */
export default function LlmUsagePanel() {
  const [hours, setHours] = useState<24 | 168 | 720>(168)
  const { data, error } = usePolled(() => admin.ops.llm(hours), 30_000, [hours])
  const [detail, setDetail] = useState<LlmRequestDetailDto | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const open = async (id: number) => {
    setDetailError(null)
    try {
      setDetail(await admin.ops.llmRequest(id))
    } catch (e) {
      setDetailError((e as Error).message)
    }
  }
  return (
    <Section title="Використання й вартість" badge={data && <Badge ok={data.failures === 0 ? true : false} text={`${fmtNum(data.calls)} викликів`} />}>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {PERIODS.map((p) => <button key={p.hours} className={`rounded border px-2 py-1 text-xs ${hours === p.hours ? 'border-sky-500 bg-sky-50 text-sky-700 dark:bg-sky-950' : 'border-slate-300 dark:border-slate-600'}`} onClick={() => setHours(p.hours)}>{p.label}</button>)}
        <span className="text-xs text-slate-500">оновлюється кожні 30 с; сума — оцінка за тарифом у момент виклику</span>
      </div>
      <Loading error={error} empty={!data} />
      {data && <>
        <div className="grid grid-cols-2 gap-2 text-xs sm:grid-cols-4">
          <Stat label="Вартість" value={fmtUsd(data.estimatedCostUsd)} hint="USD, без податків і кредитів" />
          <Stat label="Виклики" value={fmtNum(data.calls)} hint={`фактів ${fmtNum(data.withFacts)} · порожньо ${fmtNum(data.empty)}`} />
          <Stat label="Токени input" value={fmtNum(data.inputTokens + data.cacheWriteTokens + data.cacheReadTokens)} hint={`звичайні ${fmtNum(data.inputTokens)} · cache read ${fmtNum(data.cacheReadTokens)}`} />
          <Stat label="Токени output" value={fmtNum(data.outputTokens)} hint={`середня тривалість ${fmtMs(data.meanDurationMs)}`} />
        </div>
        {data.timeline.length > 0 && <div className="mt-3 flex items-end gap-3 text-xs text-slate-500"><Bars values={data.timeline.map((x) => x.estimatedCostUsd)} color="bg-violet-500" height={36} width="w-2" title={(i, v) => `${fmtTime(data.timeline[i].at)} · ${fmtUsd(v)}`} /><span>витрати за днями</span></div>}
        <div className="mt-4 overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="text-slate-500"><tr><th className="py-1 pr-2">Час</th><th className="pr-2">Джерело</th><th className="pr-2">Результат</th><th className="pr-2">Токени</th><th className="pr-2">Вартість</th><th className="pr-2">Час відповіді</th><th /></tr></thead>
            <tbody>{data.recent.map((r) => <tr key={r.id} className="border-t border-slate-100 dark:border-slate-800">
              <td className="whitespace-nowrap py-1.5 pr-2" title={r.model}>{fmtTime(r.occurredAt)}</td><td className="pr-2 font-mono">{r.sourceCode}</td><td className="pr-2">{outcome(r)}</td>
              <td className="pr-2">{fmtNum((r.inputTokens ?? 0) + (r.cacheWriteTokens ?? 0) + (r.cacheReadTokens ?? 0) + (r.outputTokens ?? 0))}</td><td className="pr-2 font-mono">{fmtUsd(r.estimatedCostUsd)}</td><td className="pr-2">{fmtMs(r.durationMs)}</td>
              <td><button className="rounded border border-slate-300 px-2 py-0.5 text-xs dark:border-slate-600" onClick={() => void open(r.id)}>Деталі</button></td>
            </tr>)}</tbody>
          </table>
          {data.recent.length === 0 && <p className="py-3 text-xs text-slate-500">За цей період LLM ще не викликався. Старі виклики до появи аудиту тут не відновлюються.</p>}
        </div>
      </>}
      {detailError && <p className="mt-3 text-xs text-red-600">{detailError}</p>}
      {detail && <div className="mt-4 rounded border border-slate-200 p-3 text-xs dark:border-slate-700">
        <div className="mb-2 flex items-center justify-between"><b>Запит #{detail.request.id} · {detail.request.model}</b><button className="text-slate-500 underline" onClick={() => setDetail(null)}>Закрити</button></div>
        <AuditText title="Надісланий текст" value={detail.requestText} /><AuditText title="System prompt" value={detail.systemPrompt} /><AuditText title="Відповідь моделі" value={detail.responseText ?? detail.request.error ?? '—'} />
      </div>}
    </Section>
  )
}

function AuditText({ title, value }: { title: string; value: string }) {
  return <details className="mb-2"><summary className="cursor-pointer text-slate-600 dark:text-slate-300">{title}</summary><pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap rounded bg-slate-50 p-2 text-[11px] dark:bg-slate-800">{value}</pre></details>
}
