import type { StatsFilterMetaDto } from '../api/types'

/** Makes a partial metric explicit instead of leaving active URL chips to imply an unfiltered chart. */
export default function FilterMeta({ meta }: { meta: StatsFilterMetaDto }) {
  if (!meta.applied.length && !meta.unavailable.length) return null
  return (
    <div className="rounded-xl border border-slate-300 bg-white px-3 py-2 text-xs text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200">
      {meta.applied.length > 0 && <p><b>Застосовано:</b> {meta.applied.join(', ')}. <span className="text-slate-500">Час: {meta.timeBasis}; популяція: {meta.population}.</span></p>}
      {meta.exclusionReason && <p className="mt-1 text-slate-600 dark:text-slate-300">{meta.exclusionReason}</p>}
      {meta.unavailable.length > 0 && <p role="alert" className="mt-1 text-amber-800 dark:text-amber-300"><b>Не застосовано:</b> {meta.unavailable.map((item) => `${item.key} — ${item.reason}`).join('; ')}</p>}
    </div>
  )
}
