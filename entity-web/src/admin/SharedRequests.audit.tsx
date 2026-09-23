import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import { usePaged, usePolled } from './shared'

type Page = { items: string[]; next?: string | null; total?: number }
type Pending = { filter: string; cursor?: string; resolve: (value: Page | string) => void; reject: (error: Error) => void }
const pending: Pending[] = []
Object.assign(window, { pending })
function deferred<T extends Page | string>(filter: string, cursor?: string): Promise<T> {
  return new Promise<T>((resolve, reject) => pending.push({ filter, cursor, resolve: value => resolve(value as T), reject }))
}

function Polled({ filter }: { filter: string }) {
  const result = usePolled(() => deferred<string>(filter), 60_000, [filter])
  return <><output aria-label="state">{JSON.stringify(result)}</output><button onClick={() => { void result.reload(); void result.reload() }}>Overlap</button></>
}
function Paged({ filter }: { filter: string }) {
  const result = usePaged<string, string>(cursor => deferred<Page>(filter, cursor), [filter])
  return <><output aria-label="state">{JSON.stringify(result)}</output>
    <button onClick={() => { result.more(); result.more() }}>More twice</button>
    <button onClick={result.refresh}>Refresh</button>
  </>
}
function Harness() {
  const [filter, setFilter] = useState('A')
  const [mounted, setMounted] = useState(true)
  return <>
    <button onClick={() => setFilter('A')}>Filter A</button><button onClick={() => setFilter('B')}>Filter B</button>
    <button onClick={() => setMounted(!mounted)}>Toggle mount</button>
    {mounted && (location.search.includes('paged') ? <Paged filter={filter} /> : <Polled filter={filter} />)}
  </>
}
createRoot(document.getElementById('root')!).render(<Harness />)
