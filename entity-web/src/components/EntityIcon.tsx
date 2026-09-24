import { entityIconSvg } from '../entities/presentation'

/** A decorative image next to a text label; data-URL images cannot execute SVG scripts in the document. */
export default function EntityIcon({ entity, className = 'h-6 w-6 shrink-0', svg, values }: { values?: Record<string, unknown>; entity: string; className?: string; svg?: string }) {
  const source = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg || entityIconSvg(entity, values))}`
  return <img key={source} src={source} alt="" aria-hidden="true" className={className} onError={event => {
    event.currentTarget.onerror = null
    const fallback = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(entityIconSvg(entity, values))}`
    if (event.currentTarget.src !== fallback) event.currentTarget.src = fallback
  }} />
}
