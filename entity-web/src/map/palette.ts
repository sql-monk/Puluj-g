import type { DisplayMode } from '../api/types'
import type { Theme } from '../store/useStore'

/**
 * Everything on the map that carries colour, per theme: class markers, movement vectors, alert fills, land and
 * borders. Each theme keeps the same reading (yellow = target level, red = alert, one colour per class) in its own
 * key, so a sepia map is not a slate map with sepia panels.
 */
export interface MapPalette {
  /** Marker fill per display mode. Never yellow, orange or red: those are the alert fills the markers sit on.
   * Drones dark green, cruise missiles dark blue, ballistic bright violet, aircraft black — each with a light outline
   * of its own hue (markerEdge), so the class reads from the rim as well as the fill on any ground. */
  marker: Record<DisplayMode, string>
  /** Outline of the marker glyph per display mode: the light tint of the fill's hue (white for aircraft). */
  markerEdge: Record<DisplayMode, string>
  /** The selected target's colour per display mode: a strong magenta ink no class or alert uses (cyan for the violet
   * ballistic class), for its glyph, its predecessors (fainter) and its own vectors. */
  selected: Record<DisplayMode, string>
  /** Halo around marker glyphs, so they read over any fill. */
  glyphHalo: string
  glyphEdge: string
  alertRedFill: string
  alertRedLine: string
  alertYellowFill: string
  alertYellowLine: string
  /** Oblast land fill inside Ukraine, and the alternating district fill on the Kyiv page. */
  land: string
  landAlt: string
  oblastLine: string
  border: string
  borderHalo: string
  /** Outline of the clicked region. */
  selectedRegion: string
  cluster: string
  clusterText: string
  /** Label text / halo. */
  label: string
  labelHalo: string
  /** The viewer's own point: the theme's plain ink, a ring no class colour shares. */
  home: string
  hazardNear: string
  hazardTowards: string
  /** Forecast vectors of every unselected target: neutral grey of the theme. */
  vectorMuted: string
}

const light: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#9333ea', aircraft: '#000000' },
  markerEdge: { uav: '#86efac', cruise: '#93c5fd', ballistic: '#d8b4fe', aircraft: '#ffffff' },
  selected: { uav: '#d946ef', cruise: '#d946ef', ballistic: '#0891b2', aircraft: '#d946ef' },
  glyphHalo: 'rgba(255,255,255,0.95)',
  glyphEdge: 'rgba(0,0,0,0.8)',
  alertRedFill: '#f2b8b5',
  alertRedLine: '#b91c1c',
  alertYellowFill: '#f7e58c',
  alertYellowLine: '#ca8a04',
  land: '#cfe3f7',
  landAlt: '#dbeafe',
  oblastLine: '#5b8fc7',
  border: '#1e3a8a',
  borderHalo: '#ffffff',
  selectedRegion: '#d97706',
  cluster: '#15803d',
  clusterText: '#111111',
  label: '#ffffff',
  labelHalo: '#000000',
  home: '#0f172a',
  hazardNear: '#ef4444',
  hazardTowards: '#f97316',
  vectorMuted: '#8a94a6',
}

const sepia: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#9333ea', aircraft: '#000000' },
  markerEdge: { uav: '#9ee89e', cruise: '#a6c8f0', ballistic: '#dcc0f5', aircraft: '#ffffff' },
  selected: { uav: '#b8309a', cruise: '#b8309a', ballistic: '#1d7a8c', aircraft: '#b8309a' },
  glyphHalo: 'rgba(255,250,240,0.95)',
  glyphEdge: 'rgba(60,40,20,0.85)',
  alertRedFill: '#e9b3a0',
  alertRedLine: '#9a3412',
  alertYellowFill: '#f1e1a4',
  alertYellowLine: '#a16207',
  land: '#efe4cc',
  landAlt: '#f4ecd9',
  oblastLine: '#a58a5c',
  border: '#5c3d1a',
  borderHalo: '#fff8ea',
  selectedRegion: '#9a5b12',
  cluster: '#3f7d2a',
  clusterText: '#2b1d0e',
  label: '#fffaf0',
  labelHalo: '#2b1d0e',
  home: '#33281a',
  hazardNear: '#b3261e',
  hazardTowards: '#c2410c',
  vectorMuted: '#a89b85',
}

