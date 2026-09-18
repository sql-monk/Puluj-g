import { useCallback, useEffect, useState } from 'react'
import { AdminError } from '../api/admin'
import { adminCatalog, type AdminCatalogDto, type AdminKindDto, type KindAuditDto, type KindUpdate } from '../api/adminCatalog'
import { Badge, Section } from '../components/settings/fields'

const shapeLabel: Record<string, string> = { circle: 'Коло', triangle: 'Трикутник', diamond: 'Ромб', square: 'Квадрат', cross: 'Хрест', shield: 'Щит', hex: 'Шестикутник' }
const iconShape: Record<string, string> = { 'air-defence': 'shield', interception: 'shield', alert: 'shield', launch: 'triangle', damage: 'square', evacuation: 'square' }

interface Draft {
  nameUk: string
  mapVisible: boolean
  mapColor: string
  mapIcon: string
  mapLifetime: string
  renderMode: string
  sortOrder: string
  enabled: boolean
  requiresLocationForMap: boolean
}

function draftOf(k: AdminKindDto): Draft {
  return { nameUk: k.nameUk, mapVisible: k.mapVisible, mapColor: k.mapColor ?? '', mapIcon: k.mapIcon ?? 'unknown', mapLifetime: k.mapLifetime ?? '', renderMode: k.renderMode ?? 'marker', sortOrder: String(k.sortOrder), enabled: k.enabled, requiresLocationForMap: k.requiresLocationForMap }
}

/**
 * Catalog editor (plan §8.7, P12, ADR-0008): the presentation of every event kind — name, map colour/icon/lifetime,
 * visibility, render mode, sort order, enabled — with a required actor/reason on every change and the audit history.
 * The policy fields (category and state model) belong to the seed and are shown read-only.
 * Presentation changes reach the map after the API's catalog refresh (up to 10 minutes) and a page reload.
 */
