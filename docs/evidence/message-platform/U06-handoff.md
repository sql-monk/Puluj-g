# U06 map filters and historical state handoff

## Public map contract

- `GET /api/snapshot`, `GET /api/replay`, and `GET /api/timeline` accept the canonical U03 map fields: taxonomy IDs, `sourceIds`, `eventKinds`, `eventCategories`, `entityKinds`, `regionId`, `status`, `confidence`, `location`, `q`, and `hasResults`.
- A filtered map response is built on the server and bypasses the short shared live-snapshot cache. The browser never applies a filter to the capped snapshot/replay/timeline payload.
- Filtered history intentionally omits the legacy capped incidents/feed surfaces until their own canonical filtered endpoints exist; it never labels an unfiltered 300/5000-row response as filtered.
- A replay/timeline request over 36 hours returns HTTP 400 with `code=window_too_large` and `maximumHours`; the server does not silently change the requested window.
- Map HTTP endpoints and the map SignalR protocol serialize every `long` identity as a decimal string. The existing non-map REST contracts remain numeric for compatibility; the browser treats map identifiers as opaque keys, including GeoJSON/store selection and predecessor-leg hit testing (no `Number(...)` conversion of map identities).

## Client consistency

Live and historical maps use the same hash query. Snapshot, replay, and timeline requests carry that query and use an `AbortController` plus request sequencing. Filtered SignalR deltas trigger a fresh server-filtered snapshot rather than re-adding an incomplete unfiltered delta. `at` is an optional frozen instant inside the half-open `from`/`to` history window and is preserved in a shared URL.

## Verification

- `dotnet test tests/Puluj.Api.Tests/Puluj.Api.Tests.csproj --no-restore --verbosity:minimal`
- `npm run test -- --run src/public/routes.test.ts` from `web`
- `npm run build` from `web`
