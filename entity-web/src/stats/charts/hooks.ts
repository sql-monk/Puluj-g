import { useEffect, useRef, useState, type FocusEvent, type KeyboardEvent, type PointerEvent, type RefObject } from 'react'
import { stepCursor } from './geometry'

/** Width of the chart's container, so every SVG is drawn at its real pixel size (no distorted text from a stretched viewBox). */
export function useWidth<T extends HTMLElement>(): [RefObject<T | null>, number] {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(0)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0
      setWidth(Math.floor(w))
    })
    ro.observe(el)
    setWidth(Math.floor(el.getBoundingClientRect().width))
    return () => ro.disconnect()
  }, [])
  return [ref, width]
}

export interface TipLine {
  value: string
  label: string
  color?: string
}

/** One floating readout per chart, positioned inside the chart's container (`data-chart`). */
export interface TipState {
  x: number
  y: number
  title?: string
  lines: TipLine[]
}

/**
 * The cursor of a bucketed chart: one bucket at a time, from the pointer or the keyboard (←/→, Home/End, Escape),
 * so the whole row of series is read in one tooltip. The SVG itself is the focusable element.
 */
export function useBucketCursor(n: number) {
  const [cursor, setCursor] = useState<number | null>(null)
  useEffect(() => {
    if (cursor !== null && cursor >= n) setCursor(n > 0 ? n - 1 : null)
  }, [n, cursor])
  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    let next: number | null
    switch (e.key) {
      case 'ArrowLeft':
        next = stepCursor(cursor, -1, n)
        break
      case 'ArrowRight':
        next = stepCursor(cursor, 1, n)
        break
      case 'Home':
        next = n > 0 ? 0 : null
        break
      case 'End':
        next = n > 0 ? n - 1 : null
        break
      case 'Escape':
        next = null
        break
      default:
        return
    }
    e.preventDefault()
    setCursor(next)
  }
  return { cursor, setCursor, onKeyDown, onBlur: () => setCursor(null) }
}

/** Per-mark tooltips (bars, cells): the readout follows the pointer, or sits on the focused mark. */
export function useTooltip() {
  const [tip, setTip] = useState<TipState | null>(null)
  const show = (e: PointerEvent | FocusEvent, title: string | undefined, lines: TipLine[]) => {
    const host = (e.currentTarget as Element).closest('[data-chart]') as HTMLElement | null
    const rect = host?.getBoundingClientRect()
    const target = (e.target as Element).getBoundingClientRect()
    const px = 'clientX' in e ? e.clientX : target.left + target.width / 2
    const py = 'clientY' in e ? e.clientY : target.top
    setTip({ x: px - (rect?.left ?? 0), y: py - (rect?.top ?? 0), title, lines })
  }
  const hide = () => setTip(null)
  return { tip, show, hide }
}
