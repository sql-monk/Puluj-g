import { useEffect, useState } from 'react'
import { themeIsDark, useStore } from '../store/useStore'
import LiveLine from './LiveLine'
import { chartVars } from './palette'
import { PRESETS, TABS, presetPeriod, rangeText, toLocalInput, type Period, type Preset, type Tab } from './period'
import { parseKyivInput } from '../public/kyivTime'
import AlertsTab from './tabs/AlertsTab'
import RecognitionTab from './tabs/RecognitionTab'
import SourcesTab from './tabs/SourcesTab'
import TargetsTab from './tabs/TargetsTab'
import { useStatsRoute } from './useStatsRoute'

/**
 * The statistics page: four tabs, each answering one question with its own payload, one period for all of them,
 * and a live line from the store. Sits over the map (which stays mounted underneath) and scrolls on its own; the
 * chart colours of the current theme are CSS variables on this root.
 */
export default function StatsPage({ filterUnavailable = false }: { filterUnavailable?: boolean }) {
  const { route, setTab, setPeriod } = useStatsRoute()
  const dark = themeIsDark(useStore((s) => s.theme))
  return (
    <div className="pointer-events-auto absolute inset-0 z-10 overflow-y-auto bg-slate-100 pt-12 text-slate-900 md:pt-14 dark:bg-slate-950 dark:text-slate-100" style={chartVars(dark)}>
      <div className="mx-auto flex max-w-6xl flex-col gap-3 px-3 pb-8">
        <div className="sticky top-0 z-20 -mx-3 flex flex-col gap-1.5 bg-slate-100/95 px-3 py-2 backdrop-blur dark:bg-slate-950/95">
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <h2 className="mr-1 font-semibold">Статистика</h2>
            <Tabs tab={route.tab} onChange={setTab} />
            <PeriodBar period={route.period} onChange={setPeriod} />
          </div>
          <LiveLine />
        </div>
        {filterUnavailable ? <div role="alert" className="rounded-xl bg-amber-100 p-4 text-amber-950 dark:bg-amber-950 dark:text-amber-100">Обрана метрика ще не підтримує активні URL filters. Дані не завантажено, щоб не показати unfiltered chart під активними chips.</div> : <TabPanel tab={route.tab} period={route.period} />}
      </div>
    </div>
  )
}

function Tabs({ tab, onChange }: { tab: Tab; onChange: (t: Tab) => void }) {
  return (
    <span className="inline-flex overflow-hidden rounded-md border border-slate-300 text-xs dark:border-slate-600" role="tablist" aria-label="Розділ">
      {TABS.map((t) => (
        <button key={t.id} type="button" role="tab" id={`stats-tab-${t.id}`} aria-selected={tab === t.id} aria-controls="stats-panel" title={t.question} className={`px-2.5 py-1 ${tab === t.id ? 'bg-blue-600 text-white' : 'hover:bg-slate-200 dark:hover:bg-slate-700'}`} onClick={() => onChange(t.id)}>
          {t.label}
        </button>
      ))}
    </span>
  )
}

function TabPanel({ tab, period }: { tab: Tab; period: Period }) {
  return (
    <div id="stats-panel" role="tabpanel" aria-labelledby={`stats-tab-${tab}`} className="flex flex-col gap-3">
      {tab === 'targets' && <TargetsTab period={period} />}
      {tab === 'alerts' && <AlertsTab period={period} />}
      {tab === 'sources' && <SourcesTab period={period} />}
      {tab === 'recognition' && <RecognitionTab period={period} />}
    </div>
  )
}

/** Presets and, for a custom range, two local date-time inputs applied with a button (not on every keystroke). */
function PeriodBar({ period, onChange }: { period: Period; onChange: (p: Period) => void }) {
  const [from, setFrom] = useState(toLocalInput(period.from))
  const [to, setTo] = useState(toLocalInput(period.to))
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    setFrom(toLocalInput(period.from))
    setTo(toLocalInput(period.to))
  }, [period])
  const pick = (id: Preset) => {
    if (id === 'custom') onChange({ preset: 'custom', from: period.from, to: period.to })
    else onChange(presetPeriod(id))
  }
  const apply = () => {
    const f = parseKyivInput(from)
    const t = parseKyivInput(to)
    if (!f || !t || t <= f) {
      setError('Вкажіть коректний інтервал Europe/Kyiv; неіснуюча DST-година не приймається.')
      return
    }
    setError(null)
    onChange({ preset: 'custom', from: f, to: t })
  }
  return (
    <>
      <span className="inline-flex overflow-hidden rounded-md border border-slate-300 text-xs dark:border-slate-600" role="group" aria-label="Період">
        {PRESETS.map((p) => (
          <button key={p.id} type="button" aria-pressed={period.preset === p.id} className={`px-2.5 py-1 ${period.preset === p.id ? 'bg-blue-600 text-white' : 'hover:bg-slate-200 dark:hover:bg-slate-700'}`} onClick={() => pick(p.id)}>
            {p.label}
          </button>
        ))}
      </span>
      {period.preset === 'custom' && (
        <span className="flex flex-wrap items-center gap-1 text-xs">
          <input type="datetime-local" className="rounded border border-slate-300 bg-white px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="Початок" />
          <span>—</span>
          <input type="datetime-local" className="rounded border border-slate-300 bg-white px-1.5 py-0.5 dark:border-slate-600 dark:bg-slate-800" value={to} onChange={(e) => setTo(e.target.value)} aria-label="Кінець" />
          <button type="button" className="rounded bg-blue-600 px-2 py-0.5 text-white hover:bg-blue-700" onClick={apply}>
            Показати
          </button>
          {error && <span role="alert" className="text-red-700 dark:text-red-300">{error}</span>}
        </span>
      )}
      <span className="ml-auto text-xs text-slate-500 dark:text-slate-400">{rangeText(period)}</span>
    </>
  )
}
