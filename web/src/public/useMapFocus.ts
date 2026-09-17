import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { PublicMapLocatorDto } from '../api/types'
import { type MapFocus, type MapSelection } from './mapLink'

export interface MapFocusResolution {
  loading: boolean
  focus: MapFocus | null
  error: string | null
}

/** Resolves a deep-link independently of the transient map snapshot. */
export function useMapFocus(selection: MapSelection | null, dataset: string | undefined, historyAt?: string): MapFocusResolution {
  const [state, setState] = useState<MapFocusResolution>({ loading: false, focus: null, error: null })
  useEffect(() => {
    if (!selection) { setState({ loading: false, focus: null, error: null }); return }
    const controller = new AbortController()
    let current = true
    setState({ loading: true, focus: null, error: null })
    if (selection.kind === 'message') {
      api.publicMessage(selection.id, dataset ?? 'live', controller.signal).then((message) => {
        const locators = message.results.items.filter((item) => item.mapAvailable).map((item) => item.map)
        const omitted = Math.max(0, message.results.totalCount - message.results.items.length)
        const unavailable = locators.length === 0
          ? 'У цьому повідомленні немає результатів із підтвердженою геометрією.'
          : omitted > 0 ? `Показано перші ${message.results.items.length} результатів; для решти оберіть конкретний результат у повідомленні.` : undefined
        if (current) setState({ loading: false, focus: { selection, locators, label: `Повідомлення ${selection.id}`, unavailable }, error: null })
      }).catch((error: Error) => { if (current && error.name !== 'AbortError') setState({ loading: false, focus: null, error: error.message }) })
    } else {
      api.publicEntity(selection.kind, selection.id, dataset ?? 'live', controller.signal, historyAt).then((details) => {
        const locator: PublicMapLocatorDto = details.entity.map
        if (current) setState({ loading: false, focus: { selection, locators: details.entity.mapAvailable ? [locator] : [], label: details.entity.title, unavailable: details.entity.mapAvailable ? undefined : details.entity.map.unavailableReason ?? 'Для цієї сутності немає підтвердженої геометрії.' }, error: null })
      }).catch((error: Error) => { if (current && error.name !== 'AbortError') setState({ loading: false, focus: null, error: error.message }) })
    }
    return () => { current = false; controller.abort() }
  }, [dataset, historyAt, selection?.id, selection?.kind])
  return state
}
