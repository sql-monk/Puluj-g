import { useEffect, useRef, useState, type RefObject } from 'react'
import { isMapRoute, publicHash, type PublicRoute, type PublicSection } from '../public/routes'
import { THEMES, useStore, type Theme } from '../store/useStore'

interface Props {
  route: PublicRoute
  rememberedRoutes: Partial<Record<PublicSection, PublicRoute>>
  panelOpen: boolean
  onTogglePanel: () => void
  panelButtonRef: RefObject<HTMLButtonElement | null>
}

/** Public navigation is hash links, so browser history remains the source of truth. */
export default function TopBar({ route, rememberedRoutes, panelOpen, onTogglePanel, panelButtonRef }: Props) {
  const connection = useStore((s) => s.connection)
  const mode = useStore((s) => s.mode)
  const at = useStore((s) => s.at)
  const theme = useStore((s) => s.theme)
  const setTheme = useStore((s) => s.setTheme)
  const error = useStore((s) => s.error)
  const [mapOpen, setMapOpen] = useState(false)
  const mapButton = useRef<HTMLButtonElement>(null)
  const liveLink = useRef<HTMLAnchorElement>(null)
  const historyLink = useRef<HTMLAnchorElement>(null)
  const map = isMapRoute(route)

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || !mapOpen) return
      event.preventDefault()
      setMapOpen(false)
      mapButton.current?.focus()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [mapOpen])

  const dot = connection === 'connected' ? 'bg-emerald-500' : connection === 'reconnecting' ? 'animate-pulse bg-amber-500' : 'bg-red-500'
  const status = connection === 'connected' ? 'онлайн' : connection === 'reconnecting' ? 'перепідключення…' : 'офлайн'
  const routeFor = (section: PublicSection): PublicRoute => {
    const remembered = rememberedRoutes[section]
    return { section, mapMode: section === 'map' ? remembered?.mapMode ?? 'live' : undefined, preset: section === 'map' ? remembered?.preset : undefined, query: new URLSearchParams(remembered?.query), detail: undefined }
  }
  const mapBase = map ? route : routeFor('map')
  const focusMapItem = (which: 'live' | 'history') => window.setTimeout(() => (which === 'live' ? liveLink : historyLink).current?.focus(), 0)
  const nav = (href: string, label: string, active: boolean) => (
    <a href={href} className={`whitespace-nowrap rounded px-2 py-1 hover:bg-slate-200 dark:hover:bg-slate-700 ${active ? 'bg-blue-600 text-white hover:bg-blue-700' : ''}`} aria-current={active ? 'page' : undefined} onClick={() => setMapOpen(false)}>
      {label}
    </a>
  )

  return (
    <header className="pointer-events-auto absolute inset-x-0 top-0 z-30 flex min-h-12 flex-wrap items-center gap-1.5 bg-white/90 px-2 py-2 text-sm shadow backdrop-blur dark:bg-slate-900/90 dark:text-slate-100">
      <button ref={panelButtonRef} className={`rounded px-2 py-1 hover:bg-slate-200 dark:hover:bg-slate-700 ${panelOpen ? 'bg-slate-200 dark:bg-slate-700' : ''}`} onClick={onTogglePanel} aria-label="Панель фільтрів і налаштувань" aria-expanded={panelOpen} title={panelOpen ? 'Сховати панель' : 'Показати панель'}>☰</button>
      <span className="font-semibold tracking-wide">Puluj</span>
      <nav className="order-3 flex w-full items-center gap-1 overflow-x-auto text-xs sm:order-none sm:w-auto sm:text-sm" aria-label="Основна навігація">
        <span className="relative">
          <button ref={mapButton} className={`rounded px-2 py-1 hover:bg-slate-200 dark:hover:bg-slate-700 ${map ? 'bg-blue-600 text-white hover:bg-blue-700' : ''}`} aria-current={map ? 'page' : undefined} aria-haspopup="menu" aria-expanded={mapOpen} onClick={() => setMapOpen((open) => !open)} onKeyDown={(event) => { if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); setMapOpen(true); focusMapItem(event.key === 'ArrowDown' ? 'live' : 'history') } }}>Мапа <span aria-hidden="true">▾</span></button>
          {mapOpen && <span className="absolute left-0 top-full z-40 mt-1 flex min-w-36 flex-col rounded-md bg-white p-1 shadow-lg ring-1 ring-slate-200 dark:bg-slate-800 dark:ring-slate-700" role="menu" aria-label="Режим мапи">
            <a ref={liveLink} role="menuitem" href={publicHash({ ...mapBase, mapMode: 'live' })} className="rounded px-2 py-1.5 hover:bg-slate-100 dark:hover:bg-slate-700" onClick={() => setMapOpen(false)} onKeyDown={(event) => { if (event.key === 'ArrowDown') { event.preventDefault(); historyLink.current?.focus() } else if (event.key === 'ArrowUp') { event.preventDefault(); historyLink.current?.focus() } }}>Онлайн</a>
            <a ref={historyLink} role="menuitem" href={publicHash({ ...mapBase, mapMode: 'history' })} className="rounded px-2 py-1.5 hover:bg-slate-100 dark:hover:bg-slate-700" onClick={() => setMapOpen(false)} onKeyDown={(event) => { if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); liveLink.current?.focus() } }}>Історія</a>
          </span>}
        </span>
        {nav(publicHash(routeFor('analytics')), 'Аналітика', route.section === 'analytics')}
        {nav(publicHash(routeFor('entities')), 'Цілі і події', route.section === 'entities')}
        {nav(publicHash(routeFor('messages')), 'Повідомлення', route.section === 'messages')}
      </nav>
      <span className="ml-auto flex items-center gap-1.5">
        {map && mode === 'history' && at && <span className="rounded bg-indigo-600 px-2 py-0.5 text-xs font-medium text-white">історія · {at.toLocaleString('uk-UA', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}</span>}
        {map && <><span className={`inline-block h-2.5 w-2.5 rounded-full ${dot}`} title={status} /><span className="hidden text-xs text-slate-500 sm:inline dark:text-slate-400">{status}</span></>}
        {error && <span className="max-w-44 truncate rounded bg-red-600 px-2 py-0.5 text-xs text-white" title={error}>{error}</span>}
        <label className="flex items-center gap-1 rounded px-1 py-1 hover:bg-slate-200 dark:hover:bg-slate-700" title="Кольорова тема"><span aria-hidden="true">◐</span><select className="max-w-24 bg-transparent text-xs outline-none" value={theme} onChange={(event) => setTheme(event.target.value as Theme)} aria-label="Кольорова тема">{THEMES.map((item) => <option key={item.id} value={item.id} className="bg-white text-slate-900 dark:bg-slate-900 dark:text-slate-100">{item.label}</option>)}</select></label>
      </span>
    </header>
  )
}
