import { useCallback, useEffect, useState } from 'react'
import { parseStatsHash, statsHash, type Period, type StatsRoute, type Tab } from './period'

/**
 * The tab and the period come from the hash (`#/analytics?tab=alerts&p=7d`) and every change goes back into it, so a
 * view can be linked and the browser's back button walks through tabs and periods.
 */
export function useStatsRoute() {
  const [route, setRoute] = useState<StatsRoute>(() => parseStatsHash(window.location.hash))

  useEffect(() => {
    const onHash = () => {
      if (window.location.hash.startsWith('#/analytics') || window.location.hash.startsWith('#/stats')) setRoute(parseStatsHash(window.location.hash))
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const go = useCallback((next: StatsRoute) => {
    const hash = statsHash(next)
    if (window.location.hash === hash) {
      setRoute(next)
    } else {
      window.location.hash = hash
    }
  }, [])
  const setTab = useCallback((tab: Tab) => go({ ...route, tab }), [go, route])
  const setPeriod = useCallback((period: Period) => go({ ...route, period }), [go, route])
  return { route, setTab, setPeriod }
}
