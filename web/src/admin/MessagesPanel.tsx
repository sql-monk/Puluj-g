import { useCallback, useEffect, useState } from 'react'
import { adminOps, type LifecycleEventDto, type MessageLifecycleDto, type MessageSearchRowDto } from '../api/adminOps'
import { Badge, Section } from '../components/settings/fields'
import { fmtTime } from './shared'

const OUTCOME: Record<string, string> = { completed: 'завершено', noop: 'noop', quarantined: 'КАРАНТИН', waived: 'списано' }
const COMPLETION: Record<string, { text: string; ok: boolean | null }> = {
  completed: { text: 'усі гілки завершені', ok: true },
  in_progress: { text: 'у роботі', ok: null },
  needs_attention: { text: 'ПОТРЕБУЄ УВАГИ', ok: false },
  pending: { text: 'подій ще немає', ok: null },
}

function safeHref(url?: string | null): boolean {
  return typeof url === 'string' && /^https?:\/\//i.test(url)
}

function rawFromHash(): number | null {
  const m = /[?&]raw=(\d+)/.exec(window.location.hash)
  return m ? Number(m[1]) : null
}

/**
 * Панель «Повідомлення» (P13, §8.7): пошук raw-повідомлення (id / ключ джерела / текст) і одна картка всього lifecycle —
 * події → доставки з квитанціями і attempts (помилки повністю), extractions/observations, похідні (tracks/alerts/incidents),
 * карантин, підсумок «хто чекає / хто завершив / хто помилився». Текст джерела — лише текст (React екранує); посилання — лише http(s).
 */
