import { expect, test } from '@playwright/test'
import { clickIncident, openMap, sourceFeatures } from './helpers'

/**
 * E01 (§8.5): the layer draws exactly what the API's precision allows — a point/city as a glyph at the reported point,
 * a district without a known polygon as an error circle, a region as its polygon, nothing at all without a location;
 * the popup labels the precision and shows the radius for anything coarser than a point.
 */
test.describe('E01 precision', () => {
  test('point/city → glyph, district → circle, region → polygon, no location → nothing', async ({ page }) => {
    await openMap(page)
    const points = await sourceFeatures(page, 'incident-points')
    const areas = await sourceFeatures(page, 'incident-areas')
    const ids = points.map((f) => Number(f.properties.id)).sort((a, b) => a - b)
    // 1 point, 2 city, 3 district, 4 region, 7 Kyiv district; 5 has no location, 6 is a catalog-hidden kind.
    expect(ids).toEqual([1, 2, 3, 4, 7])
    expect(points.find((f) => f.properties.id === 1)?.properties).toMatchObject({ precision: 'point', approx: '', icon: 'incident-explosion' })
    expect(points.find((f) => f.properties.id === 2)?.properties).toMatchObject({ precision: 'city', approx: '' })
    expect(points.find((f) => f.properties.id === 3)?.properties).toMatchObject({ precision: 'district', approx: '≈' })
    expect(points.find((f) => f.properties.id === 4)?.properties).toMatchObject({ precision: 'region', approx: '≈', icon: 'incident-impact' })
    const areaIds = areas.map((f) => Number(f.properties.id)).sort((a, b) => a - b)
    expect(areaIds).toEqual([3, 4, 7])
    // District 501 has no polygon anywhere → a 64-segment circle of the 30 km radius; region 8 and Kyiv district 2500 → their polygons.
    const circle = areas.find((f) => f.properties.id === 3)!.geometry.coordinates as number[][][]
    expect(circle[0]).toHaveLength(65)
    const region = areas.find((f) => f.properties.id === 4)!.geometry.coordinates as number[][][]
    expect(region[0]).toEqual([[35.2, 48.7], [38.2, 48.7], [38.2, 50.5], [35.2, 50.5], [35.2, 48.7]])
    const kyiv = areas.find((f) => f.properties.id === 7)!.geometry.coordinates as number[][][]
    expect(kyiv[0][0]).toEqual([30.52, 50.4])
  })

  test('popup labels the precision and the radius; a city marker is never an address', async ({ page }) => {
    await openMap(page)
    await clickIncident(page, 36.7, 49.6, 8)
    const dialog = page.getByRole('dialog')
    await expect(dialog).toContainText('Підтверджене влучання')
    await expect(dialog).toContainText('область — приблизна область')
    await expect(dialog).toContainText('±128 км')
    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)

    await clickIncident(page, 36.23, 49.99, 11)
    await expect(page.getByRole('dialog')).toContainText('населений пункт (маркер, не адреса)')
    await expect(page.getByRole('dialog')).toContainText('±15 км')
  })
})
