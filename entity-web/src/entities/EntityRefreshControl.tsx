import { POLL_OPTIONS, type PollSeconds } from './useEntityPolling'

export default function EntityRefreshControl({ seconds, setSeconds, lastSuccess, error, loading, truncated, refresh, catalogue = false, history = false, snapshotAt }: { seconds: PollSeconds; setSeconds: (value: PollSeconds) => void; lastSuccess?: string; error?: string; loading: boolean; truncated?: boolean; refresh: () => Promise<void>; catalogue?: boolean; history?: boolean; snapshotAt?: string }) {
  return <div className={`pointer-events-auto absolute right-3 z-20 flex max-w-[calc(100%-1.5rem)] flex-wrap items-center gap-2 rounded-lg bg-white/95 px-3 py-2 text-xs shadow dark:bg-slate-900/95 ${history ? 'top-36 sm:top-28' : 'bottom-3'}`} role={error ? 'alert' : 'status'} aria-label="Оновлення даних">
    <label>Кожні <select aria-label="Інтервал оновлення сутностей" className="rounded border border-slate-300 bg-transparent px-1 py-0.5 dark:border-slate-600" value={seconds} onChange={(event) => setSeconds(Number(event.target.value) as PollSeconds)}>{POLL_OPTIONS.map((value) => <option key={value} value={value}>{value} с</option>)}</select></label>
    <button className="rounded bg-slate-200 px-2 py-1 disabled:opacity-50 dark:bg-slate-700" disabled={loading} onClick={() => void refresh()}>{loading ? 'Оновлення…' : 'Оновити'}</button>
    {history && snapshotAt && <span>Кадр {new Date(snapshotAt).toLocaleString('uk-UA', {timeZone:'Europe/Kyiv'})}</span>}
    <span className="max-w-sm break-words" title={lastSuccess}>{error ? 'Не вдалося оновити дані. Спробуйте ще раз.' : loading ? 'Завантаження даних…' : !catalogue && truncated ? 'Обмежений зріз; повний список у каталозі' : lastSuccess ? `Оновлено ${new Date(lastSuccess).toLocaleTimeString('uk-UA', {timeZone:'Europe/Kyiv'})}` : 'Ще не оновлено'}</span>
  </div>
}
