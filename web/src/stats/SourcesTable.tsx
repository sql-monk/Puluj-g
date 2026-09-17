import type { StatsFilterMetaDto, StatsPeriodDto, StatsSourceDto } from '../api/types'
import type { DataQuery } from '../public/query'
import { canSourceMessagesDrillDown, sourceMessagesHref } from './drilldown'
import Sparkline from './charts/Sparkline'
import { MUTED } from './palette'
import { lagText, num, pct } from './period'

/** The sources of the period, busiest first: volume, the trend, how much was processed and how much of that carried a fact, the lag. */
export default function SourcesTable({ sources, period, filter, filters }: { sources: StatsSourceDto[]; period: StatsPeriodDto; filter: DataQuery; filters: StatsFilterMetaDto }) {
  const drillDown = canSourceMessagesDrillDown(filters)
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs tabular-nums">
        <thead className="text-left text-slate-500 dark:text-slate-400">
          <tr>
            <th className="py-1 pr-2 font-medium">Джерело</th>
            <th className="py-1 pr-2 text-right font-medium">Повідомлень</th>
            <th className="py-1 pr-2 font-medium">Динаміка</th>
            <th className="py-1 pr-2 text-right font-medium">Оброблено</th>
            <th className="py-1 pr-2 text-right font-medium">З фактами</th>
            <th className="py-1 pr-2 text-right font-medium">Фактів</th>
            <th className="py-1 pr-2 text-right font-medium" title="Медіана затримки між публікацією і отриманням">
              Затримка
            </th>
          </tr>
        </thead>
        <tbody>
          {sources.map((s) => (
            <tr key={s.id} className="border-t border-slate-100 dark:border-slate-800">
              <td className="py-1 pr-2">
                {drillDown ? <a className="font-medium text-blue-700 hover:underline dark:text-blue-300" href={sourceMessagesHref(s.id, period, filter)} title="Відкрити рівно ці збережені ревізії повідомлень за періодом">{s.name}</a> : <span className="font-medium">{s.name}</span>} <span className="text-slate-400">{s.code}</span>
              </td>
              <td className="py-1 pr-2 text-right">{num(s.messages)}</td>
              <td className="py-1 pr-2">
                <Sparkline values={s.series} color={MUTED} />
              </td>
              <td className="py-1 pr-2 text-right">
                {pct(s.processed, s.messages)} <span className="text-slate-400">({num(s.processed)})</span>
              </td>
              <td className="py-1 pr-2 text-right">
                {pct(s.withTargets, s.processed)} <span className="text-slate-400">({num(s.withTargets)})</span>
              </td>
              <td className="py-1 pr-2 text-right">{num(s.targets)}</td>
              <td className="py-1 pr-2 text-right">{lagText(s.medianLagSeconds)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">{drillDown ? 'Назва джерела відкриває каталог з тим самим джерелом і UTC-інтервалом за часом публікації.' : 'За поточного фільтра немає точного каталожного еквівалента, тому джерела лишаються у таблиці.'} Для фактів, треків і тривог точного каталожного еквівалента також немає — їхні повні значення доступні у таблицях.</p>
    </div>
  )
}