const graphite: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#9333ea', aircraft: '#000000' },
  markerEdge: { uav: '#86efac', cruise: '#93c5fd', ballistic: '#d8b4fe', aircraft: '#ffffff' },
  selected: { uav: '#d63de0', cruise: '#d63de0', ballistic: '#0e9bb5', aircraft: '#d63de0' },
  glyphHalo: 'rgba(255,255,255,0.95)',
  glyphEdge: 'rgba(30,35,45,0.85)',
  alertRedFill: '#dea19b',
  alertRedLine: '#a52a2a',
  alertYellowFill: '#eee0a0',
  alertYellowLine: '#a16207',
  land: '#d5dbe4',
  landAlt: '#e0e5ec',
  oblastLine: '#7d8796',
  border: '#2f3947',
  borderHalo: '#f5f6f8',
  selectedRegion: '#3b82f6',
  cluster: '#22a04a',
  clusterText: '#111111',
  label: '#ffffff',
  labelHalo: '#1f242c',
  home: '#1f242c',
  hazardNear: '#dc3c3c',
  hazardTowards: '#ea7a1a',
  vectorMuted: '#8b93a1',
}

const dark: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#a855f7', aircraft: '#000000' },
  markerEdge: { uav: '#4ade80', cruise: '#7dd3fc', ballistic: '#e9d5ff', aircraft: '#ffffff' },
  selected: { uav: '#e879f9', cruise: '#e879f9', ballistic: '#22d3ee', aircraft: '#e879f9' },
  glyphHalo: 'rgba(255,255,255,0.95)',
  glyphEdge: 'rgba(0,0,0,0.8)',
  alertRedFill: '#7f1d1d',
  alertRedLine: '#b91c1c',
  alertYellowFill: '#a16207',
  alertYellowLine: '#facc15',
  land: '#1b3149',
  landAlt: '#1e3a5c',
  oblastLine: '#7fb3e6',
  border: '#7dd3fc',
  borderHalo: '#0b1220',
  selectedRegion: '#fbbf24',
  cluster: '#22c55e',
  clusterText: '#111111',
  label: '#ffffff',
  labelHalo: '#000000',
  home: '#f8fafc',
  hazardNear: '#ef4444',
  hazardTowards: '#f97316',
  vectorMuted: '#6b7a8f',
}

const midnight: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#a855f7', aircraft: '#000000' },
  markerEdge: { uav: '#4ade80', cruise: '#7dd3fc', ballistic: '#e9d5ff', aircraft: '#ffffff' },
  selected: { uav: '#f0abfc', cruise: '#f0abfc', ballistic: '#2dd4bf', aircraft: '#f0abfc' },
  glyphHalo: 'rgba(238,243,255,0.95)',
  glyphEdge: 'rgba(4,10,28,0.9)',
  alertRedFill: '#6b1d2e',
  alertRedLine: '#f43f5e',
  alertYellowFill: '#6f5416',
  alertYellowLine: '#facc15',
  land: '#12213f',
  landAlt: '#172a4d',
  oblastLine: '#5b7fc2',
  border: '#8fb6ff',
  borderHalo: '#040a1c',
  selectedRegion: '#fbbf24',
  cluster: '#4ade80',
  clusterText: '#0a1530',
  label: '#eef3ff',
  labelHalo: '#040a1c',
  home: '#eef3ff',
  hazardNear: '#fb7185',
  hazardTowards: '#fb923c',
  vectorMuted: '#5f6f95',
}

const olive: MapPalette = {
  marker: { uav: '#166534', cruise: '#1e3a8a', ballistic: '#a855f7', aircraft: '#000000' },
  markerEdge: { uav: '#86efac', cruise: '#9fd8f0', ballistic: '#e4ccf7', aircraft: '#ffffff' },
  selected: { uav: '#f08ae0', cruise: '#f08ae0', ballistic: '#5fd7e8', aircraft: '#f08ae0' },
  glyphHalo: 'rgba(242,245,234,0.95)',
  glyphEdge: 'rgba(13,18,9,0.9)',
  alertRedFill: '#6e2a22',
  alertRedLine: '#e06b5f',
  alertYellowFill: '#6b6118',
  alertYellowLine: '#d9c94a',
  land: '#1e2a1a',
  landAlt: '#243120',
  oblastLine: '#6f8560',
  border: '#b7cf8f',
  borderHalo: '#0d120a',
  selectedRegion: '#e8c547',
  cluster: '#7fd66a',
  clusterText: '#181f12',
  label: '#f2f5ea',
  labelHalo: '#0d120a',
  home: '#f2f5ea',
  hazardNear: '#e0655c',
  hazardTowards: '#e8963a',
  vectorMuted: '#7d8a72',
}

const palettes: Record<Theme, MapPalette> = { light, sepia, graphite, dark, midnight, olive }

export function getPalette(theme: Theme): MapPalette {
  return palettes[theme] ?? light
}

export const DISPLAY_MODES: DisplayMode[] = ['uav', 'cruise', 'ballistic', 'aircraft']
