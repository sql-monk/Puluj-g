import { POLL_OPTIONS, type PollSeconds } from './useEntityPolling'

export default function EntityRefreshControl({ seconds, setSeconds, lastSuccess, error, loading, truncated, refresh }: { seconds: PollSeconds; setSeconds: (value: PollSeconds) => void; lastSuccess?: string; error?: string; loading: boolean; truncated?: boolean; refresh: () => Promise<void> }) {
  return <div className="pointer-events-auto absolute bottom-3 right-3 z-20 flex max-w-sm items-center gap-2 rounded-lg bg-white/95 px-3 py-2 text-xs shadow dark:bg-slate-900/95 dark:text-slate-100" role={error ? 'alert' : 'status'}>
    <label>Сутності кожні <select aria-label="Інтервал оновлення сутностей" className="rounded border border-slate-300 bg-transparent px-1 py-0.5 dark:border-slate-600" value={seconds} onChange={(event) => setSeconds(Number(event.target.value) as PollSeconds)}>{POLL_OPTIONS.map((value) => <option key={value} value={value}>{value} с</option>)}</select></label>
    <button className="rounded bg-slate-200 px-2 py-1 disabled:opacity-50 dark:bg-slate-700" disabled={loading} onClick={() => void refresh()}>{loading ? 'Оновлення…' : 'Оновити'}</button>
    <span title={lastSuccess}>{error ? `Помилка: ${error}` : truncated ? 'Показано обмежений зріз; відкрийте каталог для повного списку' : lastSuccess ? `Оновлено ${new Date(lastSuccess).toLocaleTimeString('uk-UA')}` : 'Ще не оновлено'}</span>
  </div>
}
