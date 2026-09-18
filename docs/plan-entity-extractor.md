# Entity Extractor delivery plan

Baseline: `45bbd79` on branch `codex/entity-extractor`.

## Fixed scope

- PostgreSQL and `raw_messages` are shared by the legacy processor and the Entity Extractor (EE).
- EE never changes the legacy processing claim/status columns or writes legacy processor outputs.
- A committed `raw_messages` insert creates a small delivery-queue row. Existing worker processes deliver it to EE over asynchronous HTTP; entity writes and lock waits happen only in EE.
- EE is the only new service. The public and admin services are source forks of the existing applications and replace those two services in the EE deployment. The legacy public `web` and `Puluj.Api` sources remain unchanged. Worker and shared Domain/Infrastructure receive additive queue/delivery changes; the legacy admin backend receives only the shared reset/deployment safeguards needed to preserve EE configuration and stop the EE container safely.
- A Python extractor receives the message and one callable, `write(table_name, values)`. `values` is a key/value collection. The callable is the only entity-write API.
- Extractors return no entity payload. The orchestrator reports `1` after at least one committed entity write, `0` after successful rules and LLM produce none, and a non-2xx error for technical failures.
- Python code is current-state only: edit, validate, test, save, enable/disable. There is no rules version history or rollback.
- Entity kinds are concrete (`targets`, `tracks`, `alerts`, `impacts`, `explosions`, `airDefenseActions`, `launches`, `takeoffs`), with more kinds created from the admin UI.
- Existing `Llm:*` settings and `llm_requests` audit are reused.
- The public fork preserves existing behavior and APIs except that entity overlays/catalogue/details/history read EE entity tables and refresh by polling.

## Phase 1 — contracts and additive database model

1. Define the HTTP request (`deliveryId`, `rawMessageId`, source, timestamps, text, raw payload, URL) and scalar `1`/`0` response.
2. Add EF entities/configurations and an additive migration for:
   - `ee_delivery_queue` and `ee_delivery_attempts`;
   - `ee_extractors` (one current code body per extractor);
   - `ee_entity_definitions` (entity/table name, fields, map rendering settings, enabled flag);
   - `ee_processing_runs`, `ee_extractor_runs`, and `ee_entity_writes` technical audit.
3. Add an `AFTER INSERT` trigger that enqueues new `raw_messages` in the same transaction without touching legacy status/claim fields.
4. Add PostgreSQL procedures to enqueue one raw ID, an ID array, and a bounded source/time/ID selection. Migration does not enqueue history.
5. Seed concrete entity definitions and starter Python extractors without creating a generic `events` entity.

Acceptance:

- insert commit creates one queue row; rollback creates none;
- legacy processing columns are byte-for-byte unchanged by EE operations;
- migrations preserve every legacy table and existing row;
- procedure selection is bounded and does not silently enqueue all history.

## Phase 2 — asynchronous HTTP delivery inside the existing worker

1. Add an `EntityDeliveryLoop` hosted component to collector worker processes; do not add a dispatcher service/container.
2. Claim queue rows with a lease and `SKIP LOCKED`, post to EE with `deliveryId` as the idempotency key, and record the HTTP attempt.
3. On connection failure, timeout, or non-success response, persist the error, finish that delivery as failed, and continue immediately with the next row.
4. Recover an abandoned lease safely. EE uses `deliveryId` to return the already completed scalar result instead of executing it twice.
5. Add DB-backed settings for EE URL, delivery timeout, polling interval, and concurrency; expose them through the forked admin.

Acceptance:

- unavailable EE never blocks ingestion or the legacy processor;
- N queued messages produce N delivery outcomes and the sender continues after each failure;
- a lost HTTP response and repeated delivery ID do not repeat a completed extraction.

## Phase 3 — the single Python EE service

1. Build one FastAPI service with `/health`, `/extract`, and admin-only validation/test endpoints.
2. Run different messages concurrently with a configured bound. Run each extractor in a child process with a timeout and bounded output.
3. Support either `extract(message, write)` or an `Extractor.extract(message, write)` method.
4. Implement the two-argument `write(table_name, values)` callable. Child processes return only captured write calls; the EE host validates and commits them, so child code receives no database credentials.
5. For each extractor, commit its captured writes and audit row in one transaction. A successful extractor can create multiple rows. Completed extractor steps are skipped on recovery of the same delivery.
6. Aggregate normal-rule results:
   - at least one committed write -> `1`; failures of other extractors remain in the log;
   - no committed write plus any timeout, exception, invalid value, or DB failure -> technical error, never `0`;
   - every enabled extractor completed with no write -> run LLM.
7. Read current extractor definitions for new work without rebuilding the image.

Acceptance:

- the callable has exactly two user-visible parameters;
- syntax/test execution never writes production entities;
- infinite loop and exception are contained and audited;
- parallel messages do not share captured writes or state;
- one extractor failure does not erase already committed writes and does not trigger LLM as a false zero.

## Phase 4 — simple dynamic entity tables and map configuration

1. Implement an admin operation accepting one entity name plus field name/type rows. The physical table name uses the `ee_` Entity Extractor prefix to avoid legacy collisions (for example `target` -> `ee_targets`), while the generated ID remains `targetId`.
2. Create a normal PostgreSQL table with:
   - generated `<entityName>Id` identity column;
   - automatic `rawMessageId` link;
   - configured fields.
3. Support a small field-type set: text, integer, decimal, boolean, datetime, JSON, point, line, polygon.
4. Store map settings on the entity definition: visible, label/time/status/lifetime fields, geometry or lat/lon fields, renderer, sanitized SVG icon, and line/polygon style.
5. Validate table/field identifiers and field values; runtime writes may target only registered entity tables.
6. Do not add a ledger, table versions, a DDL wizard, arbitrary SQL, or rule-author audit.

