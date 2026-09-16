# U05 public message API handoff

## Endpoints

- `GET /api/public/messages` lists one stored `RawMessage` revision per row.
- `GET /api/public/messages/{id}` returns its allow-listed detail.
- `GET /api/public/messages/{id}/results`, `/revisions`, and `/text` continue bounded collections/text chunks.

All raw and result bigint IDs are JSON strings. A revision group is exactly `(SourceId, SourceMessageKey)`; `SourceRevision` is opaque and is never numerically ordered. Identical text from different source/key pairs remains distinct.

## Contract

The message list has a 24-hour `PublishedAt` default, `[from,to)` semantics, a 366-day maximum, keyset cursor (`PublishedAt DESC`, then raw ID DESC), and page size 50/max100. It accepts `sourceIds`, `q`, `hasResults`, `outcome`, plus result-derived taxonomy/region/location/confidence filters. Derived predicates are one correlated `Target` row, not independent joins. Without a derived filter, raw messages with zero facts are visible.

Public outcomes use a non-replay finalizer stage when it exists. Otherwise `RawMessage.ProcessingStatus` is exposed with `outcomeSource=legacy`; the API deliberately does not infer `no_facts` from absent `Targets`. Text is allow-listed, but a text over 32 KiB uses the bounded `/text` resource with an opaque cursor. Only http/https source links are exposed. `RawPayload`, parser/stage JSON, errors, worker identities, and secrets are never DTO fields.

## Verification

- `dotnet test tests/Puluj.Api.Tests/Puluj.Api.Tests.csproj --no-restore --verbosity:minimal` — 36 passed.
- `dotnet test tests/Puluj.Integration.Tests/Puluj.Integration.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~PublicMessageQueriesTests" --verbosity:minimal` — 1 PostGIS test passed.
- `npm run build` from `web` — passed (existing Vite sourcemap/chunk-size warnings only).

The integration fixture proves a pending structured/no-text raw stays in the list, a multi-fact raw returns all result records in detail, bigint target IDs survive as strings, and an unsafe source URL is omitted. Production query-plan metrics remain an operational capture against the deployed PostGIS volume/index set; the service itself uses ordered page queries and grouped/correlated reads rather than per-row round trips.
