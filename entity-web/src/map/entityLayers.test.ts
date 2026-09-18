import { describe, expect, it } from 'vitest'
import { iconForRenderer } from './entityLayers'

describe('EE renderer icon selection', () => {
  it('does not suppress a point renderer merely because an SVG is configured', () => {
    expect(iconForRenderer('point', 'target', '<svg/>')).toBeUndefined()
  })

  it('versions icon renderer image names when SVG content changes', () => {
    expect(iconForRenderer('icon', 'target', '<svg><circle/></svg>')).not.toBe(iconForRenderer('icon', 'target', '<svg><path/></svg>'))
  })
})
