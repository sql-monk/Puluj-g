export function RegionSelectionNote({ name, onClose }: { name: string; onClose: () => void }) {
  return <aside aria-label="Вибраний регіон" className="pointer-events-auto absolute bottom-24 left-3 z-20 w-80 max-w-[calc(100%-1.5rem)] rounded-xl bg-white/95 p-4 text-sm shadow-xl dark:bg-slate-900/95 dark:text-slate-100">
    <button className="float-right rounded px-2" aria-label="Закрити вибраний регіон" onClick={onClose}>×</button>
    <h2 className="font-semibold">{name}</h2>
    <p className="mt-2">Вибрано межі регіону. Це не фільтр записів і не індикатор стану тривоги.</p>
    <p className="mt-2 text-xs text-slate-500 dark:text-slate-300">Карта показує отримані сутності з геометрією за поточними фільтрами. Відсутність позначок не підтверджує відсутність тривоги.</p>
  </aside>
}
