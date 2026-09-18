import type { ReactNode } from 'react'
import type { SettingDto } from '../../api/admin'

export type Draft = Record<string, string | null>

export function findSetting(all: SettingDto[], key: string): SettingDto | undefined {
  return all.find((s) => s.key === key)
}

/** Text / number / password input bound to a settings key. Secrets never show their value; an empty draft means "keep". */
export function Field({
  label,
  setting,
  draft,
  onChange,
  type = 'text',
  placeholder,
  hint,
}: {
  label: string
  setting?: SettingDto
  draft: Draft
  onChange: (key: string, value: string | null) => void
  type?: 'text' | 'password' | 'number'
  placeholder?: string
  hint?: string
}) {
  if (!setting) return null
  const isSecret = setting.isSecret
  const current = draft[setting.key]
  const value = current ?? (isSecret ? '' : (setting.value ?? ''))
  return (
    <label className="block text-sm">
      <span className="mb-0.5 flex items-baseline justify-between">
        <span className="font-medium">{label}</span>
        <span className="text-[11px] text-slate-400">
          {setting.source === 'db' ? 'з налаштувань' : setting.source === 'config' ? 'з конфігурації' : setting.source === 'default' ? 'типово' : 'не задано'}
          {isSecret && setting.hasValue && current !== '' && ' · збережено'}
        </span>
      </span>
      <div className="flex gap-1">
        <input
          className="w-full rounded border border-slate-300 bg-white px-2 py-1 font-mono text-sm dark:border-slate-600 dark:bg-slate-800"
          type={type}
          value={value}
          placeholder={isSecret && setting.hasValue ? '•••••• (залишити як є)' : placeholder}
          onChange={(e) => onChange(setting.key, e.target.value)}
          autoComplete="off"
        />
        {isSecret && setting.hasValue && current !== '' && (
          <button type="button" className="rounded border border-slate-300 px-2 text-xs text-slate-500 dark:border-slate-600" title="Прибрати збережене значення" onClick={() => onChange(setting.key, '')}>
            ✕
          </button>
        )}
      </div>
      {hint && <span className="text-[11px] text-slate-500">{hint}</span>}
      {isSecret && current === '' && <span className="text-[11px] text-red-500">буде видалено при збереженні</span>}
    </label>
  )
}

export function Toggle({ label, setting, draft, onChange, hint }: { label: string; setting?: SettingDto; draft: Draft; onChange: (key: string, value: string | null) => void; hint?: string }) {
  if (!setting) return null
  const raw = draft[setting.key] ?? setting.value ?? 'false'
  const on = raw.toLowerCase() === 'true'
  return (
    <label className="flex items-start gap-2 text-sm">
      <input type="checkbox" className="mt-1" checked={on} onChange={(e) => onChange(setting.key, e.target.checked ? 'true' : 'false')} />
      <span>
        <span className="font-medium">{label}</span>
        {hint && <span className="block text-[11px] text-slate-500">{hint}</span>}
      </span>
    </label>
  )
}

export function Section({ title, badge, children }: { title: string; badge?: ReactNode; children: ReactNode }) {
  return (
    <section className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-slate-700">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold">{title}</h3>
        {badge}
      </div>
      {children}
    </section>
  )
}

export function Badge({ ok, text }: { ok: boolean | null; text: string }) {
  const cls = ok === null ? 'bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200' : ok ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/50 dark:text-emerald-200' : 'bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-200'
  return <span className={`rounded px-2 py-0.5 text-[11px] font-medium ${cls}`}>{text}</span>
}