export function MessagesPanel() {
  const [q, setQ] = useState('')
  const [hours, setHours] = useState<24 | 168 | 720>(24)
  const [rows, setRows] = useState<MessageSearchRowDto[] | null>(null)
  const [card, setCard] = useState<MessageLifecycleDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const open = useCallback(async (rawId: number) => {
    setBusy(true)
    try {
      setCard(await adminOps.lifecycle(rawId))
      setError(null)
    } catch (e) {
      setError(`#${rawId}: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusy(false)
    }
  }, [])
  const search = async () => {
    setBusy(true)
    try {
      setRows(await adminOps.search(q.trim(), hours))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }
  useEffect(() => {
    const follow = () => {
      const raw = rawFromHash()
      if (raw) void open(raw)
    }
    const t = window.setTimeout(follow, 0) // the load is a fetch (external system); deferred so the effect itself sets no state
    window.addEventListener('hashchange', follow) // a `raw #N` link clicked while the panel is already open
    return () => {
      window.clearTimeout(t)
      window.removeEventListener('hashchange', follow)
    }
  }, [open])

  return (
    <div className="space-y-4 text-sm" data-testid="messages-panel">
      <Section title="Пошук">
        <form
          className="flex flex-wrap items-end gap-2 text-xs"
          onSubmit={(e) => {
            e.preventDefault()
            void search()
          }}
        >
          <label className="grow">
            raw id, ключ джерела або фрагмент тексту
            <input className="mt-1 w-full rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={q} onChange={(e) => setQ(e.target.value)} aria-label="запит" placeholder="напр. 12345 або «Шахеди»" />
          </label>
          <label>
            Період
            <select className="ml-1 rounded border border-slate-300 px-2 py-1 dark:border-slate-600 dark:bg-slate-800" value={hours} onChange={(e) => setHours(Number(e.target.value) as 24 | 168 | 720)} aria-label="період">
              <option value={24}>24 год</option>
              <option value={168}>7 д</option>
              <option value={720}>30 д (текст — до 7 д)</option>
            </select>
          </label>
          <button type="submit" className="rounded bg-slate-800 px-3 py-1 text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" disabled={busy}>
            Шукати
          </button>
        </form>
        {error && (
          <p className="text-xs text-red-600" role="alert">
            {error}
          </p>
        )}
        {rows && (
          <div className="overflow-x-auto">
            {rows.length === 0 ? (
              <p className="text-xs text-slate-500">Нічого не знайдено.</p>
            ) : (
              <table className="w-full text-xs" data-testid="search-results">
                <thead className="text-left text-[10px] uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="py-1 pr-2">raw</th>
                    <th className="pr-2">Джерело</th>
                    <th className="pr-2">Ключ</th>
                    <th className="pr-2">Опубліковано</th>
                    <th className="pr-2">Статус</th>
                    <th className="pr-2">Витяги/факти</th>
                    <th className="pr-2">Остання квитанція</th>
                    <th>Текст</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.rawMessageId} className="border-t border-slate-100 dark:border-slate-800">
                      <td className="py-1 pr-2">
                        <button className="font-mono underline" onClick={() => void open(r.rawMessageId)}>
                          #{r.rawMessageId}
                        </button>
                      </td>
                      <td className="pr-2 font-mono">{r.sourceCode}</td>
                      <td className="pr-2 font-mono">{r.sourceMessageId}</td>
                      <td className="pr-2">{fmtTime(r.publishedAt)}</td>
                      <td className="pr-2">{r.status}</td>
                      <td className="pr-2 font-mono">
                        {r.extractions}/{r.observations}
                      </td>
                      <td className="pr-2">{r.lastOutcome ? OUTCOME[r.lastOutcome] ?? r.lastOutcome : 'очікує'}</td>
                      <td className="max-w-md truncate">{r.textPreview}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </Section>

      {card && <LifecycleCard card={card} />}
    </div>
  )
}

function LifecycleCard({ card }: { card: MessageLifecycleDto }) {
  const c = COMPLETION[card.summary.completion] ?? { text: card.summary.completion, ok: null }
  return (
    <Section title={`Повідомлення #${card.rawMessageId} · ${card.sourceCode} · ${card.sourceMessageId}`} badge={<Badge ok={c.ok} text={c.text} />}>
      <div className="space-y-3" data-testid="lifecycle-card">
        <div className="grid gap-2 text-xs sm:grid-cols-3">
          <div>
            <div className="text-[10px] uppercase text-slate-500">Опубліковано / отримано</div>
            {fmtTime(card.publishedAt)} / {fmtTime(card.receivedAt)}
          </div>
          <div>
            <div className="text-[10px] uppercase text-slate-500">Legacy-статус</div>
            {card.status}
          </div>
          <div>
            <div className="text-[10px] uppercase text-slate-500">Джерело</div>
            {safeHref(card.url) ? (
              <a className="underline" href={card.url!} target="_blank" rel="noreferrer noopener">
                оригінал
              </a>
            ) : (
              '—'
            )}
          </div>
        </div>
        {card.text && (
          <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-words rounded bg-slate-50 p-2 font-sans text-xs dark:bg-slate-800/60" data-testid="raw-text">
            {card.text}
          </pre>
        )}

        <div className="grid gap-2 text-xs sm:grid-cols-3" data-testid="summary">
          <SummaryList label="Чекають" items={card.summary.waiting} tone="text-amber-700 dark:text-amber-300" />
          <SummaryList label="Завершили" items={card.summary.completed} tone="text-emerald-700 dark:text-emerald-300" />
          <SummaryList label="Помилилися" items={card.summary.failed} tone="text-red-700 dark:text-red-300" />
        </div>

        <div>
          <h4 className="text-xs font-semibold uppercase text-slate-500">Події та доставки {card.eventsTruncated ? '(показано перші 200)' : ''}</h4>
          {card.events.length === 0 ? (
            <p className="text-xs text-slate-500">Архів ще не має подій цього повідомлення (архівна підписка не завершила, або шину вимкнено).</p>
          ) : (
            <ol className="space-y-2" data-testid="events">
              {card.events.map((e) => (
                <EventRow key={e.eventId} e={e} />
              ))}
            </ol>
          )}
        </div>

        {card.quarantine.length > 0 && (
          <div data-testid="quarantine">
            <h4 className="text-xs font-semibold uppercase text-red-700 dark:text-red-300">Карантин</h4>
            <ul className="space-y-1 text-xs">
              {card.quarantine.map((q) => (
                <li key={q.quarantineId} className="rounded border border-red-200 p-2 dark:border-red-900">
                  <div className="font-mono">
                    #{q.quarantineId} · {q.subscriptionId}/{q.lane} · {q.reason} · {fmtTime(q.quarantinedAt)}
                    {q.resolvedAt ? ` · вирішено: ${q.resolution ?? '?'} ${fmtTime(q.resolvedAt)}` : ' · відкрито'}
                  </div>
                  {q.error && <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-[11px]">{q.error}</pre>}
                  <details className="mt-1">
                    <summary className="cursor-pointer text-[11px] text-slate-500">envelope{q.envelopeTruncated ? ' (обрізано, повний — у таблиці processing.quarantine)' : ''}</summary>
                    <pre className="max-h-40 overflow-auto whitespace-pre-wrap break-all font-mono text-[11px]">{q.envelopePreview}</pre>
                  </details>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="grid gap-3 text-xs sm:grid-cols-2">
          <div>
            <h4 className="font-semibold uppercase text-slate-500">Витяги (extractions)</h4>
            {card.extractions.length === 0 ? (
              <p className="text-slate-500">—</p>
            ) : (
              <ul className="space-y-0.5 font-mono">
                {card.extractions.map((x) => (
                  <li key={x.extractionId}>
                    v{x.version} {x.method} → {x.outcome} · {x.observations} факт(ів) · {x.finalizedBy} · {fmtTime(x.createdAt)}
                    {x.error && <div className="whitespace-pre-wrap break-words text-red-700 dark:text-red-300">{x.error}</div>}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div>
            <h4 className="font-semibold uppercase text-slate-500">Факти (observations)</h4>
            {card.observations.length === 0 ? (
              <p className="text-slate-500">—</p>
            ) : (
              <ul className="space-y-0.5">
                {card.observations.map((o) => (
                  <li key={o.observationId} className="font-mono">
                    {o.kind} ({o.category}) {fmtTime(o.effectiveAt)}
                    {o.legacyTargetId ? ` · target #${o.legacyTargetId}` : ''}
                    <span className="block truncate text-[11px] text-slate-500" title={o.payloadPreview}>
                      {o.payloadPreview}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
        <div className="text-xs">
          <h4 className="font-semibold uppercase text-slate-500">Похідне</h4>
          {card.derived.length === 0 ? (
            <p className="text-slate-500">—</p>
          ) : (
            <ul className="flex flex-wrap gap-2 font-mono">
              {card.derived.map((d) => (
                <li key={`${d.kind}-${d.id}`} className="rounded bg-slate-100 px-1.5 py-0.5 dark:bg-slate-800">
                  {d.label}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </Section>
  )
}

function SummaryList({ label, items, tone }: { label: string; items: string[]; tone: string }) {
  return (
    <div>
      <div className="text-[10px] uppercase text-slate-500">
        {label} ({items.length})
      </div>
      <ul className={`font-mono ${tone}`}>
        {items.length === 0 ? <li className="text-slate-400">—</li> : items.map((i) => <li key={i}>{i}</li>)}
      </ul>
    </div>
  )
}

function EventRow({ e }: { e: LifecycleEventDto }) {
  return (
    <li className="rounded border border-slate-200 p-2 text-xs dark:border-slate-700" data-testid={`event-${e.eventType}`}>
      <div className="flex flex-wrap items-baseline gap-2 font-mono">
        <span className="font-semibold">{e.eventType}</span>
        <span className="text-slate-500">{e.lane}</span>
        <span className="text-slate-500">{e.producer}</span>
        <span className="text-slate-500" title={e.eventId}>
          occurred {fmtTime(e.occurredAt)} · published {fmtTime(e.publishedAt)} · confirmed {fmtTime(e.confirmedAt)}
        </span>
      </div>
      {e.deliveries.length === 0 ? (
        <div className="text-slate-500">без очікуваних доставок</div>
      ) : (
        <ul className="mt-1 space-y-0.5">
          {e.deliveries.map((d) => (
            <li key={`${d.subscription}/${d.lane}`} className="pl-2" data-outcome={d.outcome ?? 'pending'}>
              <span className="font-mono">
                {d.subscription}/{d.lane}
              </span>{' '}
              <span className={d.outcome === 'quarantined' ? 'font-semibold text-red-700 dark:text-red-300' : d.outcome ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'}>
                {d.outcome ? OUTCOME[d.outcome] ?? d.outcome : 'очікує'}
              </span>
              {d.reason && <span className="text-slate-500"> · {d.reason}</span>}
              {d.actor && <span className="text-slate-500"> · {d.actor}</span>}
              <span className="text-slate-400">
                {' '}
                · expected {fmtTime(d.expectedAt)}
                {d.completedAt ? ` · completed ${fmtTime(d.completedAt)}` : ''}
              </span>
              {d.attempts.length > 0 && (
                <ul className="ml-3 text-[11px]">
                  {d.attempts.map((a) => (
                    <li key={a.attemptId} className="font-mono" data-state={a.state}>
                      #{a.attemptId} {a.worker} {a.state} {fmtTime(a.startedAt)}
                      {a.finishedAt ? ` → ${fmtTime(a.finishedAt)}` : ''}
                      {a.retryOfAttemptId ? ` (retry of #${a.retryOfAttemptId}: ${a.retryReason ?? ''})` : ''}
                      {a.error && <pre className="whitespace-pre-wrap break-words text-red-700 dark:text-red-300">{a.error}</pre>}
                    </li>
                  ))}
                  {d.attemptsTruncated && <li className="text-slate-500">показано останні 20 attempts</li>}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}
