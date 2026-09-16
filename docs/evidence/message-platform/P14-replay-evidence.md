# P14 — run/generation orchestration, replay checkpoints, delta catchup, promote/rollback: evidence

Task: [P14 / issue #14](https://github.com/sql-monk/Puluj-g/issues/14). Base `6e183a5`. План і рішення review: [`P14-plan.md`](P14-plan.md); ADR:
[ADR-0005 «Orchestration (P14)»](../../adr/ADR-0005-runs-completion.md). Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, Testcontainers
`postgis/postgis:17-3.5` + `rabbitmq:4.3-management`, Node 22 / Playwright 1.58.2 / Chromium 145. TRX — `test-results/p14-*.trx`; JSON-записи —
`messaging-crash-evidence.json` (`P14-R01-R03-R04`, `P14-R02`, `P14-R05`, `P14-R06`).

## Що доведено (gate issue #14)

| Gate | Доказ |
|---|---|
| Live не зупиняється | R01: під час replay live-консюмери (усі ролі) працюють; після catchup/promote/rollback live-повідомлення (`r04`, `r04-late`, `r03-live`, `r03-after`) обробляються і потрапляють у ту generation, яка активна в момент запису |
| Shadow без production effects | R01: після replay 2 raw → 2 incidents у **новій** generation; live incidents і їхні revisions незмінні (`liveRevisions` рівність), `count(targets)` незмінний, `PgNotifyListener` (positive control: live дав ≥2 NOTIFY) — 0 нових NOTIFY за весь replay і catchup, 0 deliveries lane replay для projection/track-worker/alert-worker/raw-writer, `llm_requests` незмінно, `incident_observations.legacy_target_id IS NULL` у generation |
| Atomic switch | R01: verify → promote → `processing.generations` рівно одна active (= generation run'а), `IncidentQueries.Active`-предикат дає 4 incidents нової generation і 0 старої; нові live incidents → у promoted generation; NOTIFY для них є (проєкція бачить active). R06: promote чекає на writer, що тримає `generation:active` shared (≥1,5 с), наступний writer бачить promoted generation |
| Відрепетируваний rollback | R01: rollback → active = live generation, run `rolled_back`, обидва набори incidents на місці (нічого не видалено), audit `incidentsWrittenSincePromote = 1`; повторний rollback → 409; новий live raw після rollback → у відновленій generation |
| Checkpoints resumable, pause/cancel | R02: BatchSize 2 із 5 raw → checkpoint `2/5` після 1-го батчу; pause → publisher нічого не публікує; resume → `5/5 done`; кожен raw опублікований рівно раз; rewind checkpoint (crash/backup) → повторний батч публікує 2 raw з новими event id → normalizer `noop` ×2, `stage_results` per (raw, run) = 5; cancel → publisher не бере run, resume зі cancelled → 409 |
| Delta catchup | R01/R04: свіжий raw і raw зі старим `published_at` (у вікні, збережений після створення run'а) невидимі для run'а до `CatchUpAsync` → total 2→4, обидва replayed у generation; verify: `pending 0`, `unanalyzed 0`, `missing_in_generation 0`, `outside_scope 0` |
| Один run, state machine | R01: promote з created → 409, pause з created → 409, другий replay run при відкритому → 409, після terminal — можна; audit `run:create,start,catchup,verify,promote,rollback` |
| Supersede live run | R05: новіша `pipeline_version` → старий run `superseded` (`finished_at`), новий `running` з `supersedes_run_id`; старий процес після новішого run приймає його (без хитання), ще новіший процес supersede'ить |

## Прогони (команда → exit → результат)

| # | Команда | Exit | Passed / Failed / Skipped |
|---|---|---|---|
| 1 | `dotnet test Messaging.Tests --filter ReplayTests` run1 | 1 | 0/3 — R01: `incident-worker/replay=noop:2` (live guards `already_written` — review B1), R02: `lastPublishedAt` формат у rewind, R05: `DateTimeOffset?` cast |
| 2 | run2 (після B1 shadow-гілки, B3 keyset за PK, N1 supersede) | 1 | 2/1 — R01: NOTIFY-лічильник (live post дає 2 NOTIFY: `TargetCreated` + `IncidentChanged`) — assert перероблено на «catchup не сповістив нікого» |
| 3 | run3 | 0 | **3/0** |
| 4 | `--filter ReplayTests.R06` run4 | 1 | 0/1 — live generation ще не існувала до першого incident'а (тест створює її явно) |
| 5 | `--filter ReplayTests` run5 | 0 | **4/0** (R01/R03/R04, R02, R05, R06; 19 с) |
| 6 | `dotnet test Messaging.Contracts.Tests` (v9, `producer_roles.replay`) | 0 | **61/0** |
| 7 | `cd web; npx playwright test A05 --project desktop` run1 → run2 | 0; 0 | 1/0; **1/0** |
| 8 | `cd web; npx playwright test` (усі) | 0 | **20/0** (desktop 16 + mobile 4; 1,4 хв) |
| 9 | `cd web; npx vitest run src/admin`; `tsc` app + e2e | 0 | **44/0**; — |
| 10 | `dotnet build Puluj.sln` | 0 | 0 warnings |
| R1 | `dotnet test tests/Puluj.Messaging.Tests` (повний) | 0 | **94/0/1** (6 хв 1 с; +4 ReplayTests) |
| R2 | `dotnet test tests/Puluj.Integration.Tests` | 1 | 42/1/1 — `PipelineTests.Raw_messages_become_targets_tracks_and_revisions` («більше одного track»): паралельна задача закомітила `PublicCatalogQueriesTests` (`f6bb518`), який виконується раніше й лишає `target_tracks`; сам тест наодинці — **1/0** (run3); P14 legacy-pipeline не торкається |
| R3 | `dotnet test tests/Puluj.Messaging.Contracts.Tests` | 0 | **61/0** |
| R4 | `dotnet test tests/Puluj.Processing.Tests` | 0 | **134/0** |
| R5 | `dotnet test tests/Puluj.Api.Tests` | 0 | **35/0** |
| R6 | `dotnet test tests/Puluj.Analytics.Tests` | 0 | **33/0** |
| R7 | `dotnet test tests/Puluj.Admin.Tests` | 0 | **92/0** |
| F1 | після review результату: `--filter ReplayTests\|TopologyRegistryTests` run6 | 0 | **16/0** (cancel з verified, catchup лише до promote, verify з вікном run'а, unanalyzed за distinct raw) |
| F2 | `npx playwright test A05 --project desktop` run3 (force checkbox) | 0 | **1/0** |
| F3 | `dotnet test tests/Puluj.Messaging.Tests` (повний, run2) | 0 | **94/0/1** (5 хв 33 с) |
| F4 | `dotnet test tests/Puluj.Admin.Tests` run2 | 0 | **92/0** |

## R01/R03/R04 (P14-R01-R03-R04)

Live: `Вибухи у Харкові.` / `Вибухи у Полтаві.` → 2 incidents (live generation `UUIDv5(generation:live)`), projection deliveries ≥ 2, listener ≥ 2 NOTIFY.
Replay run (scope 60 хв, усі джерела): `created`, `total 2`, generation неактивна; start + publisher → normalizer/parser/finalizer/incident-worker у replay
lane → 2 incidents generation B; `stage_results normalize` = 2, `extractions(run)` = 2, `messaging.events lane replay raw.stored` = 2; incident-worker replay
= 2 `completed`. R04: 2 пізні raw → catchup(watermark now) → `total 4`, 4 incidents у B, listener незмінний з моменту live-постів. Verify:
`incidents_in_generation 4, active_incidents_in_window 4, pending 0, unanalyzed 0, missing 0, outside 0` → promote → active = B, 4 active incidents, старі
4 у live generation залишаються; live `r03-live` → 5 у B, NOTIFY є; rollback → live active, 4 active, B має 5, audit `incidentsWrittenSincePromote 1`;
`r03-after` → у live generation (5). Другий replay run створюється лише після terminal стану першого.

## R02 (P14-R02)

5 raw (SQL), run BatchSize 2: `PublishOnceAsync` × (1 → 2/5) → pause → false → resume → ×2 (5/5) → false (done). Outbox `lane replay raw.stored` = 5,
distinct raw = 5. Rewind checkpoint на raw #2 → повторний батч → 7 рядків outbox; relay + normalizer + archive → normalizer receipts 7 (5 completed +
2 noop), `stage_results normalize(run)` = 5. Cancel → `cancelled`, publisher не бере, resume → 409.

## R05, R06

R05 — див. gate-таблицю. R06: порожній scope → `done` одразу → verify → `verified`; writer A тримає tx після `EnsureGenerationAsync(null)` (= live);
`PromoteAsync` не завершується 1,5 с і active лишається live; commit A → promote завершено, active = B; writer B у новій tx отримує B; rollback → live.

## A05 (Playwright, mock)

Runs: verified (checkpoint `1 200/1 200 ✓`), failed з помилкою `57014 … statement timeout` повністю і кнопкою «продовжити», live run. «активувати» лише у
verified run'а і disabled без actor/reason; звіт verify з `active_incidents_missing_in_generation`; підтвердження promote називає generation і лічильники
(`у generation 57 incidents`, `відсутні в generation 3`), без FORCE; POST `/promote {actor, reason}`; 409 сервера показано як є; create → `{sourceIds [1,2],
actor, reason}`.


## Review результату (`p14_review`) → виправлення → перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | `WorkerOptions.AllRoles` без `replay`, але роль у дефолтних `Worker__Roles` → `messaging` не стартує | `Replay` додано в `AllRoles` | build; `RoleSet` |
| B2 | Catchup після promote — мертвий шлях (publisher бере лише `running`), а запис у active generation через replay lane не дійшов би до карти | Catchup дозволено лише до promote (running/paused/verified); ADR/fork-deployment/UI: останній catchup — перед promote; вікно між ними — новий replay | R01: catchup після rollback → 409 |
| N1 | Cancel неможливий з `verified`/`failed` → verified run блокує єдиний слот | `CancelAsync` з created/running/paused/verified/failed; A05 409-текст оновлено | R01 (другий run: verify → cancel) |
| N2 | Verify/backpressure рахували всю lane replay (залишки попередніх runs) | Обмежено `expected_at/created_at >= run.created_at`; quarantined так само | R01 (другий run verify зелений після першого) |
| N3 | `datetime-local` парсився як локальний, підпис «UTC»; сервер міг отримати offset ≠ 0 | UI: підпис «локальний час», `localInput()`; `RunService`: `ToUniversalTime()` для scope і watermark | tsc, A05 |
| N4 | `force` ставився автоматично → gate = лише діалог | Явний checkbox «force promote»; підтвердження з FORCE; `?force=true` у запиті | A05 |
| N5 | R01: NOTIFY-лічильник знімався до кінця burst'у live-поста (flake) | `MapListener.QuietCountAsync()` — знімок після 1 с тиші | R01 run6 |
| N6 | fork-deployment: «v8 відмовить за hash» — неправда | Описано реальну поведінку (v8 не читає replay-чергу → verify червоний, без хибних ефектів) і порядок деплою | docs |
| N7 | Unit-тест не перевіряв incident-worker у replay lane | Додано assert `["archive","incident-worker"]` і `incident.changed` replay → лише archive | Unit run6 |
| N8 | Rollback-audit за `first_reported_at` | `created_at` | R01 (`incidentsWrittenSincePromote = 1`) |
| N9 | `unanalyzed = published − extractions` ламався після rewind checkpoint | `distinct raw (messaging.events run) − extractions` | R01 verify |
| Q4 | Rollback без `promoted_from` і без live generation → run застряг | Live generation створюється (`ON CONFLICT DO NOTHING`) і активується | код |
| Q6 | Post-promote shadow-запис невидимий для карти | Закрито разом із B2 (catchup лише до promote) | — |
| Q7 | Evidence/handoff/manifest | Створено | — |
| Q8 | Supersede: годинники хостів, рестарт старої збірки | ADR-0005 (прийнято, задокументовано) | docs |
| Q1–Q3, Q5 | «live keeps running» не одночасно з replay; failure path publisher'а без тесту; вартість keyset/індексу events; advisory-lock пауза writers | Документовано в «Не покрито» / ADR (bounded pause = найдовша in-flight tx writer'а) | — |

## Не покрито / межі

- Tracks/alerts: replay їх не будує (без `generation_id`); shadow доведено для incidents; треки/тривоги live-шляху не змінюються (replay lane їх не досягає).
- `force` promote для partial scope — логіка (`active_incidents_outside_scope > 0` → 409 без force) не має інтеграційного тесту з фактичними incidents поза
  scope (unit-логіка проста; A05 перевіряє лише відсутність FORCE у підтвердженні).
- Publisher failure → `failed` після `MaxBatchFailures` — не відтворено помилкою БД (шлях коду; R02 покриває checkpoint/CAS).
- «Live не зупиняється» доведено послідовно (live до/після replay, під час catchup), не одночасним навантаженням; advisory lock `generation:active`
  означає bounded-паузу live-writers на час найдовшої in-flight tx під час promote/rollback.
- `CREATE INDEX` `ix_messaging_events_run` без CONCURRENTLY (write lock на archive на час побудови); keyset за PK з фільтром `published_at` для
  вузького вікна над великою таблицею проходить PK-діапазони до `ingestCeiling`.
- Кілька реплік publisher'а (`FOR UPDATE SKIP LOCKED`) — одна репліка у fixture.
- HTTP-рівень `RunEndpoints` (400/404/409) — Playwright над mock; Admin.Tests без WebApplicationFactory (P16).
- LLM-only incidents після promote зникають (replay без моделі) — видно у verify (`missing_in_generation`), не тестовано з реальним LLM-шляхом.
- Project card — токен без scope.
