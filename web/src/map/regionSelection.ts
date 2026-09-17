import type { RegionDto } from '../api/types'

/**
 * A click is deliberately coarser than the rendered hit: a district is a
 * child of an oblast, so the first click must establish that parent context.
 * Keeping this free of MapLibre makes the interaction contract testable.
 */
export function selectedRegionFromHit(hit: RegionDto | undefined, selectedId: number | null, byId: ReadonlyMap<number, RegionDto>): number | null {
  if (!hit) return null
  if (hit.level === 'District' && hit.parentId !== undefined) return selectedId === hit.parentId ? hit.id : hit.parentId
  // An alert can be published for a lower administrative level that does not
  // have its own visible district layer. Walk it up to its visible oblast.
  let parent = hit.parentId === undefined ? undefined : byId.get(hit.parentId)
  while (parent && parent.level !== 'Region' && parent.level !== 'City') parent = parent.parentId === undefined ? undefined : byId.get(parent.parentId)
  return parent?.id ?? hit.id
}

/** Prefer the most precise visible feature, while still accepting alert fills. */
export function regionFromHits(hits: readonly { layer: { id: string }; properties?: Record<string, unknown> | null }[], byId: ReadonlyMap<number, RegionDto>): RegionDto | undefined {
  const regions = hits
    .map((hit) => Number(hit.layer.id === 'alerts-fill' ? hit.properties?.placeId : hit.properties?.id))
    .filter(Number.isFinite)
    .map((id) => byId.get(id))
    .filter((region): region is RegionDto => region !== undefined)
  return regions.find((region) => region.level === 'District') ?? regions.find((region) => region.level === 'Region' || region.level === 'City') ?? regions[0]
}
