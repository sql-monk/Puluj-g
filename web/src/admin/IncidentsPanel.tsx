import { useCallback, useEffect, useState } from 'react'
import { adminIncidents, type AdminIncidentDto, type MergePreviewDto, type ReviewItemDto } from '../api/adminCatalog'
import { Badge, Section } from '../components/settings/fields'
import { stateLabel } from '../lib/incidentLabels'

const HOURS: { hours: 24 | 168 | 720; label: string }[] = [
  { hours: 24, label: '24 год' },
  { hours: 168, label: '7 д' },
  { hours: 720, label: '30 д' },
]

function safeHref(url?: string): boolean {
  return typeof url === 'string' && /^https?:\/\//i.test(url)
}

/**
 * Incident review queue (plan §8.7, P12): incidents the policy flagged (an ambiguous link, near candidates) with their
 * candidates and a merge preview; the commands (merge with preview, split, resolve/retract/confirm/suppress) go through the
 * same state writer as the worker (P10) and need actor + reason. Source text is rendered as text only (React escapes it);
 * raw permalinks are links only when they are http(s).
 */
export function IncidentsPanel() {
  const [hours, setHours] = useState<24 | 168 | 720>(24)
  const [queue, setQueue] = useState<ReviewItemDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<AdminIncidentDto | null>(null)
  const [preview, setPreview] = useState<MergePreviewDto | null>(null)
  const [splitPick, setSplitPick] = useState<Set<string>>(new Set())
  const [actor, setActor] = useState('')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [mergeTarget, setMergeTarget] = useState('')
  const [stateFilter, setStateFilter] = useState('')
  const [kindFilter, setKindFilter] = useState('')
  const [truncated, setTruncated] = useState(false)

  const load = useCallback(async () => {
    try {
      const r = await adminIncidents.review(hours)
      setQueue(r.items)
      setTruncated(r.truncated)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [hours])
  useEffect(() => {
    const t = window.setTimeout(() => void load(), 0) // the load is a fetch (external system); deferred so the effect itself sets no state
    return () => window.clearTimeout(t)
  }, [load])

  const open = async (id: number) => {
    setPreview(null)
    setSplitPick(new Set())
    setMessage(null)
    try {
      setSelected(await adminIncidents.get(id))
    } catch (e) {
      setMessage(`#${id}: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  const run = async (label: string, action: () => Promise<unknown>, reopen?: number) => {
    setBusy(true)
    try {
      await action()
      setReason('')
      await load()
      if (reopen !== undefined) await open(reopen)
      setMessage(`${label}: виконано`) // after the reopen, which clears the status line
    } catch (e) {
      setMessage(`${label}: помилка — ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusy(false)
    }
  }

  const ready = actor.trim() !== '' && reason.trim() !== ''

  return (
    <>
      <Section
        title="Інциденти: черга ревʼю"
        badge={
          <span className="flex gap-1">
            {HOURS.map((p) => (
              <button key={p.hours} className={`rounded px-2 py-0.5 text-xs ${hours === p.hours ? 'bg-slate-800 text-white dark:bg-slate-100 dark:text-slate-900' : 'bg-slate-100 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700'}`} onClick={() => setHours(p.hours)}>
                {p.label}
              </button>
            ))}
          </span>
        }
      >
        <p className="text-xs text-slate-600 dark:text-slate-300">Incidents, які policy не змогла розмістити впевнено: link `ambiguous` або кандидати поруч. Команди — через той самий writer, що й worker; потрібні actor і reason.</p>
        <div className="flex flex-wrap gap-2 text-xs">
          <label className="flex items-center gap-1">
            Actor
            <input className="rounded border border-slate-300 px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={actor} onChange={(e) => setActor(e.target.value)} aria-label="Actor" />
          </label>
          <label className="flex flex-1 items-center gap-1">
            Reason
            <input className="w-full rounded border border-slate-300 px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={reason} onChange={(e) => setReason(e.target.value)} aria-label="Reason" />
          </label>
        </div>
        {message && <div className="text-xs" role="status">{message}</div>}
        {error && <div className="text-xs text-red-600">{error}</div>}
        {queue && queue.length > 0 && (
          <div className="flex flex-wrap gap-2 text-xs">
            <select aria-label="Стан" className="rounded border px-1 dark:bg-slate-800" value={stateFilter} onChange={(e) => setStateFilter(e.target.value)}>
              <option value="">усі стани</option>
              {[...new Set(queue.map((i) => i.state))].map((s) => (
                <option key={s} value={s}>
                  {stateLabel[s] ?? s}
                </option>
              ))}
            </select>
            <select aria-label="Вид" className="rounded border px-1 dark:bg-slate-800" value={kindFilter} onChange={(e) => setKindFilter(e.target.value)}>
              <option value="">усі види</option>
              {[...new Set(queue.map((i) => i.kind))].map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
            {truncated && <span className="text-amber-700 dark:text-amber-300">показано перші {queue.length} — звузьте період</span>}
          </div>
        )}
        {queue && queue.length === 0 && <div className="text-xs text-slate-500">черга порожня</div>}
        {queue && queue.length > 0 && (
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-slate-500">
                <th className="py-1 pr-2">#</th>
                <th className="py-1 pr-2">Вид</th>
                <th className="py-1 pr-2">Стан</th>
                <th className="py-1 pr-2">Останнє</th>
                <th className="py-1 pr-2">Джерела</th>
                <th className="py-1 pr-2">Прапорці</th>
                <th className="py-1 pr-2">Кандидати</th>
              </tr>
            </thead>
            <tbody>
              {queue.filter((i) => (!stateFilter || i.state === stateFilter) && (!kindFilter || i.kind === kindFilter)).map((i) => (
                <tr key={i.incidentId} className="border-t border-slate-200 dark:border-slate-700" data-testid={`review-${i.incidentId}`}>
                  <td className="py-1 pr-2">
                    <button className="underline" onClick={() => void open(i.incidentId)}>
                      {i.incidentId}
                    </button>
                  </td>
                  <td className="py-1 pr-2 font-mono">{i.kind}</td>
                  <td className="py-1 pr-2">{stateLabel[i.state] ?? i.state}</td>
                  <td className="py-1 pr-2 font-mono">{i.lastReportedAt}</td>
                  <td className="py-1 pr-2">{i.sourceCount}</td>
                  <td className="py-1 pr-2">
                    {i.flags.map((f) => (
                      <span key={f.observationId} className="mr-1 rounded bg-slate-100 px-1 dark:bg-slate-800">
                        {f.relation}
                        {f.ambiguous.length > 0 && ` ambiguous ${f.ambiguous.join('/')}`}
                        {f.near.length > 0 && ` near ${f.near.join('/')}`}
                      </span>
                    ))}
                  </td>
                  <td className="py-1 pr-2">
                    {i.candidates.map((c) => (
                      <span key={c.incidentId} className={`mr-1 ${c.mergeable ? '' : 'line-through text-slate-400'}`} title={c.mergeable ? 'merge можливий' : `не зливається: ${c.state}${c.sameKind ? '' : ', інший вид'}`}>
                        #{c.incidentId} {stateLabel[c.state] ?? c.state}
                      </span>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>

      {selected && (
        <Section
          title={`Інцидент #${selected.incidentId}`}
          badge={
            <span className="flex items-center gap-2 text-xs">
              <Badge ok={selected.state === 'confirmed' ? true : selected.state === 'retracted' ? false : null} text={stateLabel[selected.state] ?? selected.state} />
              <span>rev {selected.revision}</span>
              {selected.suppressed && <Badge ok={false} text="suppressed" />}
              <button className="underline" onClick={() => setSelected(null)}>
                закрити
              </button>
            </span>
          }
        >
          <div className="text-xs">
            <span className="font-mono">{selected.kind}</span> · подія {selected.eventAt} · останнє {selected.lastReportedAt} · джерела {selected.sourceCount}
            {selected.accuracyKm !== undefined && ` · ±${Math.round(selected.accuracyKm)} км`}
            {selected.mergedIntoIncidentId !== undefined && ` · злито в #${selected.mergedIntoIncidentId}`}
          </div>
          <div className="flex flex-wrap gap-1 text-xs">
            {(['confirm', 'resolve', 'retract', 'suppress', 'unsuppress'] as const).map((a) => (
              <button key={a} className="rounded bg-slate-100 px-2 py-0.5 hover:bg-slate-200 disabled:opacity-50 dark:bg-slate-800 dark:hover:bg-slate-700" disabled={busy || !ready} onClick={() => void run(`${a} #${selected.incidentId}`, () => adminIncidents.command(selected.incidentId, a, actor, reason), selected.incidentId)}>
                {a}
              </button>
            ))}
            <label className="ml-2 flex items-center gap-1">
              merge → #
              <input aria-label="Target incident id" className="w-16 rounded border px-1 dark:bg-slate-800" value={mergeTarget} onChange={(e) => setMergeTarget(e.target.value)} placeholder="id" />
              <button
                className="rounded bg-slate-100 px-2 py-0.5 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700"
                disabled={busy}
                onClick={() => {
                  const targetId = Number(mergeTarget)
                  if (!targetId) return
                  void adminIncidents.mergePreview(selected.incidentId, targetId).then(setPreview).catch((e) => setMessage(`preview: ${e instanceof Error ? e.message : String(e)}`))
                }}
              >
                preview
              </button>
            </label>
            {splitPick.size > 0 && (
              <button className="rounded bg-slate-100 px-2 py-0.5 hover:bg-slate-200 disabled:opacity-50 dark:bg-slate-800 dark:hover:bg-slate-700" disabled={busy || !ready} onClick={() => void run(`split #${selected.incidentId}`, () => adminIncidents.split(selected.incidentId, [...splitPick], actor, reason), selected.incidentId)}>
                split ({splitPick.size})
              </button>
            )}
          </div>
          {preview && (
            <div className="rounded border border-slate-200 p-2 text-xs dark:border-slate-700" data-testid="merge-preview">
              {preview.allowed ? (
                <>
                  <div>
                    Злиття #{preview.sourceId} → #{preview.targetId}: перенесеться {preview.movedObservationIds.length} observation(s); після: джерела {preview.sourceCountAfter}, стан {stateLabel[preview.stateAfter] ?? preview.stateAfter} (не змінюється), локація з {preview.locationFrom === 'source' ? 'джерела' : 'цілі'}
                    {preview.accuracyKmAfter !== undefined && ` (±${Math.round(preview.accuracyKmAfter)} км)`}, впевненість {preview.confidenceAfter}; target rev {preview.targetRevision}
                  </div>
                  <button className="mt-1 rounded bg-slate-800 px-2 py-0.5 text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" disabled={busy || !ready} onClick={() => void run(`merge #${preview.sourceId} → #${preview.targetId}`, async () => {
                    const fresh = await adminIncidents.mergePreview(preview.sourceId, preview.targetId)
                    if (fresh.targetRevision !== preview.targetRevision || fresh.sourceRevision !== preview.sourceRevision) throw new Error('інцидент змінився після preview — оновіть preview')
                    return adminIncidents.merge(preview.sourceId, preview.targetId, actor, reason)
                  }, preview.targetId)}>
                    Підтвердити злиття
                  </button>
                </>
              ) : (
                <div className="text-red-600">Злиття неможливе: {preview.refusal}</div>
              )}
            </div>
          )}
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-slate-500">
                <th className="py-1 pr-2">split</th>
                <th className="py-1 pr-2">relation</th>
                <th className="py-1 pr-2">score</th>
                <th className="py-1 pr-2">джерело</th>
                <th className="py-1 pr-2">час</th>
                <th className="py-1 pr-2">текст</th>
              </tr>
            </thead>
            <tbody>
              {selected.observations.map((o) => (
                <tr key={o.observationId} className="border-t border-slate-200 align-top dark:border-slate-700">
                  <td className="py-1 pr-2">
                    <input type="checkbox" aria-label={`split ${o.observationId}`} checked={splitPick.has(o.observationId)} onChange={(e) => setSplitPick((s) => { const n = new Set(s); if (e.target.checked) n.add(o.observationId); else n.delete(o.observationId); return n })} />
                  </td>
                  <td className="py-1 pr-2">{o.relation}</td>
                  <td className="py-1 pr-2">{o.score.toFixed(2)}</td>
                  <td className="py-1 pr-2">{o.sourceCode ?? o.sourceId}</td>
                  <td className="py-1 pr-2 font-mono">{o.effectiveAt}</td>
                  <td className="py-1 pr-2">
                    {o.segmentText ?? o.rawText ?? '—'}
                    {safeHref(o.rawUrl) && (
                      <a className="ml-1 underline" href={o.rawUrl} target="_blank" rel="noreferrer">
                        оригінал
                      </a>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <ol className="text-xs text-slate-600 dark:text-slate-300" aria-label="Ревізії">
            {selected.revisions.map((r) => (
              <li key={r.revision}>
                #{r.revision} {r.change} · {r.recordedAt} · {r.actor}
                {r.reason && ` · ${r.reason}`}
              </li>
            ))}
          </ol>
        </Section>
      )}
    </>
  )
}