Acceptance:

- `target` creates `targetId`, `explosion` creates `explosionId`;
- `write("explosions", values)` inserts configured fields and automatic raw-message linkage;
- unknown tables/fields or wrong value types fail with an audited technical error;
- SVG scripts, event handlers, and external references cannot reach the browser.

## Phase 5 — existing LLM fallback

1. On a true aggregate rule result of zero, read existing `Llm:*` settings and API key from the same configuration sources as the current system.
2. Build the prompt from enabled concrete entity definitions and their fields.
3. Require an LLM response containing table plus key/value fields; pass each valid result through the same `write(table_name, values)` path.
4. Insert the complete sanitized request/response, usage, cost, latency, outcome, and error into the existing `llm_requests` table linked to the raw message.
5. Return final `1` when LLM committed a write, `0` when it successfully found none, and an error on provider/schema/write failure.

Acceptance:

- existing API key/model/settings are used; no EE-specific LLM credentials are introduced;
- invalid JSON or an unknown field cannot create a row;
- every provider call is visible in existing LLM audit without storing an API key.

## Phase 6 — forked public replacement

1. Copy the current frontend into `entity-web` and add `Puluj.EntityApi`; do not edit the legacy `web` or `Puluj.Api` sources.
2. Retain the existing basemap, navigation, reference data, filters, statistics, and non-entity API behavior.
3. Add generic EE entity-definition, snapshot, catalogue, detail, and history endpoints backed by registered entity tables.
4. Replace only entity overlays/data adapters in the fork. Do not connect entity overlays to the legacy SignalR stream.
5. Render configured point/SVG, line, and polygon entities; keep entities without geometry in the catalogue.
6. Add a persisted 15/30/60/120-second selector, visible-route-only polling, one in-flight request, manual refresh, last-success time, and non-destructive error state.
7. Preserve camera, zoom, filters, selection, panels, and scroll across refreshes.

Acceptance:

- the fork still provides the old public functionality outside entity data;
- deliberately different legacy and EE entity rows prove the fork renders only EE entities;
- hidden tabs do not poll and returning performs at most one overdue refresh;
- large IDs remain strings and refresh does not recreate the MapLibre instance.

## Phase 7 — forked admin replacement

1. Host the admin entry from `entity-web` in `Puluj.EntityAdmin`; preserve existing admin functions while leaving legacy admin sources unchanged.
2. Add EE panels for queue/logs, raw-message enqueue, extractors, test execution, entity/table definitions, map settings, and existing LLM audit.
3. Use CodeMirror 6 with locally bundled Python syntax highlighting, line numbers, search, cursor position, and server diagnostic markers.
4. Save only the current extractor body; enable/disable without version or rollback UI.
5. Add the simple field/type editor and SVG/renderer inputs.

Acceptance:

- test execution shows `1`, `0`, or error and never writes production rows;
- editing and saving a parser changes subsequent processing without image rebuild;
- entity definition creates the requested table and is immediately usable by `write` and the public fork.

## Phase 8 — packaging, deployment, and compatibility

1. Add exactly one new Compose service: `entity-extractor`.
2. Build the public and admin forks as replacement images for the existing public/admin roles in the EE deployment; backend and SPA stay together in each image.
3. Keep PostgreSQL, collectors, legacy processor, and analytics. The HTTP sender is a hosted component, not another service.
4. Add health checks and operational status for queue age, last delivery error, active processing count, and EE availability.
5. Ensure reset behavior preserves extractor/entity definitions and clears operational queue/runs/results consistently.

Acceptance:

- Compose adds one service only;
- the public/admin replacements start on the expected ports;
- stopping EE does not stop collectors, processor, analytics, or raw-message insertion;
- the legacy applications remain buildable from untouched source directories.

## Phase 9 — tests, independent reviews, fixes, and delivery

1. Run focused unit tests for queue claims, request contract, identifier/type validation, Python execution, `write`, LLM parsing, polling, visibility, and SVG sanitization.
2. Run real disposable-PostGIS integration tests for trigger/rollback, manual enqueue, asynchronous HTTP success/failure, same raw row processed by both pipelines, recovery, dynamic table creation, and existing `llm_requests` audit.
3. Run frontend unit tests and Playwright for parser editing, diagnostics, test execution, entity creation/rendering, 15/30/60/120 polling, hidden-tab behavior, manual refresh, and preserved map state.
4. Run Docker smoke with old processor plus EE and the two replacement applications.
5. Freeze the implementation commit and dispatch three independent subagents:
   - requirements/post-review against this document;
   - code review focused on concurrency, DB transactions, Python execution, dynamic SQL, LLM audit, and XSS;
   - independent test execution from a clean state.
6. Fix all critical/high findings and material medium findings. Ask reviewers to verify their findings again and rerun affected plus coexistence suites.
7. Run final locked solution/frontend checks, inspect the complete diff, ensure no unrelated files are staged, create task-scoped commit(s), and push `codex/entity-extractor`.
8. If repository integration requires a PR/merge, prepare it, verify current remote `main`, merge only when no unresolved review/test failures remain, then report the final commit and remote state.

## Completion gate

Work is complete only when one committed raw message can be processed concurrently by the legacy processor and EE; ingestion does not wait for entity-table writes; Python code can be edited and syntax-highlighted without rebuilding EE; concrete entities are stored in configured tables through `write(table, values)`; LLM fallback uses existing settings/audit; the public replacement preserves old behavior while polling EE entities; the admin replacement manages the complete flow; independent post-review, code review, and tests have passed; and the final branch is committed and pushed.
