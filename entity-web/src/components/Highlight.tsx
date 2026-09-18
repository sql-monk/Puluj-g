/**
 * A message with the segment a fact was built from marked inside it. Matching is case-insensitive and ignores
 * whitespace differences (the parser normalises text). Folded: only the marked segment with a little context is shown,
 * so the relevant line is visible even when the message is a thirty-line list and the row is collapsed.
 */
export default function Highlight({ text, part, folded = false }: { text: string; part: string; folded?: boolean }) {
  const range = locate(text, part)
  if (!range) return <>{folded ? text : text}</>
  const [from, to] = range
  if (folded) {
    const before = text.slice(Math.max(0, from - 30), from).replace(/\s+/g, ' ')
    const after = text.slice(to, to + 40).replace(/\s+/g, ' ')
    return (
      <>
        {from > 30 ? '…' : ''}
        {before}
        <mark className="rounded bg-amber-200 px-0.5 text-slate-900 dark:bg-amber-500/70 dark:text-slate-950">{text.slice(from, to)}</mark>
        {after}
        {to + 40 < text.length ? '…' : ''}
      </>
    )
  }
  return (
    <>
      {text.slice(0, from)}
      <mark className="rounded bg-amber-200 px-0.5 text-slate-900 dark:bg-amber-500/70 dark:text-slate-950">{text.slice(from, to)}</mark>
      {text.slice(to)}
    </>
  )
}

/** Start/end of `part` inside `text`, tolerant to case and whitespace; null when absent. */
export function locate(text: string, part: string): [number, number] | null {
  const needle = part.toLowerCase().replace(/\s+/g, ' ').trim()
  if (!needle) return null
  // Build the normalised haystack together with a map from normalised index to original index.
  const map: number[] = []
  let hay = ''
  let lastSpace = true
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (/\s/.test(ch)) {
      if (lastSpace) continue
      hay += ' '
      map.push(i)
      lastSpace = true
    } else {
      hay += ch.toLowerCase()
      map.push(i)
      lastSpace = false
    }
  }
  const start = hay.indexOf(needle)
  if (start < 0) return null
  const from = map[start]
  const endIdx = start + needle.length - 1
  const to = map[endIdx] + 1
  return [from, to]
}
