# U04 public catalogue handoff

The read-only public catalogue is exposed through `/api/public/entities` and the entity detail routes:

- `/api/public/entities/{kind}/{id}`
- `/api/public/entities/{kind}/{id}/evidence`
- `/api/public/entities/{kind}/{id}/messages`
- `/api/public/entities/{kind}/{id}/relations`

`kind` is `track`, `incident`, `alert`, or `observation`; every public entity and raw-message identifier is serialized as a string. The list returns metadata and a location locator only. Full geometry is retained for a detail response, while raw message text, payloads, and parser metadata are never allow-listed.

## Query contract and consistency

The list accepts the canonical public filter vocabulary (`entityKinds`, `q`, `eventKinds`, `eventCategories`, taxonomy/source IDs, `regionId`, `status`, `confidence`, `location`, `from`, `to`, `cursor`, `pageSize`, `dataset`). The time interval is `[from,to)`, has a 24-hour default and a 366-day maximum. Alerts use interval overlap. Evidence predicates for tracks/incidents are correlated to one evidence fact rather than combined across rows.

The first live response returns `dataset=live` or a concrete `live:{generation}` descriptor. A page cursor is bound to the canonical filters and dataset. Detail links carry that descriptor; collection cursors are also bound to the kind, ID, collection name, and descriptor. A projection change produces `409`; an unavailable incident projection produces explicit `503`, never a silently incomplete incident result.

## Bounded-query evidence

The adapter composes existing `Targets`, `TargetTracks`, `AirAlerts`, and incident read models. Each list kind is keyset-bounded to `pageSize + 1` (maximum 100) before the final merge. Evidence, relations, and message references use SQL `Count`, ordered `Skip/Take`, or correlated `EXISTS`; no raw message bodies are selected and no full aggregate collection is materialized.

The concrete execution check is the PostGIS integration test:

`dotnet test tests/Puluj.Integration.Tests/Puluj.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~PublicCatalogQueriesTests" --verbosity:normal`

It seeds a bigint target ID, track membership, alert, and raw message; verifies canonical suppression in the list, direct observation detail, alert visibility, and that the public message payload has no text field. The focused run passed on 2026-09-16. Production EXPLAIN/ANALYZE remains an operational capture because its plan and row budget depend on the deployed PostGIS data volume and indexes; run it against the exact canonical filter before changing those indexes.
