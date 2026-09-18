import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '../index.css'
import AdminApp from './AdminApp'
import { themeIsDark, type Theme } from '../store/useStore'

// The admin panel has no theme picker of its own: it takes the theme saved under the map app's key when the two
// happen to share an origin (dev proxy), else the system preference.
function applyTheme() {
  let theme = ''
  try {
    theme = JSON.parse(localStorage.getItem('puluj.theme') ?? '""') as string
  } catch {
    /* no saved theme */
  }
  if (!theme) theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  const dark = themeIsDark(theme as Theme)
  document.documentElement.classList.toggle('dark', dark)
  document.documentElement.dataset.theme = theme
}
applyTheme()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AdminApp />
  </StrictMode>,
)
