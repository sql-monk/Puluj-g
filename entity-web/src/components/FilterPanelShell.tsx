import type { ReactNode } from 'react'

export default function FilterPanelShell({ open, onClose, title, subtitle, children }: { open: boolean; onClose: () => void; title: string; subtitle?: ReactNode; children: ReactNode }) {
  return <aside aria-label={title} data-section-panel={open ? 'open' : 'closed'} data-map-occlusion="true" inert={!open} aria-hidden={!open}
    className={`pointer-events-auto absolute bottom-0 z-40 flex max-h-[75dvh] w-full flex-col overflow-hidden rounded-t-2xl border border-slate-200 bg-white text-slate-900 shadow-xl md:bottom-auto md:left-3 md:top-14 md:max-h-[calc(100dvh-5rem)] md:w-80 md:rounded-2xl dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 ${open ? '' : 'pointer-events-none translate-y-full md:-translate-x-[120%] md:translate-y-0'}`}>
    <div className="flex shrink-0 items-start justify-between gap-2 border-b border-slate-200 px-4 py-3 dark:border-slate-700">
      <div className="min-w-0"><h2 className="font-semibold">{title}</h2>{subtitle && <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">{subtitle}</div>}</div>
      <button type="button" aria-label="Згорнути панель" onClick={onClose} className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-xl hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-blue-500 dark:hover:bg-slate-800">×</button>
    </div>
    <div className="min-h-0 overflow-y-auto overscroll-contain p-4">{children}</div>
  </aside>
}