export function CatalogPanel() {
  const [catalog, setCatalog] = useState<AdminCatalogDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [actor, setActor] = useState('')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [audit, setAudit] = useState<{ code: string; rows: KindAuditDto[] } | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setCatalog(await adminCatalog.kinds())
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])
  useEffect(() => {
    const t = window.setTimeout(() => void load(), 0) // the load is a fetch (external system); deferred so the effect itself sets no state
    return () => window.clearTimeout(t)
  }, [load])

  const startEdit = (k: AdminKindDto) => {
    setEditing(k.code)
    setDraft(draftOf(k))
    setMessage(null)
  }

  const save = async (k: AdminKindDto, force = false) => {
    if (!draft) return
    const patch: KindUpdate = { actor, reason, force }
    const original = draftOf(k)
    if (draft.nameUk !== original.nameUk) patch.nameUk = draft.nameUk
    if (draft.mapVisible !== original.mapVisible) patch.mapVisible = draft.mapVisible
    if (draft.mapColor !== original.mapColor) patch.mapColor = draft.mapColor
    if (draft.mapIcon !== original.mapIcon) patch.mapIcon = draft.mapIcon
    if (draft.mapLifetime !== original.mapLifetime) patch.mapLifetime = draft.mapLifetime
    if (draft.renderMode !== original.renderMode) patch.renderMode = draft.renderMode
    if (draft.sortOrder !== original.sortOrder) patch.sortOrder = Number(draft.sortOrder)
    if (draft.enabled !== original.enabled) patch.enabled = draft.enabled
    if (draft.requiresLocationForMap !== original.requiresLocationForMap) patch.requiresLocationForMap = draft.requiresLocationForMap
    setBusy(true)
    try {
      await adminCatalog.update(k.code, patch)
      setMessage(`${k.code}: збережено (на мапі — до 10 хв, після оновлення сторінки)`)
      setEditing(null)
      setDraft(null)
      setReason('')
      await load()
    } catch (e) {
      if (e instanceof AdminError && e.status === 409 && !force && window.confirm(`${e.message}\n\nВимкнути попри це?`)) {
        setBusy(false)
        return save(k, true)
      }
      const problems = e instanceof AdminError && e.body && typeof e.body === 'object' && 'problems' in e.body ? ((e.body as { problems?: string[] }).problems ?? []).join('; ') : ''
      setMessage(`${k.code}: помилка — ${e instanceof Error ? e.message : String(e)}${problems ? ` (${problems})` : ''}`)
    } finally {
      setBusy(false)
    }
  }

  const showAudit = async (code: string) => {
    try {
      setAudit({ code, rows: await adminCatalog.audit(code) })
    } catch (e) {
      setMessage(`${code}: аудит — ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  if (error) return <Section title="Каталог подій">{<div className="text-sm text-red-600">{error}</div>}</Section>
  if (!catalog) return <Section title="Каталог подій">{<div className="text-sm text-slate-500">завантаження…</div>}</Section>

  return (
    <>
      <Section title="Каталог подій" badge={<Badge ok={null} text={`${catalog.kinds.length} видів`} />}>
        <p className="text-xs text-slate-600 dark:text-slate-300">
          Презентація (назва, колір, іконка, час на мапі, видимість, порядок, увімкнено) — редагується тут і далі не перезаписується seed; категорія й state model — із seed. Кожна зміна потребує <b>actor</b> і <b>reason</b> і лишає запис в аудиті.
        </p>
        <div className="flex flex-wrap gap-2 text-xs">
          <label className="flex items-center gap-1">
            Actor
            <input className="rounded border border-slate-300 px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={actor} onChange={(e) => setActor(e.target.value)} placeholder="хто" aria-label="Actor" />
          </label>
          <label className="flex flex-1 items-center gap-1">
            Reason
            <input className="w-full rounded border border-slate-300 px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="чому" aria-label="Reason" />
          </label>
        </div>
        {message && <div className="text-xs text-slate-700 dark:text-slate-200" role="status">{message}</div>}
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-slate-500">
                <th className="py-1 pr-2">Код</th>
                <th className="py-1 pr-2">Назва</th>
                <th className="py-1 pr-2">Категорія</th>
                <th className="py-1 pr-2">Колір / форма</th>
                <th className="py-1 pr-2">Іконка</th>
                <th className="py-1 pr-2">Час на мапі</th>
                <th className="py-1 pr-2">Режим</th>
                <th className="py-1 pr-2">Видимий</th>
                <th className="py-1 pr-2">Увімк.</th>
                <th className="py-1 pr-2">#</th>
                <th className="py-1 pr-2"></th>
              </tr>
            </thead>
            <tbody>
              {catalog.kinds.map((k) => {
                const isEditing = editing === k.code && draft
                return (
                  <tr key={k.code} className="border-t border-slate-200 align-top dark:border-slate-700" data-testid={`kind-${k.code}`}>
                    <td className="py-1 pr-2 font-mono">
                      {k.code}
                      {k.presentationOverriddenAt && <span className="ml-1 rounded bg-amber-100 px-1 text-[10px] text-amber-800 dark:bg-amber-900 dark:text-amber-100" title={`адмін-презентація з ${k.presentationOverriddenAt}`}>admin</span>}
                      {k.legacyEventType && <span className="ml-1 text-[10px] text-slate-500">legacy {k.legacyEventType}</span>}
                    </td>
                    <td className="py-1 pr-2">{isEditing ? <input aria-label="Назва" className="w-40 rounded border px-1 dark:bg-slate-800" value={draft.nameUk} onChange={(e) => setDraft({ ...draft, nameUk: e.target.value })} /> : k.nameUk}</td>
                    <td className="py-1 pr-2">
                      {k.category}
                    </td>
                    <td className="py-1 pr-2">
                      <span aria-hidden="true" className="mr-1 inline-block h-3 w-3 rounded-sm border border-slate-400 align-middle" style={{ background: (isEditing ? draft.mapColor : k.mapColor) || 'transparent' }} />
                      {isEditing ? <input aria-label="Колір" className="w-20 rounded border px-1 font-mono dark:bg-slate-800" value={draft.mapColor} onChange={(e) => setDraft({ ...draft, mapColor: e.target.value })} placeholder="#rrggbb" /> : <span className="font-mono">{k.mapColor ?? '—'}</span>}
                      <span className="ml-1 text-slate-500">{shapeLabel[iconShape[(isEditing ? draft.mapIcon : k.mapIcon) ?? 'unknown'] ?? 'circle']}</span>
                    </td>
                    <td className="py-1 pr-2">
                      {isEditing ? (
                        <select aria-label="Іконка" className="rounded border px-1 dark:bg-slate-800" value={draft.mapIcon} onChange={(e) => setDraft({ ...draft, mapIcon: e.target.value })}>
                          {(catalog.iconVocabulary.includes(draft.mapIcon) ? catalog.iconVocabulary : [draft.mapIcon, ...catalog.iconVocabulary]).map((i) => (
                            <option key={i} value={i}>
                              {i}
                            </option>
                          ))}
                        </select>
                      ) : (
                        (k.mapIcon ?? '—')
                      )}
                    </td>
                    <td className="py-1 pr-2">{isEditing ? <input aria-label="Час на мапі" className="w-24 rounded border px-1 font-mono dark:bg-slate-800" value={draft.mapLifetime} onChange={(e) => setDraft({ ...draft, mapLifetime: e.target.value })} placeholder="hh:mm:ss" /> : <span className="font-mono">{k.mapLifetime ?? '—'}</span>}</td>
                    <td className="py-1 pr-2">
                      {isEditing ? (
                        <select aria-label="Режим" className="rounded border px-1 dark:bg-slate-800" value={draft.renderMode} onChange={(e) => setDraft({ ...draft, renderMode: e.target.value })}>
                          {catalog.renderModes.map((m) => (
                            <option key={m} value={m}>
                              {m}
                            </option>
                          ))}
                        </select>
                      ) : (
                        (k.renderMode ?? '—')
                      )}
                    </td>
                    <td className="py-1 pr-2">{isEditing ? <input aria-label="Видимий на мапі" type="checkbox" checked={draft.mapVisible} onChange={(e) => setDraft({ ...draft, mapVisible: e.target.checked })} /> : k.mapVisible ? 'так' : 'ні'}</td>
                    <td className="py-1 pr-2">{isEditing ? <input aria-label="Увімкнено" type="checkbox" checked={draft.enabled} onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} /> : k.enabled ? 'так' : 'ні'}</td>
                    <td className="py-1 pr-2">{isEditing ? <input aria-label="Порядок" className="w-14 rounded border px-1 dark:bg-slate-800" value={draft.sortOrder} onChange={(e) => setDraft({ ...draft, sortOrder: e.target.value })} /> : k.sortOrder}</td>
                    <td className="py-1 pr-2 whitespace-nowrap">
                      {isEditing ? (
                        <>
                          <button className="rounded bg-slate-800 px-2 py-0.5 text-white disabled:opacity-50 dark:bg-slate-100 dark:text-slate-900" disabled={busy || !actor.trim() || !reason.trim()} onClick={() => void save(k)} title={!actor.trim() || !reason.trim() ? 'потрібні actor і reason' : undefined}>
                            Зберегти
                          </button>
                          <button className="ml-1 rounded px-2 py-0.5 hover:bg-slate-100 dark:hover:bg-slate-800" onClick={() => setEditing(null)}>
                            Скасувати
                          </button>
                        </>
                      ) : (
                        <>
                          <button className="rounded px-2 py-0.5 hover:bg-slate-100 dark:hover:bg-slate-800" onClick={() => startEdit(k)}>
                            Редагувати
                          </button>
                          <button className="ml-1 rounded px-2 py-0.5 hover:bg-slate-100 dark:hover:bg-slate-800" onClick={() => void showAudit(k.code)}>
                            Аудит
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </Section>
      {audit && (
        <Section title={`Аудит: ${audit.code}`} badge={<button className="text-xs underline" onClick={() => setAudit(null)}>закрити</button>}>
          {audit.rows.length === 0 ? (
            <div className="text-xs text-slate-500">змін не було</div>
          ) : (
            <ul className="space-y-1 text-xs">
              {audit.rows.map((a) => (
                <li key={a.auditId}>
                  <span className="font-mono">{a.at}</span> · {a.action} · {a.actor} · {a.reason}
                  {a.before && a.after && (
                    <span className="ml-1 text-slate-500">
                      {Object.keys(a.after)
                        .filter((f) => JSON.stringify(a.before![f]) !== JSON.stringify(a.after![f]))
                        .map((f) => `${f}: ${String(a.before![f] ?? '—')} → ${String(a.after![f] ?? '—')}`)
                        .join(', ')}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Section>
      )}
    </>
  )
}
