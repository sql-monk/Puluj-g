import { StrictMode, useState } from 'react'
import { createRoot } from 'react-dom/client'
import type { EntityItem } from '../api/entityExtractor'
import { useEntitySelection } from './selection'
import { RegionSelectionNote } from './RegionSelectionNote'
import '../index.css'

const original: EntityItem = { entity: 'alert', table: 'ee_alerts', id: '9007199254740993', values: { status: 'active' } }
function Harness() {
  const [items, setItems] = useState([original])
  const [hidden, setHidden] = useState(false)
  const [region, setRegion] = useState(true)
  const { selected, select } = useEntitySelection(hidden ? [] : items)
  return <>
    <button onClick={() => select(original)}>Select</button>
    <button onClick={() => setItems([{ ...original, values: { status: 'closed' } }])}>Update</button>
    <button onClick={() => setItems([])}>Remove</button>
    <button onClick={() => setItems([original])}>Restore</button>
    <button onClick={() => setItems([{ ...original, entity: 'target' }])}>Other kind</button>
    <button onClick={() => setHidden(!hidden)}>Toggle filter</button>
    {selected && <output aria-label="Selected status">{String(selected.values.status)}</output>}
    {region && <RegionSelectionNote name="Київ" onClose={() => setRegion(false)} />}
  </>
}
createRoot(document.getElementById('root')!).render(<StrictMode><Harness /></StrictMode>)
