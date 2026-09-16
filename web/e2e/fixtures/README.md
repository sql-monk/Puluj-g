# E2E fixtures

- `api.ts` — every API answer the map app needs (deterministic; times relative to the test start).
- `glyphs-0-255.pbf` — one glyph range of «Noto Sans Regular» from OpenFreeMap (`https://tiles.openfreemap.org/fonts/Noto%20Sans%20Regular/0-255.pbf`),
  served for every glyph request so symbol layers (cluster counts) render without the network. Font: Noto Sans, SIL Open Font License 1.1.
