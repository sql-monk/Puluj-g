import { useEffect, useRef, useState } from 'react'
import type * as React from 'react'

export interface DragOffset {
  dx: number
  dy: number
}

/**
 * Lets a map window be dragged by a handle (its header). Keeps the offset from the spot where the window opened;
 * the window's own layout adds it to the computed position. The offset is forgotten when `resetKey` changes (another
 * target, region or link was picked). Returns the handle's pointer-down handler and a render tick so the layout effect
 * re-runs while dragging.
 */
export function useDraggable(resetKey: unknown, enabled = true): { offset: React.RefObject<DragOffset>; onPointerDown: (e: React.PointerEvent<HTMLElement>) => void; tick: number; handleProps: { onPointerDown: (e: React.PointerEvent<HTMLElement>) => void; className: string; title?: string } } {
  const offset = useRef<DragOffset>({ dx: 0, dy: 0 })
  const [tick, setTick] = useState(0)
  useEffect(() => {
    offset.current = { dx: 0, dy: 0 }
    setTick((n) => n + 1)
  }, [resetKey])
  const onPointerDown = (e: React.PointerEvent<HTMLElement>) => {
    // Buttons and links inside the handle keep working as themselves.
    if (!enabled || e.button !== 0 || (e.target as HTMLElement).closest('button, a, input, select')) return
    const start = { x: e.clientX, y: e.clientY, dx: offset.current.dx, dy: offset.current.dy }
    const handle = e.currentTarget
    const move = (ev: PointerEvent) => {
      offset.current = { dx: start.dx + ev.clientX - start.x, dy: start.dy + ev.clientY - start.y }
      setTick((n) => n + 1)
    }
    const up = () => {
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', up)
      handle.removeEventListener('pointercancel', up)
    }
    handle.setPointerCapture(e.pointerId)
    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', up)
    handle.addEventListener('pointercancel', up)
    e.preventDefault()
  }
  return {
    offset,
    onPointerDown,
    tick,
    handleProps: enabled ? { onPointerDown, className: 'cursor-move touch-none select-none', title: 'Перетягніть, щоб пересунути' } : { onPointerDown, className: '' },
  }
}

/** Where a window goes: its default spot next to the anchor, moved by the drag offset and kept inside the map box. */
export function placeWindow(node: HTMLElement, left: number, top: number, offset: DragOffset, box: { width: number; height: number }, width: number, minTop = 44) {
  const h = node.offsetHeight
  const x = Math.min(Math.max(0, left + offset.dx), Math.max(0, box.width - width))
  const y = Math.min(Math.max(minTop, top + offset.dy), Math.max(minTop, box.height - Math.min(h, 80)))
  node.style.left = `${x}px`
  node.style.top = `${y}px`
}
