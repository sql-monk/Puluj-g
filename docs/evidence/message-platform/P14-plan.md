# P14 — run/generation orchestration, isolated lanes, replay checkpoints, delta catchup/promote/rollback (§11) — план

Task: [P14 / issue #14](https://github.com/sql-monk/Puluj-g/issues/14). Залежності: контракт — P01 (ADR-0005), виконання — P09/P10/P11 — done.
Base commit: `6e183a5` (P13). Gate: live не зупиняється; shadow без production effects; atomic switch і відрепетируваний rollback.

## Стан коду на старті (звірено)

- Є: `processing.runs` (run_id, lane, kind live|history|replay, state, generation_id, supersedes_run_id, replays_run_id, versions, scope, checkpoint,
  finished_at; partial unique `ux_processing_runs_open_per_lane`), `processing.generations` (is_active, promoted_at, rolled_back_at, verified_by),
  `ProcessingRuns.GetOpenRunAsync` (один running run на lane live/history, версія pipeline pinned, без supersede), `stage_results` unique
  `(raw, run, stage, version)` (стадії ідемпотентні per run), `extractions` unique (raw, run), `observations` per run, incidents з `generation_id`
  (`IncidentStateWriter.EnsureGenerationAsync`: `run.generation_id ?? LiveGeneration`, is_active на insert), read-side `IncidentQueries.Active`
  (лише active generations), `ProjectionHandler` ігнорує lane `replay` і неактивну generation; topology v8: lane `replay` у raw-writer/normalizer/parser/
  llm-worker/finalizer/archive/message-analytics, **не** у track/alert/incident-worker/projection; parser у replay lane не викликає LLM
  (`llm_skipped_lane`); `Delivery.lane` (P13) дає pending per lane; `control_audit` (P13).
- Немає: replay run як job (scope, checkpoint, publisher), state machine, supersede live run, delta catchup/watermark, verify/promote/rollback,
  replay lane для incident-worker (агрегат з generation), квоти replay lane, admin API/UI для runs.
- Tracks/alerts не мають `generation_id` (ADR-0009: replay lane агрегатів — P14): їх ізоляція потребує generation-колонок і зміни всіх запитів мапи —
  **не в P14** (D7): replay lane не досягає track/alert-worker (topology), тож shadow не змінює треки/тривоги; incidents — повний цикл.

## Рішення

### D1. Run state machine і `RunService` (`Puluj.Infrastructure/Processing/RunService.cs`)

- `CreateReplayAsync(scope {sourceIds[]?, from, to, stages?}, actor, reason)` → `processing.runs {kind replay, lane replay, state created,
  generation_id = new (processing.generations is_active=false), replays_run_id = поточний live run, versions = {pipeline_version, catalog policy},
  scope jsonb, checkpoint {published:0, total:N, last_raw_message_id:0, done:false}}` + `control_audit run:create`.
- Переходи (ADR-0005): `created → running` (start), `running ↔ paused`, `running|paused → cancelled`, `running|paused → verified` (verify),
  `verified → promoted` (promote), `promoted → rolled_back` (rollback), `failed` з будь-якого активного (publisher). `UPDATE … WHERE state = @from`
  (compare-and-set, 409 при гонці); кожна команда — `control_audit` (`run:start|pause|resume|cancel|catchup|verify|promote|rollback`, actor, reason).
- **Supersede live run**: `ProcessingRuns.GetOpenRunAsync` — якщо відкритий run lane'а має інший `pipeline_version`, у tx: старий → `superseded`
  (`finished_at`), новий `running` з `supersedes_run_id` (гонка двох процесів — partial unique + re-select). Кешований id інвалідується.

### D2. Replay publisher (роль `replay`, `Puluj.Messaging/ReplayPublisher.cs`) з checkpoint, pause/cancel, квотами

- Hosted service: кожні `Replay:PollInterval` (2 с) бере один run `kind='replay' AND state='running'` `FOR UPDATE SKIP LOCKED` (одна репліка на run —
  fencing publisher'а), читає raw у scope (`source_id = ANY`, `published_at ∈ [from, to]`, `raw_message_id > checkpoint.last`) порядком
  `(published_at, raw_message_id)`, батч `Replay:BatchSize` (200): для кожного raw — `raw.stored` envelope (`RawStoredEnvelope`, lane `replay`,
  `processing_run_id = run`, producer `replay@{instance}`, `is_new:false`) через `OutboxWriter` **в тій самій tx**, що й `checkpoint` update
  (`last_raw_message_id`, `published`, `last_published_at`) → resumable без дублікатів (crash між батчами повторює лише неопублікований батч; stage
  unique per run робить повтор `noop`).
- **Квота/backpressure**: батч публікується лише якщо `pending deliveries lane='replay'` ≤ `Replay:MaxInFlight` (1000) — replay не заливає черги;
  консюмери: `Messaging:Consumer:PrefetchByLane {replay: 2}` (prefetch per lane, менший за live) — live має зарезервовану місткість каналу.
- Scope вичерпано → `checkpoint.done=true`, стан лишається `running` (catchup/verify — оператор); `paused/cancelled` читаються між батчами.
- **Delta catchup** (`CatchUpAsync(runId, watermark = now − Replay:WatermarkLag 60 с)`): розширює `scope.to` до watermark і скидає `done` — publisher
  дочитує raw, що надійшли після старту (у межах sources). Повторюваний до/після promote (після promote generation = active → пізні raw пишуться
  в неї).

### D3. Verify / promote / rollback (atomic switch)

- `VerifyAsync(runId, actor, reason)`: умови — `checkpoint.done`, 0 pending deliveries подій run'а (`messaging.events.processing_run_id` ⋈ deliveries
  `outcome IS NULL`), 0 quarantined; звіт `verification` у `runs.scope`: raw у scope / опубліковано / stage_results per outcome / observations /
  incidents у generation vs incidents active generation у тому ж вікні; невиконана умова → 409 з причиною. Успіх → `verified`, `generations.verified_by`.
- `PromoteAsync` (лише з `verified`): одна tx — `UPDATE generations SET is_active=false WHERE is_active` (запамʼятати `promoted_from` у scope),
  `is_active=true, promoted_at` для generation run'а, run → `promoted`; audit. Live writers пишуть у **active** generation
  (`IncidentStateWriter.EnsureGenerationAsync`: `run.generation_id ?? active ?? LiveGeneration`) → після promote нові live incidents — у promoted generation;
  read-side автоматично показує нову generation (`IncidentQueries.Active`); projection: `incident.changed` з неактивної generation → noop (є).
- `RollbackAsync` (з `promoted`): active ← `promoted_from`, `rolled_back_at`, run → `rolled_back`; audit. Інциденти обох generations лишаються (нічого не
  видаляється — ADR-0005 «результати не перезаписуються»).
- Partial replay (scope вужчий за всю історію): promote перемикає **всю** active generation; несумісні з promote сценарії (scope не покриває вікно
  read-side) — verify показує `active_incidents_outside_scope` і UI попереджає; explicit merge — після даних (ADR).

### D4. Isolated lanes: topology v9

- `incident-worker.lanes += replay` (v9); `raw.stored`/`observations.recorded` у replay lane → normalizer/parser/finalizer/incident-worker/archive.
  Track/alert-worker і projection без replay lane (без live effects). `ExpectedSubscriptions` уже фільтрує за lane.
- `SubscriptionConsumer`: prefetch per lane (`PrefetchByLane`), інакше як є.

### D5. Admin API + UI

- `GET /api/admin/ops/runs` (усі runs з generation/state/checkpoint/verification), `POST /api/admin/ops/runs/replay` (`{sourceIds?, from, to, actor,
  reason}`), `POST /{id}/start|pause|resume|cancel|catchup|verify|promote|rollback` (`{actor, reason}`; 400/404/409). Панель «Replay» (`ReplayPanel.tsx`,
  `api/adminRuns.ts`): таблиця runs, форма створення, кнопки з actor/reason і підтвердженням («promote перемикає active generation для всіх incidents;
  rollback повертає generation X»), прогрес checkpoint, звіт verify. Playwright A05.

### D6. Тести

| # | Сценарій | Доказ |
|---|---|---|
| R01 | Messaging.Tests: live обробляє 2 raw (incidents у live generation, NOTIFY/projection як зазвичай); replay run scope = ці raw → publisher публікує в replay lane → normalizer/parser/finalizer/incident-worker (replay) створюють incidents у **новій** generation; live incidents/revisions незмінні; projection deliveries для replay подій відсутні (0 `IncidentChanged` NOTIFY); track-worker не отримує replay | shadow без effects |
| R02 | pause після 1-го батчу (BatchSize 2 з 5 raw) → checkpoint.published = 2; resume → 5, без дублів `raw.stored` (outbox per run per raw = 1); cancel зупиняє; crash-повтор батчу → stage_results unique → noop | checkpoints resumable |
| R03 | verify (усі deliveries terminal) → promote → `IncidentQueries` повертає incidents нової generation, старі не видно; новий live raw після promote → incident у promoted generation; rollback → старі видно, нові ні; promote з `running` → 409 | atomic switch, rehearsed rollback |
| R04 | raw, що надійшов у live під час replay → після catchup (watermark) опублікований у replay run, verify зелений | delta catchup |
| R05 | supersede: `GetOpenRunAsync` з новим pipeline_version → старий run `superseded`, новий з `supersedes_run_id`; unit `RunService` переходи (CAS 409) | state machine |
| R06 | Contracts: topology v9, incident-worker має replay lane; replay lane `observations.recorded` → expected {archive, incident-worker} (без track/alert) | contracts |
| A05 | Playwright: панель Replay — створення, promote disabled поки не verified, підтвердження називає generation, verify-звіт видно | UI |

### D7. Документація

ADR-0005 (status accepted для orchestration-частини: state machine, checkpoint format, catchup, promote/rollback, межі tracks/alerts), ADR-0009/0010
(replay lane incident-worker, generation = active), ADR-0011 (read-side після promote), README контрактів (v9, `raw.stored` від `replay`), `docs/README.md`
(endpoints), `fork-deployment.md` (роль `replay`, `Replay:*`, `PrefetchByLane`, rollout/rollback), plan §16.1/§17, evidence `P14-replay-evidence.md`,
handoff, manifest.

## Порядок

1. Topology v9 + prefetch per lane + `EnsureGenerationAsync` active. 2. `RunService` + supersede + publisher + catchup (R01/R02/R04/R05). 3. verify/promote/
rollback (R03). 4. Admin API + UI + A05. 5. Docs, review, handoff, commit (явний перелік), push.

## Ризики / межі

- Tracks/alerts без generation — replay їх не торкається і не ізолює (документовано; P16/після даних).
- Promote «всієї» generation при partial scope — verify показує, UI попереджає; explicit merge/replacement scope — не в P14.
- Вікно між останнім catchup і promote: raw, оброблені live у старій generation, невидимі після promote до повторного catchup (post-promote catchup
  дописує їх) — документовано в ADR.
- Legacy `ReprocessService.ResetAsync`/`ProcessingLoop` лишаються (cutover — P16).

## Незалежне review плану — `p14_review` (approve after fixes)

| # | Finding | Рішення |
|---|---|---|
| B1 | Replay-lane incident-worker іде live-шляхом: `already_written`/`legacy_owned` → noop для кожного raw, який live уже матеріалізував; при обході — вставка `targets`, `TargetCreated` NOTIFY, тригери | Окрема shadow-гілка в `IncidentWriterHandler` для lane `replay`: без Store/legacy/already-written guards, без `targets`, без `LinkObservations`, без NOTIFY; лише `incidents`/`incident_observations`/`incident_revisions` у generation run'а під `incident:kind` locks; ідемпотентність — `(raw_message_id, generation_id)` guard + observation link; `legacy_target_id = null`. R01 перевіряє `count(targets)` незмінний, NOTIFY 0 з positive control |
| B2 | `FOR SHARE` на active generation не race-free з promote (EvalPlanQual → порожньо → LiveGeneration) | Advisory lock `generation:active`: writers shared, promote/rollback exclusive; тест на гонку (writer tx після read → promote → incident у promoted generation) |
| B3 | Catchup за `published_at` не бачить raw з `published_at` у вікні, збережених пізніше; post-promote catchup дублює incidents | Scope = `published_at ∈ [from,to]` ∧ `raw_message_id ≤ ingest_ceiling` (max id при create); keyset **за raw_message_id** (порядок ingestion, btree PK — закриває і N3); catchup підіймає `ingest_ceiling` до поточного max id і `to` до watermark → пізні raw зі старим `published_at` теж потрапляють; дублікати у promoted generation блокує `(raw, generation)` guard (B1). R04 покриває late raw зі старим published_at |
| N1 | Supersede «хитається» між версіями у mixed-version window | Supersede лише якщо відкритий run старший за старт процесу (`created_at < process start`) і версія інша; інакше — прийняти відкритий run. R05: старий процес після нового run приймає його |
| N2 | Verify через `messaging.events.processing_run_id` (seq scan) і залежить від archive | Pending = `deliveries lane='replay' AND outcome IS NULL` + `outbox lane='replay' AND confirmed_at IS NULL` (lane належить лише replay run'у — N12); індекс `messaging.events(processing_run_id)` додано для звіту; R03 чекає verify через `WaitUntil` |
| N3 | Keyset `(published_at, raw_message_id)` без btree | Keyset за PK (B3) |
| N4 | Publisher: будь-яка помилка → `failed` без CAS, `failed → running` відсутній | Помилка батчу → `checkpoint.error` з CAS `WHERE state='running'`, run лишається running; після `Replay:MaxBatchFailures` (5) поспіль → `failed` (CAS); `failed → running` через resume (audit) |
| N5 | Cancel/pause не зупиняють in-flight replay deliveries | Документовано (drain семантика); incident-worker replay-гілка → `noop run_cancelled`, якщо run `cancelled`/`rolled_back` |
| N6 | Rollback: вікно `[promoted_at, rolled_back_at]`, `promoted_from = null` | Rollback відмовляє без `promoted_from`, якщо `LiveGeneration` відсутня; audit фіксує кількість incidents, записаних у promoted generation за вікно; ADR-0005 описує recovery (новий replay вікна) і fencing §11.6 |
| N7 | Admin incidents/review queue без фільтра generation | `GET /api/admin/incidents` і `/review` — лише active generation (default), `?generation=all` показує shadow з позначкою |
| N8 | Unit pin v8, `producer_roles.replay` відсутній | Оновлено pin; `producer_roles.replay {emits: [raw.stored]}`; T04 приймає secondary producer |
| N9 | R01 NOTIFY без listener; R02 timing; R04 watermark | R01 — `PgNotifyListener` з positive control (live) і 0 під час replay; R02 — `PublishOnceAsync` напряму; R04 — явний watermark |
| N10 | Replay без LLM → після promote LLM-only incidents зникають | Звіт verify: `active_incidents_missing_in_generation` (за raw); UI показує перед promote; ADR-0005 |
| N11 | Ролі/DI/compose/опції не перелічені | `WorkerOptions.Replay`, `AddPulujMessaging(ReplayRole)`, `Replay:*` у fork-deployment; publisher потребує `relay` |
| N12 | Кілька паралельних replay runs | Partial unique index: один не-термінальний replay run (`created|running|paused|verified`); create → 409 |
| N13 | Колізії з іншим агентом (`Dtos.cs`, `PublicCatalogQueries.cs`, web public) | Явний перелік файлів у commit; ці файли не чіпаються |
| Q1 | History run `completed` | Відкладено (P16), ADR-0005 «Відкрите» |
| Q2 | Пункти інших ADR, призначені P14 (projection checkpoint, processing_status backlog, backfill forecast) | Таблиця «Відкладено з P14» в ADR-0005 з власниками (P15/P16); рядки ADR-0009/0011/0012 оновлено |
| Q3 | Promote всієї generation при partial scope | Hard gate: promote → 409, якщо `active_incidents_outside_scope > 0`, крім `force` з reason (audit); зафіксовано в ADR-0005 як відхилення від §11.5 |
| Q4 | Формати checkpoint/scope | ADR-0005 описує контракт (`{published,total,lastRawMessageId,lastPublishedAt,ingestCeiling,done,error,failures}`, `scope.{source_ids,from,to,ingest_ceiling,catchup_from,verification,promoted_from}`) |
| Q5 | Verify лише за deliveries | + `unanalyzed = published − extractions(run)` → gate 0 |
| Q6 | Обсяг | Відкладення з власниками: tracks/alerts generations (P16), partial merge (після даних), history completed (P16), legacy reset cutover (P16) |
