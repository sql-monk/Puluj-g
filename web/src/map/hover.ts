/** Public, short labels only.  The caller supplies already-visible objects. */
export function hoverLabel(values: { target?: string; event?: string; incident?: string; region?: string }): string | null {
  return values.target ?? values.event ?? values.incident ?? values.region ?? null
}
