import { StrictMode, useState } from 'react'
import { createRoot } from 'react-dom/client'
import ReplayBar from './ReplayBar'
import { useStore } from '../store/useStore'
import { historyWindow } from '../public/routes'
import '../index.css'

const initial = historyWindow(new URLSearchParams(location.hash.slice(1)))
useStore.getState().setMode('history', initial.at)

function Harness() {
  const [window, setWindow] = useState(initial)
  const [panelOpen, setPanelOpen] = useState(false)
  const [closed, setClosed] = useState(false)
  return <>
    <button onClick={() => setPanelOpen(!panelOpen)}>Фільтри тесту</button>
    <button onClick={() => setWindow({ ...window, at: new Date(window.from.getTime() + 120_000) })}>Інша позиція URL</button>
    {panelOpen && <div data-section-panel="open" onKeyDown={(event) => {
      if (event.key === 'Escape') { event.preventDefault(); setPanelOpen(false) }
    }}><button>Поле фільтрів</button></div>}
    {closed ? <p>Закрито</p> : <ReplayBar
      key={`${window.from.toISOString()}-${window.to.toISOString()}`}
      initialWindow={window}
      keyboardEnabled={!panelOpen}
      onHistoryChange={(next) => {
        const query = new URLSearchParams({ from: next.from.toISOString(), to: next.to.toISOString(), at: next.at.toISOString() })
        if (location.hash !== `#${query}`) history.replaceState(null, '', `#${query}`)
        setWindow(next)
      }}
      onClose={() => setClosed(true)}
    />}
    <output aria-label="Поточний знімок">{useStore((state) => state.at)?.toISOString()}</output>
  </>
}

createRoot(document.getElementById('root')!).render(<StrictMode><Harness /></StrictMode>)
