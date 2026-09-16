/** Ukrainian labels of the incident read-side vocabulary (ADR-0011); shared by the popup and the legend. */
export const stateLabel: Record<string, string> = {
  reported: 'повідомлено',
  confirmed: 'підтверджено',
  resolved: 'завершено',
  retracted: 'відкликано',
}

/** §8.5: what the marker means — a point only for located evidence; a city is a marker, not an address; coarser levels are areas. */
export const precisionLabel: Record<string, string> = {
  point: 'точка',
  city: 'населений пункт (маркер, не адреса)',
  district: 'район — приблизна область',
  region: 'область — приблизна область',
  unknown: 'без локації',
}

/** The glyph shapes of the incident kinds, named: the legend prints the shape next to the colour so meaning never rests on colour alone. */
export const shapeLabel: Record<string, string> = {
  burst: 'зірка',
  flame: 'полум’я',
  bolt: 'блискавка',
  square: 'квадрат',
  shield: 'щит',
  chevron: 'шеврон',
  circle: 'коло',
}

export const confidenceLabel: Record<string, string> = { unknown: 'невідома', low: 'низька', medium: 'середня', high: 'висока', confirmed: 'підтверджена' }
