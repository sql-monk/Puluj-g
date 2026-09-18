import { describe, expect, it } from 'vitest'
import { areaPath, barPath, columnPath, foldSeries, frame, indexAt, labelEvery, linePath, niceTicks, routeMatrix, scale, slots, stackLayers, stepCursor } from './geometry'

describe('axis ticks', () => {
  it('start at 0 and cover the maximum with few round steps', () => {
    expect(niceTicks(0)).toEqual([0])
    for (const max of [1, 7, 23, 87, 450, 1234, 99999]) {
      const t = niceTicks(max)
      expect(t[0]).toBe(0)
      expect(t[t.length - 1]).toBeGreaterThanOrEqual(max)
      expect(t.length).toBeLessThanOrEqual(6)
      for (let i = 1; i < t.length; i++) expect(t[i] - t[i - 1]).toBeCloseTo(t[1] - t[0])
    }
    expect(niceTicks(87)).toEqual([0, 25, 50, 75, 100])
  })

  it('labels every k-th bucket', () => {
    expect(labelEvery(24, 12)).toBe(2)
    expect(labelEvery(7, 12)).toBe(1)
    expect(labelEvery(168, 12)).toBe(14)
  })
})

describe('frame and slots', () => {
  it('widens the left band for long tick labels', () => {
    const narrow = frame(400, 200, ['0', '50', '100'])
    const wide = frame(400, 200, ['0', '2 000 год', '4 000 год'])
    expect(narrow.padL).toBe(36)
    expect(wide.padL).toBeGreaterThan(narrow.padL)
    expect(wide.plotW).toBe(400 - wide.padL - wide.padR)
    expect(narrow.plotH).toBe(200 - narrow.padT - narrow.padB)
  })

  it('caps columns at 24 px and keeps a gap', () => {
    expect(slots(0, 300)).toEqual({ slot: 0, bar: 0 })
    expect(slots(10, 300)).toEqual({ slot: 30, bar: 24 })
    expect(slots(100, 300).bar).toBe(2)
    expect(slots(30, 300).bar).toBe(8)
  })

  it('maps values onto pixels, flipped for y', () => {
    const y = scale(100, 10, 200, true)
    expect(y(0)).toBe(210)
    expect(y(100)).toBe(10)
    const x = scale(0, 0, 100)
    expect(x(0)).toBe(0) // a zero domain does not divide by zero
  })
})

describe('stacks and paths', () => {
  it('stacks series bucket by bucket', () => {
    const { totals, layers } = stackLayers([
      [1, 2, 0],
      [3, 0, 4],
    ])
    expect(totals).toEqual([4, 2, 4])
    expect(layers[0]).toEqual([
      [0, 1],
      [0, 2],
      [0, 0],
    ])
    expect(layers[1]).toEqual([
      [1, 4],
      [2, 2],
      [0, 4],
    ])
    expect(stackLayers([]).totals).toEqual([])
  })

  it('draws closed areas and open lines', () => {
    expect(areaPath([0, 10], [5, 3], [20, 20])).toBe('M0,5L10,3L10,20L0,20Z')
    expect(areaPath([], [], [])).toBe('')
    expect(linePath([0, 10, 20], [1, 2, 3])).toBe('M0,1L10,2L20,3')
  })

  it('rounds only the data end of a column or bar', () => {
    expect(columnPath(0, 0, 10, 0, 3)).toBe('')
    expect(columnPath(0, 0, 10, 20, 0)).toBe('M0,0h10v20h-10Z')
    expect(columnPath(0, 0, 10, 20, 3)).toMatch(/^M0,20v-17a3,3 0 0 1 3,-3h4a3,3 0 0 1 3,3v17Z$/)
    expect(barPath(0, 0, 20, 10, 4)).toMatch(/^M0,0h16a4,4 0 0 1 4,4v2a4,4 0 0 1 -4,4h-16Z$/)
  })
})

describe('pointer and keyboard', () => {
  it('finds the bucket under a pointer inside the plot only', () => {
    expect(indexAt(45, 40, 10, 5)).toBe(0)
    expect(indexAt(89, 40, 10, 5)).toBe(4)
    expect(indexAt(39, 40, 10, 5)).toBeNull()
    expect(indexAt(90, 40, 10, 5)).toBeNull()
    expect(indexAt(50, 40, 10, 0)).toBeNull()
  })

  it('steps the cursor and clamps at the ends', () => {
    expect(stepCursor(null, 1, 5)).toBe(0)
    expect(stepCursor(null, -1, 5)).toBe(4)
    expect(stepCursor(2, 1, 5)).toBe(3)
    expect(stepCursor(4, 1, 5)).toBe(4)
    expect(stepCursor(0, -1, 5)).toBe(0)
    expect(stepCursor(null, 1, 0)).toBeNull()
  })
})

describe('folds', () => {
  it('keeps the busiest origins and destinations and fills the cells from the pairs', () => {
    const routes = [
      { fromId: 1, fromName: 'A', toId: 10, toName: 'X', count: 5 },
      { fromId: 2, fromName: 'B', toId: 10, toName: 'X', count: 3 },
      { fromId: 1, fromName: 'A', toId: 11, toName: 'Y', count: 1 },
      { fromId: 3, fromName: 'C', toId: 12, toName: 'Z', count: 1 },
    ]
    const m = routeMatrix(routes, 2)
    expect(m.rows.map((x) => x.name)).toEqual(['A', 'B'])
    expect(m.cols.map((c) => c.name)).toEqual(['X', 'Y'])
    expect(m.values).toEqual([
      [5, 1],
      [3, 0],
    ])
  })

  it('folds the series beyond the palette into one "other" row', () => {
    const series = [1, 2, 3, 4].map((k) => ({ key: String(k), values: [k, k * 10] }))
    expect(foldSeries(series, 4, (values) => ({ key: 'other', values }))).toBe(series)
    const folded = foldSeries(series, 2, (values) => ({ key: 'other', values }))
    expect(folded.map((s) => s.key)).toEqual(['1', '2', 'other'])
    expect(folded[2].values).toEqual([7, 70])
  })
})
