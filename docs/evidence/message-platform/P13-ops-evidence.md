# P13 — ops metrics/health, runtime controls per lane, message explorer: evidence

Task: [P13 / issue #16](https://github.com/sql-monk/Puluj-g/issues/16). Base `3b35e3b`. План і рішення review: [`P13-plan.md`](P13-plan.md); ADR:
[ADR-0012](../../adr/ADR-0012-ops-controls.md). Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, Testcontainers `postgis/postgis:17-3.5` +
`rabbitmq:4.3-management`, Node 22 / Playwright 1.58.2 / Chromium headless shell 145. TRX — `test-results/p13-*.trx`; JSON-записи тестів —
`messaging-crash-evidence.json` (`P13-O01…O04`).

## Що доведено (gate issue #16)

| Gate | Доказ |
|---|---|
| Видимий offline/stuck worker | O03: heartbeat `ghost` −5 хв + running attempt `parser@ghost` → `workers[ghost].stale=true, stuck=true, runningAttempts=1` і alarm `stale_heartbeat_with_jobs@worker:ghost`; A03: воркер з `stuck` показано словом «ЗАВИС» з останньою помилкою повністю |
| Коректні counters | O03: `raw-writer/live.completedHour == count(deliveries completed)` = 3, `normalizer/live.pending == count(outcome IS NULL)` = 3, `quarantined` = 1, `parser/live.inFlight` = 1, `roots.receivedHour` = 3, `needsAttention` = 1; wait/processing p50/p95 обчислені |
| Коректний scope контролів | O01: пауза `raw-writer/history` → `ConsumerCount(history)=0`, `ConsumerCount(live)=1`, live-повідомлення оброблено, 3 history чекають (pending), resume → 5 raw; пауза live окремо → нове live чекає; A03: підтвердження містить «lane «history» підписки «parser»… Інші lanes цієї підписки та інші підписки не змінюються» |
| Drain | O02: `draining` → 4 повідомлення оброблені → lane сам `paused` (`actor=system`, `reason=drained`, audit `drained`), live-консюмер лишається |
| RBAC/audit | actor+reason обовʼязкові (400; кнопки disabled без них — A03); `control_audit` рядки: O01 `pause:history, pause:live, resume:history, resume:live`, O02 `drained`, C05 `retry`, C08 `waive` |
| Одна картка lifecycle | O04: raw → події (`raw.stored`) → deliveries archive completed з attempt succeeded, normalizer pending без attempts → `waiting=[normalizer/live]`, `completed=[archive/live]`, `completion=in_progress`; після карантину → `needs_attention`, `failed=[normalizer/live]`, помилка в картці; A04: помилка delivery повністю, текст як текст, `javascript:` не є посиланням |
| «0 ready при in-flight ≠ порожня» | O05 `inflight_stuck`; A03: рядок `parser/live` з in-flight не показує «порожня» |
| Paused lane не мовчить і не є помилкою | O03: `lane_paused` info з actor/reason, `required_consumer_missing` не спрацьовує; O05 усі alarms paused-lane = info |

## Прогони (команда → exit → результат)

| # | Команда | Exit | Passed / Failed / Skipped |
|---|---|---|---|
| 1 | `dotnet test tests/Puluj.Messaging.Tests --filter OpsControlTests` (O01–O04, run1) | 0 | **4/0/0** (8 с) |
| 2 | `dotnet test tests/Puluj.Admin.Tests` (O05 + решта; run1) | 0 | **91/0/0** (+10 AlarmRulesTests) |
| 3 | `dotnet test tests/Puluj.Integration.Tests --filter MessagingControlsMigrationTests` (O07) | 0 | **1/0/0** |
| 4 | `cd web; npx playwright test A03 --project desktop` run1 → run3 | 1 → 1 → 0 | 1/1 (uk-UA `1 234` — NNBSP у assert) → 1/1 (текст статусу) → **2/0** (A03, A04) |
| 5 | `cd web; npx tsc --noEmit -p tsconfig.app.json`; `-p tsconfig.e2e.json` | 0; 0 | — |
| 6 | `cd web; npx vitest run src/admin` | 0 | **44/0** |
| 7 | `dotnet build Puluj.sln` | 0 | 0 warnings |
| R1 | `dotnet test tests/Puluj.Messaging.Tests` (повний) | 0 | **90/0/1** (4 хв 37 с; +4 O01–O04, +2 assert audit у C05/C08) |
| R2 | `dotnet test tests/Puluj.Integration.Tests` (повний) | 0 | **42/0/1** (+1 O07) |
| R3 | `dotnet test tests/Puluj.Messaging.Contracts.Tests` | 0 | **61/0/0** |
| R4 | `dotnet test tests/Puluj.Processing.Tests` | 0 | **134/0/0** |
| R5 | `dotnet test tests/Puluj.Api.Tests` | 0 | **34/0/0** |
| R6 | `dotnet test tests/Puluj.Analytics.Tests` | 0 | **33/0/0** |
| R7 | `cd web; npx playwright test` (усі, desktop + mobile) | 0 | **19/0** (desktop 15 + mobile 4; 2,3 хв) |
| F1 | після review результату: `dotnet test Messaging.Tests --filter OpsControlTests\|CrashTests` run2 | 1 | 13/1 — O02: порожня черга «зливається» одразу (relay ще не опублікував; drain empty queue = paused за 2 poll'и) → тест чекає backlog у брокері **до** drain |
| F2 | `--filter OpsControlTests` run3 → run5 (3 прогони поспіль) | 0; 0; 0 | **4/0** ×3 (O04 тепер і waive карантину → `completed`) |
| F3 | `dotnet test tests/Puluj.Admin.Tests` run2 | 0 | **92/0** (+1 `Every_command_requires_actor_and_reason…`; `inflight_stuck` без умови «0 завершень») |
| F4 | `--filter MessagingControlsMigrationTests` run2 (міграція перегенерована: +`ix_processing_attempts_running`, `ix_processing_attempts_event`) | 0 | **1/0** |
| F5 | `npx playwright test A03 --project desktop` run4 | 0 | **2/0** |
| F6 | `dotnet test tests/Puluj.Messaging.Tests` (повний, run2) | 0 | **90/0/1** (5 хв 32 с) |

## O01 — pause scope (P13-O01)

- Стан `raw-writer/history = paused` записано **до** старту консюмера → консюмер не підписався на history (стан читається до першого `basic.consume`):
  `QueueDeclarePassive(puluj.raw-writer.history).ConsumerCount = 0`, `(live) = 1`; `RawWriter.Lanes()`: history `Paused/consuming=false`, live
  `consuming=true`, тег `raw-writer@p03-test:live`.
- 3 history-повідомлення лишились у черзі (`MessageCount = 3`, deliveries `outcome IS NULL AND lane='history'` = 3); live оброблено (raw_messages = 1).
- Пауза live під час споживання → `basic.cancel` на наступному poll (200 мс у fixture): `ConsumerCount(live) = 0`, нове live-повідомлення чекає в черзі.
- Resume обох → 5 raw_messages, 0 pending; консюмери 1/1; канал live перевикористано (той самий `IChannel`, новий consumer на ньому).
- Audit: `pause:history:operator:test, pause:live:operator:test, resume:history:operator:test, resume:live:operator:test`.

## O02 — drain (P13-O02)

`draining` до старту → консюмер споживає (4 raw), після 2 порожніх опитувань (`MessageCount=0 && inFlight=0`) → `UPDATE … WHERE state='draining'` →
`paused`, `actor=system`, `reason=drained`, audit `drained`; `ConsumerCount(history)=0`, `(live)=1`.

## O03 — snapshot і alarms (P13-O03)

Після 3 live-повідомлень через ingress → raw-writer → archive (normalizer не стартував): `raw-writer/live {completedHour 3, pending 0, waitP50, processingP95,
source db}`, `archive/live completed+noop == deliveries`, `normalizer/live {pending 3, oldestPendingAge > 0, quarantined 1}`, `parser/live {inFlight 1,
oldestRunning > 500 с}`, `roots {receivedHour 3, pending ≥ 1, needsAttention 1}`. Alarms: `stale_heartbeat_with_jobs@worker:ghost`, `dlq@normalizer/live`,
`required_consumer_missing@normalizer/live`, `inflight_stuck@parser/live`; жодного `lane_paused`. Після `SetLaneStateAsync(normalizer, live, paused)`:
`lane_paused` info з `operator:test: waiting for the parser fix`; `required_consumer_missing@normalizer/live` зник. Пороги у fixture: `RequiredConsumerMissingSeconds=0`,
`InflightStuckSeconds=0`, `SnapshotCacheSeconds=0`.

## O04 — lifecycle (P13-O04)

Пошук: текст «дрони» → 1 рядок (raw id, ключ `o04`); за id → той самий; «nothing-like-this» → 0. Картка: `raw.stored` з deliveries `archive`
(completed/noop, 1 attempt succeeded) і `normalizer` (pending, 0 attempts); summary `waiting=[normalizer/live]`, `completed=[archive/live]`, `in_progress`,
quarantine порожній. Після карантину normalizer: `needs_attention`, `failed=[normalizer/live]`, `quarantine[0].error = NullReferenceException: boom`;
невідомий id → null (404).

## O05 — правила (Admin.Tests `AlarmRulesTests`, 10 тестів)

Здоровий snapshot → 0 alarms; `backlog_growing` лише при pending>0 ∧ expected5m>0 ∧ completed5m=0; `oldest_age_slo` per lane (live 300 с / history 3600 с;
required → error, optional → warn); paused/draining → усі alarms lane'а info, `backlog_growing`/`required_consumer_missing` не спрацьовують; `required_consumer_missing`
потребує active lane, вік > 60 с, 0 консюмерів; `inflight_stuck` («0 ready, unacked ≠ порожня»); dlq / stuck worker / llm_paused / broker_disconnected /
broker_blocked (немає даних ≠ alarm); outbox warn/error/unroutable, reconciliation, roots; порядок error → warn → info; `MessageExplorer.Summarize`:
`needs_attention` / `pending` / `completed` (waived рахується завершеним) / `in_progress`.

## O07 — міграція

`AddMessagingControls` Down → таблиць `subscription_lanes`/`control_audit` і колонок `deliveries.lane/occurred_at` немає; Up → є разом з індексами
`ix_processing_deliveries_pending_lane`, `ix_processing_deliveries_completed_brin`; CHECK відхиляє `state='stopped'`.

## A03/A04 — UI (Playwright, замокане API)

A03: alarms з `ПОМИЛКА/УВАГА/ІНФО`, `lane_paused` з `ops: backfill window`; `worker-ghost` → «ЗАВИС» + `Npgsql.NpgsqlException: connection reset`;
`worker-messaging-1` показує `parser/history: пауза`; `lane-parser-live` (in-flight 1) не містить «порожня», `lane-raw-writer-live` — «порожня»;
`lane-parser-history` `data-state=paused`, кнопка «відновити» disabled без actor/reason; після заповнення — confirm-діалог з точним scope і pending
`1 234`; POST `/lanes/parser/history {state: active, actor, reason}`; пауза live → «lane «live» підписки «parser», pending 1, in-flight 1»; retry карантину →
«лише ця доставка», POST `/quarantine/5/retry`. A04: пошук → таблиця → картка `ПОТРЕБУЄ УВАГИ`, текст `<b>…</b><script>` як текст, 0 `<script>`,
0 посилань «оригінал» для `javascript:`; помилка normalizer з `line 42` повністю; `data-outcome=pending` → `parser/live`; summary «Чекають (1)» /
«Помилилися (1)»; `#/messages?raw=777` відкриває картку.

## Review результату (`p13_review`) → виправлення → перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | waive карантинної доставки лишав receipt `quarantined` (root назавжди `needs_attention`) | `WaiveAsync`: `outcome IS NULL OR outcome = 'quarantined'` → `waived` | O04 (waive → `completed`, `failed=[]`, resolution `waived`) |
| N1 | `unroutable` мертвий (`ILIKE '%unroutable%'` vs `basic.return …`) | `ILIKE 'basic.return%'` | код |
| N2 | подвійний allow-list scale | лише `Docker:ScalableServices` (DockerService → 400; 400 не аудитується) | F3 |
| N3 | actor/reason без обмеження довжини → 22001/500 | `Missing()` перевіряє ≤128/≤1000 (400); `SetLaneStateAsync` кидає ArgumentException | unit F3 |
| N4 | attempts без індексів для running/event | `ix_processing_attempts_running` (partial), `ix_processing_attempts_event` у тій самій міграції | F4 |
| N5 | `inflight_stuck` вимагав `completed5m == 0` (не за B3) | умова прибрана; plan B3/ADR оновлено | F3 |
| N6 | `_cached` — tuple поза gate (torn read) | `record Cached` (атомарне посилання) | код |
| N7 | lane з закритим каналом лишалась `consuming=true`; consume під час Crash() | `StartLaneAsync` перевідкриває при закритому каналі; `Consuming = channel.IsOpen` після consume | F2 |
| N8 | `#/messages?raw=N` лише при монтуванні | `hashchange` listener | tsc, A04 |
| N9 | O01 без resume у `finally` | `finally` повертає lanes raw-writer в active | F2 |
| N10 | RBAC/audit не перевірено на HTTP-рівні | unit валідації `Missing`; HTTP-harness для Admin — P16 (задокументовано) | F3 |
| Q1 | prefetched-but-undispatched і drain | ADR-0012 п.4 | docs |
| Q2 | Admin реєструє топологію → 500 при rolling upgrade | ADR-0012 «Наслідки», fork-deployment | docs |
| Q3 | `(envelope->>'raw_message_id')::bigint` на нечисловому значенні | `jsonb_typeof(...) = 'number'` guard | O04 |
| Q5 | `Summarize(events, registry)` — registry не використовувався | параметр прибрано | F3 |
| Q6 | `CREATE INDEX` без CONCURRENTLY | fork-deployment (секунди при поточних обсягах) | docs |
| Q8 | `UriFormatException` для кривого `ManagementUrl` | додано в catch | код |
| Q9 | extractions/observations на картці лише mock | evidence «Не покрито» | docs |
| Q4, Q7 | worker↔host на одному хості; inbox без індексів | прийнято (compose: один процес на контейнер; inbox retention 30 д) | — |

## Не покрито / межі

- Management API брокера (`Messaging:Broker:ManagementUrl`) не піднімався в тестах: код читає `/api/queues/{vhost}` і `/api/nodes`, недоступність → `available=false`
  з причиною (шлях перевірено лише через `Configured=false` у O03 → `source: db`). Live-перевірка — при deploy з `RABBITMQ_MANAGEMENT_URL`.
- `POST /ops/messaging/scale` — `docker compose up --scale messaging=N` не запускався (потребує стек); поза Docker endpoint відповідає 409 (перевірено логікою
  `DockerService.UnavailableAsync` → 503 → 409 у endpoint).
- Endpoints `MessagingOpsEndpoints` — тонкі обгортки над сервісами (O01–O04 тестують сервіси напряму; валідація actor/reason/довжин — unit
  `Every_command_requires_actor_and_reason…`); HTTP-контракт (401/400/409 через ASP.NET) перевірено Playwright над mock, не над .NET — Admin.Tests не має
  WebApplicationFactory-harness (P16).
- O04 доводить події → deliveries → attempts → summary/quarantine/waive; extractions/observations/похідні на картці покриті лише mock у A04 (normalizer/parser у
  O04 не стартують).
- Кілька реплік одного консюмера при drain: кожна знає лише свій in-flight (ADR-0012) — не тестувалось (одна репліка у fixture).
- Deliveries без `lane` (до міграції): pending додається до першого lane підписки — перевірено кодом, не тестом (fixture TRUNCATE-иться).
- Project card на GitHub не оновлено (токен без scope `project`).
