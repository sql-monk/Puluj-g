# Public UI: release acceptance and rollback

## Reproducible gate

Run `pwsh scripts/test-u13.ps1` from the repository root. It deliberately clears
`PULUJ_TEST_CONNECTION` and `PULUJ_TEST_ALLOW_RESET`; `PipelineFixture` therefore uses
its disposable Testcontainers PostGIS instance. The command writes TRX, Playwright output,
an HTML report and a manifest to `../artifacts/u13-release/` (outside the source tree). A missing Docker daemon or a
failing test is a failed gate, not a pass.

The runner has two kinds of evidence:

- `u13-api.trx` and `u13-postgis.trx` exercise the actual .NET services and PostGIS provider;
- the Playwright suite exercises the built React application, navigation, visual snapshots and
  accessibility against deterministic API fixtures.

The latter is UI-contract regression coverage, not evidence that a browser has reached a
production-like backend. Before a release candidate, record a separate environment run with
its commit/image digest, URL, browser version, redacted screenshots, API timings and the
same journeys. Do not substitute a skipped browser, mock-only request, or unavailable
projection for a passed release condition.

## Public contract

Routes are hash-based and compatible with existing links:

| Area | Route | State carried in URL |
| --- | --- | --- |
| Live map | `#/map/live` | canonical common filters and optional map selection |
| History map | `#/map/history` | common filters, `at`, selection and returned context |
| Entities | `#/entities` and `#/entities/{kind}/{id}` | collection filters, opaque cursor, `dataset` |
| Messages | `#/messages` and `#/messages/{id}` | source/outcome/time filters, opaque cursor, `dataset` |
| Analytics | `#/analytics` | metric, common filters and UTC period |

IDs stay decimal strings through DTO, URL, SignalR and GeoJSON boundaries. Cursors are
opaque: callers may retain and resend them, but may not parse, sort or numeric-cast them.
Raw-message aggregation uses `PublishedAt UTC`; fact/track aggregation uses `ObservedAt
UTC`. Analytics responses label both populations where they are displayed together.

The public surface is read-only. It uses the public allowlisted DTOs, renders raw text as
text rather than HTML, and makes source links safe external links. No admin token, mutation
or configuration control belongs in the map bundle.

## Acceptance record

For each release candidate, retain a concise record outside the source tree with:

1. commit/image digest, environment, hardware, dataset cardinalities and command exit codes;
2. all journeys, including Back/Forward, >100-row paging, missing geometry, 0 results, 404,
   invalid filter, timeout, active-generation switch and reconnect/late response results;
3. 360px, 768px and 1440px screenshots showing menu/drawers, keyboard focus/Escape, loading,
   empty and error states; and the a11y tool result;
4. the versioned smoke budget in `docs/public-api-performance-budget.md`, its exact fixture
   population and its command manifest.  This U13 release gate does not claim a production
   SLO; 10,000-item/1,000-update load, cold/warm p95, payload/query-count and capacity
   profiling are explicitly deferred follow-up work;
5. independent implementation-review findings, fixes and affected-test reruns.

The deterministic suite deliberately includes values above `Number.MAX_SAFE_INTEGER`,
structured/no-text raws, revisions, differing sources with the same text, incidents,
alerts, tracks, unlocated locations and catalog changes. Small fixtures validate semantics;
they do not establish a production SLO.

## Rollout and rollback

1. Build and publish backend first, apply additive migrations, and wait for schema readiness.
   Verify public read-only credentials and allowlist before serving traffic.
2. Publish the matching frontend assets only after the backend endpoints and DTOs are healthy.
   Preserve old hash routes and API fields during the compatibility window.
3. Smoke test a live map, history map, entity/message detail, analytics and a source link.
   Record the asset hashes and active generation before increasing traffic.
4. To roll back the UI, restore the previous static asset set that matches a still-compatible
   backend contract. Do not roll back database data merely to restore a frontend asset.
5. If an API contract must be rolled back, first remove the new frontend assets, drain or
   disable incompatible traffic, and use the documented generation rollback. Preserve the
   acceptance evidence and investigate any client/API version mismatch.

Deployment itself is intentionally out of U13 scope.
