# U13 public API: provisional performance budget

## Decision and scope

This is a **repeatable release-smoke budget**, not a production SLO or capacity
claim.  The U13 release scope is explicitly limited to the disposable real-stack
acceptance runner in `scripts/test-u13-actual.ps1`: PostGIS, worker migration and
seed, the compiled API/static SPA, real public HTTP endpoints and a browser
journey.  A future load/capacity task must replace this document with measured
10,000-item / 1,000-update production-like results before any production SLO is
advertised.

## Version 1 budget

| Operation | Population / bound | Budget | Evidence |
| --- | --- | --- | --- |
| Schema + deterministic fixture | Disposable PostGIS; 123 raw messages, 123 facts, active track/incident/alert, located and unlocated facts | must complete; no fallback DB | `test-u13-actual.ps1` command manifest |
| Public messages | 100-item page plus opaque continuation; same fixed `from/to` window | HTTP 200, exactly 100 first-page rows, continuation accepted | runner preflight and actual API |
| Message detail | Saved revision chain and exact 64-bit target ID | HTTP 200, 2 revisions, decimal ID unchanged | runner preflight and Playwright API contract |
| Entity catalogue | Track, incident and alert fixture rows; active filter; historical track detail | HTTP 200 and all three aggregate kinds visible | runner preflight |
| Browser public UI | Built SPA served by the API, desktop viewport | routes to messages and entities; aXe has zero violations | `E13-actual-api.e2e.ts` and screenshot/report |
| Map hub reachability | Real API SignalR negotiate endpoint | HTTP 200 with a connection id | `E13-actual-api.e2e.ts` |

The runner records the commit, command start/end times, exit codes, the built
asset path, API logs, screenshot and Playwright HTML output in
`../artifacts/u13-actual/`.  An unavailable Docker daemon, migration, fixture,
API, browser journey or threshold is a failed smoke gate.

## Explicit non-goals

- No cold/warm p95, response-byte, query-count or hardware SLO is asserted.
- No 10,000 map-item or 1,000 update-burst load result is claimed.
- No production environment or capacity conclusion follows from this evidence.

Those measurements remain valuable follow-up work, but are not a prerequisite
for this narrowed U13 release acceptance.
