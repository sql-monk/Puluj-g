import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { PlaceDto } from '../api/types'
import { useStore } from '../store/useStore'

interface Props {
  picking: boolean
  onPickingChange: (v: boolean) => void
}

/**
 * Spec §16: the user's point comes from a map click, a place search or the browser Geolocation API
 * (explicit consent via the button). It is stored in localStorage only and never sent to the server.
 */
export default function HomeLocationPicker({ picking, onPickingChange }: Props) {
  const home = useStore((s) => s.home)
  const setHome = useStore((s) => s.setHome)
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<PlaceDto[]>([])
  const [geoError, setGeoError] = useState<string | null>(null)

  useEffect(() => {
    if (query.trim().length < 2) {
      setResults([])
      return
    }
    const handle = setTimeout(() => {
      api
        .searchPlaces(query.trim())
        .then(setResults)
        .catch(() => setResults([]))
    }, 250)
    return () => clearTimeout(handle)
  }, [query])

  const useGeolocation = () => {
    setGeoError(null)
    if (!navigator.geolocation) {
      setGeoError('Геолокація недоступна у цьому браузері')
      return
    }
    navigator.geolocation.getCurrentPosition(
      (pos) => setHome({ lon: pos.coords.longitude, lat: pos.coords.latitude }),
      () => setGeoError('Не вдалося отримати позицію (дозвіл відхилено?)'),
      { enableHighAccuracy: false, timeout: 10000, maximumAge: 300000 },
    )
  }

  return (
    <div className="space-y-2 text-sm">
      <div className="font-medium text-slate-800 dark:text-slate-100">Моя точка</div>
      {home ? (
        <div className="flex items-center justify-between rounded bg-emerald-50 px-2 py-1 text-xs text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200">
          <span>
            {home.lat.toFixed(3)}, {home.lon.toFixed(3)}
          </span>
          <button className="underline" onClick={() => setHome(null)}>
            прибрати
          </button>
        </div>
      ) : (
        <div className="text-xs text-slate-500 dark:text-slate-400">Не задано — ETA не розраховується.</div>
      )}
      <input
        className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm dark:border-slate-600 dark:bg-slate-800"
        placeholder="Пошук населеного пункту…"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      {results.length > 0 && (
        <ul className="max-h-40 overflow-auto rounded border border-slate-200 bg-white text-xs dark:border-slate-700 dark:bg-slate-800">
          {results.map((p) => (
            <li key={p.id}>
              <button
                className="block w-full px-2 py-1 text-left hover:bg-slate-100 dark:hover:bg-slate-700"
                onClick={() => {
                  setHome({ lon: p.lon, lat: p.lat })
                  setQuery('')
                  setResults([])
                }}
              >
                {p.name} <span className="text-slate-400">{p.parentName ?? p.level}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex gap-2">
        <button
          className={`flex-1 rounded border px-2 py-1 text-xs ${picking ? 'border-amber-500 bg-amber-100 dark:bg-amber-900/40' : 'border-slate-300 dark:border-slate-600'}`}
          onClick={() => onPickingChange(!picking)}
        >
          {picking ? 'Клікніть на карті…' : 'Вибрати на карті'}
        </button>
        <button className="flex-1 rounded border border-slate-300 px-2 py-1 text-xs dark:border-slate-600" onClick={useGeolocation}>
          Моя геолокація
        </button>
      </div>
      {geoError && <div className="text-xs text-red-600">{geoError}</div>}
      <div className="text-[11px] text-slate-400">Точка зберігається лише у вашому браузері.</div>
    </div>
  )
}
