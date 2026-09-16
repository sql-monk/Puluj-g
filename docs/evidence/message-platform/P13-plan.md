# P13 — моніторинг черг/воркерів, message explorer, admin controls (§9, §8.7) — план

Task: [P13 / issue #16](https://github.com/sql-monk/Puluj-g/issues/16). Залежності: мінімум — P03 (receipts/reconciliation/quarantine), domain controls — P10 (incidents
admin), P12 (Admin UI harness, audit-патерн) — done. Base commit: `3b35e3b`. Gate: видимий offline/stuck worker, коректні counters/scope; RBAC/audit; одна
картка всього lifecycle.

## Стан коду на старті (звірено)

- Є: `processing.deliveries` (expected/outcome/reason/attempt), `processing.attempts` (state, worker, started/finished, error, retry), `processing.quarantine`
  (P03), `messaging.outbox/inbox/events/event_links`, `messaging.subscriptions` (status active|paused|retired + waiver, **лише на рівні підписки**),
  `SubscriptionAdmin` (`RetryAsync` quarantine, `WaiveAsync`, `SetStatusAsync` — записує статус, але **consumer статус не читає**: «пауза» зараз впливає лише
  на expected set/declare, не на споживання), `ReconciliationService.RunOnceAsync` → `Report` (overdue, unknown, quarantine open, outbox oldest age),
  heartbeats/`WorkerStatusDto` (`Runtime:Worker:{name}:Status` кожні 10 с: roles, version, cpu/mem, processing, llm), `OpsEndpoints` (overview/workers/
  pipeline/llm/db/containers/scale processors), `DockerService.ScaleAsync` (лише `processor`), RabbitMQ management UI на :15672 (compose), `PulujMetrics`
  (`puluj.writer.*`, `puluj.processing.*`), `LlmBreaker` (paused_until/reason). Admin UI: панелі Стан/Воркери/Конвеєр/… (P12 додав Каталог/Інциденти).
- Немає: per-subscription×lane метрик (ready/unacked/in-flight/retry/quarantined/oldest age/event-time lag/p50-p99), runtime pause/drain per lane,
  alarms, message explorer (одна картка lifecycle), audit контролів, broker health у панелі, outbox/inbox панелі.
- Паралельна робота в `web/` (public UI) — P13 торкається лише `web/src/admin/*`, `web/src/api/admin*.ts`, `web/e2e` (поза їхнім diff).

## Рішення

### D1. Runtime controls з точним scope (`messaging.subscription_lanes` + audit)

- Нова таблиця `messaging.subscription_lanes (subscription_id, lane, state active|paused|draining, reason, actor, changed_at)` (PK (subscription_id, lane);
  міграція `AddMessagingControls`, additive) + `messaging.control_audit (audit_id, action, subscription_id, lane?, actor, reason, at, details jsonb)` — кожен
  pause/resume/drain/retry/waive/scale лишає рядок; RBAC = існуючий `X-Admin-Token` (ролі — не в P13, документ ADR).
- `SubscriptionConsumer` кожні `ControlPollSeconds` (5 с) читає стан своїх lanes: `paused` → `BasicCancel` consumer-tag lane'а (in-flight доробляється — ACK
  після commit), `active` → `BasicConsume` знову; `draining` → консюмер продовжує, поки `QueueDeclarePassive(queue).MessageCount == 0` і немає in-flight, тоді
  сам переводить lane у `paused` (audit `drained`, actor `system`). Стан lane видно у `WorkerStatusDto.Consumers[] {subscription, lane, state, inFlight,
  prefetch, consumerTag}` (нове поле, additive). Scope UI: «history lane підписки parser призупинено (actor, reason); live працює».
- `SubscriptionAdmin` +`SetLaneStateAsync(subscription, lane, state, actor, reason)`; existing `SetStatusAsync` (subscription-level, планове paused/retired)
  лишається для registry.
- Scale: `POST /api/admin/ops/messaging/scale {service: messaging|processor, replicas}` через `DockerService` (розширити `ScaleAsync(service, replicas)`); без
  Docker → 409 «unavailable» (не імітуємо).
- Retry/DLQ: `POST /api/admin/ops/messaging/quarantine/{id}/retry` (`RetryAsync` P03) і `POST …/waive` — з actor/reason + audit; список quarantine з фільтрами.

### D2. Ops metrics/health (`GET /api/admin/ops/messaging`)

- **Підписки × lanes** (з БД, джерело правди — receipts): `pending` (deliveries без outcome), `inFlight` (attempts running), `retry` (attempts failed/interrupted з
  retry_of за годину), `quarantined` (open), `oldestPendingAge` (now − min expected_at pending), `eventTimeLag` (now − min occurred_at pending через
  `messaging.events`), `waitP50/P95/P99` (attempt.started_at − delivery.expected_at, година), `processingP50/P95/P99` (finished − started, година),
  `completedHour`/`noopHour`/`failedHour`, `state` lane (D1), `required/status` з registry, `consumers` (з heartbeat-статусів воркерів, чиї roles містять
  підписку і lane state active).
- **Broker**: `BrokerConnection.IsConnected` + опційний management API (`Messaging:Broker:ManagementUrl`, user/password ті самі): per queue `messages_ready`,
  `messages_unacknowledged`, `consumers`; `/api/nodes` — `mem_alarm`, `disk_free_alarm`, `running`; недоступний → `broker.management = null` з причиною (не
  вигадуємо). «0 ready при великому unacked» → alarm, не «черга порожня».
- **Outbox/inbox**: unconfirmed count + oldest age, relay retry count (attempts > 1), inbox duplicates за годину (`processing.deliveries` outcome duplicate або
  consumer counters?) — з `messaging.inbox` (кількість рядків за годину vs events), reconciliation — останній `Report` (кеш у `ReconciliationService.LastReport`,
  нова властивість) + `overdue`, `unknownSubscriptions`.
- **Воркери**: як `/ops/workers` + `Consumers[]` (D1), `lastSuccess`/`lastError` per worker з attempts, `prefetch`; `stale` = heartbeat > 45 с; **stuck** =
  stale heartbeat при running attempts цього worker (гейт issue).
- **Alarms** (pure `AlarmRules.Evaluate(snapshot, slo)` → список `{code, severity, scope, message, since?}`): `backlog_growing` (pending ↑ за 5 хв і 0 completions),
  `oldest_age_slo` (oldestPendingAge > `Ops:Slo:OldestAgeSeconds` per lane, default live 120 с / history 3600 с), `required_consumer_missing` (active
  required підписка без жодного живого консюмера lane'а `live`), `stale_heartbeat_with_jobs` (stuck worker), `dlq` (quarantine open > 0), `outbox_stuck` (oldest
  unconfirmed > 300 с), `llm_paused` (breaker), `reconciliation_mismatch` (overdue/unknown > 0), `unacked_not_empty` (ready 0, unacked > prefetch×consumers).
  SLO-пороги — `Ops:Slo` config (ADR-0007 орієнтири), кардинальність labels — subscription/lane/code.
- Backfill-рядок (§9.1) — джерело/діапазон/checkpoint з `collector_states` (є) — read-only.

### D3. Message explorer (`GET /api/admin/messages`, `/api/admin/messages/{rawId}/lifecycle`)

- Пошук: `q` (source_message_key / raw id / text ILIKE, обмежено 200 символів), `sourceId`, `hours` (≤ 720), `limit` ≤ 100 → рядки raw (id, source, key,
  published/received, status, extractions count, facts count, last delivery outcome).
- Lifecycle однієї картки: raw (+text, url — admin), ingress event (`messaging.events` за raw_message_id: `ingress.received`, `raw.stored`, …), кожна подія з
  усіма deliveries (subscription, lane, expected_at, outcome, reason, attempts: worker, started/finished, state, error), extractions (run, outcome, versions),
  observations (kind, category), похідні: tracks (через `target_tracks`/`track_targets`), alerts (`air_alerts` за raw), incidents (`incident_observations` за
  observation_id), quarantine рядки; підсумок «хто чекає / хто завершив / хто помилився» (`waiting[]`, `completed[]`, `failed[]`) і `completion` за
  `completion-manifest.json` (business-branch done ≠ ACK).

### D4. Admin UI

- Панель «Черги» (`web/src/admin/QueuesPanel.tsx`): таблиця підписка×lane (state, pending, in-flight, retry, quarantined, oldest age, lag, p50/p95/p99, consumers,
  completed/h), блок alarms зверху (severity, scope, message), broker/outbox/inbox/reconciliation картки, воркери з Consumers (stale/stuck позначені), дії
  pause/resume/drain per lane, retry/waive quarantine, scale (actor/reason обов'язкові, кожна дія показує точний scope у підтвердженні).
- Панель «Повідомлення» (`web/src/admin/MessagesPanel.tsx`): пошук + картка lifecycle (timeline подій → deliveries, факти, агрегати; помилки повністю; текст —
  лише текст).
- Polling 10 с; кольори не єдиний носій (текстові стани).

### D5. Тести

| # | Сценарій | Доказ |
|---|---|---|
| O01 | Messaging.Tests: pause lane `history` для `normalizer` через `SetLaneStateAsync` → consumer скасовує consumer-tag (management або `QueueDeclarePassive.ConsumerCount` = 0 для history, live продовжує), повідомлення в history чекають (pending росте), resume → доробляє; audit рядки; `WorkerStatus.Consumers` показує стан | scope pause |
| O02 | Messaging.Tests: drain → після спорожнення черги lane сам стає `paused` (audit `drained`, actor system) | drain |
| O03 | Messaging.Tests: ops snapshot після pipeline-прогону (P05-подібний): per-subscription counters збігаються з `processing.deliveries`/`attempts` (pending/completed/inFlight), p50/p95 обчислені, `oldestPendingAge` для навмисно непідтвердженої delivery; stuck worker: heartbeat-статус з `At` −5 хв і running attempt → `stale_heartbeat_with_jobs`; required consumer без heartbeat → `required_consumer_missing`; quarantine → `dlq`; 0 ready/large unacked → `unacked_not_empty` | counters/alarms |
| O04 | Messaging.Tests: lifecycle картка raw після повного проходу (ingress → raw → parse → observations → track/incident): усі події, deliveries з outcome, факти, агрегати; `waiting/completed/failed`; quarantine у картці | explorer |
| O05 | Admin.Tests: `AlarmRules.Evaluate` (pure) — кожне правило + «0 ready при unacked ≠ порожня» + SLO per lane; explorer summary builder | unit |
| O06 | Playwright A03: панель «Черги» над замоканим API — alarms видимі, scope pause у підтвердженні («history lane parser»), дії disabled без actor/reason, stuck worker позначений; A04: «Повідомлення» — пошук → картка, помилка delivery видима повністю, текст як текст | UI |
| O07 | Integration: міграція `AddMessagingControls` Up/Down; audit рядок за кожною дією | schema |

### D6. Документація

`ADR-0012-ops-controls.md` (scope контролів per lane, drain-семантика, alarms/SLO, management API як опційне джерело, RBAC = token (ролі — P16), audit),
ADR-0004/0007 (посилання), README контрактів (`WorkerStatusDto.Consumers`), `docs/README.md` (admin endpoints, панелі), `fork-deployment.md`
(`Messaging:Broker:ManagementUrl`, `Ops:Slo`), plan §16.1 (команди O01–O07/A03) / §17; evidence `P13-ops-evidence.md`, handoff, manifest.

## Порядок

1. Схема + `SubscriptionAdmin.SetLaneStateAsync` + consumer control loop + `Consumers` у status (O01/O02). 2. Ops snapshot + alarms (O03/O05). 3. Explorer (O04).
4. UI + A03/A04. 5. Docs, review, handoff, commit (явний перелік), push.

## Ризики / межі

- Management API опційний: без нього `ready/unacked` per queue = з БД (pending deliveries) — приблизно; UI позначає джерело.
- Replay-контролі — P14; RBAC-ролі — P16; SLO абсолютні — P16 (тут `Ops:Slo` конфіг з орієнтирами ADR-0007).
- Pause через `BasicCancel` не скидає in-flight (ACK після commit) — це і є коректний drain для одного повідомлення; «жорсткий» stop = зупинка контейнера.

## Незалежне review плану — `p13_review` (approve after fixes)

| # | Finding | Рішення |
|---|---|---|
| B1 | `BrokerConnection.IsConnected`/`ReconciliationService.LastReport` живуть у процесі `messaging`; Admin не посилається на `Puluj.Messaging` | Worker публікує в `app_settings`: `WorkerStatusDto.Broker {connected, endpoint}` і `Runtime:Reconciliation:Report` (JSON, `ReconciliationService` кожен прохід). Admin читає їх; DB-частину (pending/overdue/quarantine) рахує сам — моніторинг незалежний від шини (§9.2) |
| B2 | Лічильники root messages ≠ stage jobs (§9.1) відсутні | `roots {receivedHour, completedHour, pending, needsAttention}` (raw_messages + deliveries/quarantine) поряд із jobs; O03 |
| B3 | `unacked_not_empty` (ready 0, unacked > prefetch×consumers) не може спрацювати | Замінено на `inflight_stuck`: in-flight/running > 0 і найстаріший running attempt старший за `Ops:Slo:InflightStuckSeconds` (без умови «0 завершень» — review результату N5: залишений running attempt на зайнятій lane теж має сигналити); UI ніколи не пише «порожня», якщо in-flight > 0 |
| B4 | `MessagingFixture.ResetAsync` не TRUNCATE-ить `subscription_lanes`/`control_audit` → paused lane протікає між тестами | Обидві таблиці додано до TRUNCATE; O01/O02 resume у `finally` |
| N1 | Окремий poll-таймер гонить `Crash()`/`CloseChannelsAsync` | Control poll у циклі `ExecuteAsync` (`WhenAny(Crashed, Delay(ControlPoll))`); `Dictionary<lane, LaneRuntime{channel, tag, consuming, inFlight}>`; стани читаються **до** першого `BasicConsume` |
| N2 | `QueueDeclarePassive` на consuming-каналі; `MessageCount` без prefetched | Passive declare на короткоживучому каналі; drained = `MessageCount==0 && inFlight==0` два опитування поспіль; `UPDATE … WHERE state='draining'`; кожна репліка знає лише свій in-flight (документовано) |
| N3 | Per-lane метрики потребують `lane` у deliveries | Та сама міграція: `processing.deliveries.lane`, `occurred_at` (NULL для старих рядків, заповнює `OutboxWriter`), частковий індекс `(subscription_id, lane) WHERE outcome IS NULL` |
| N4 | `completed_at` без індексу; percentile на кожен poll; wait по retries | BRIN на `deliveries.completed_at`; snapshot кешується ≥5 с; `SET LOCAL statement_timeout`; wait = `min(started_at)` per delivery; `llm-worker:job` згорнуто у `llm-worker` |
| N5 | `backlog_growing` потребує історії | Stateless: `expected5m > 0 && completed5m == 0 && pending > 0`; `since` — не в P13 |
| N6 | Alarms на навмисно paused lane | Правила отримують стан lane: paused/draining → `info` з actor/reason (`lane_paused`), не мовчки; `required_consumer_missing` per lane state=active і pending>0 |
| N7 | Stale 45 с vs heartbeat 30 с/панель 90 с | `Ops:Slo:StaleHeartbeatSeconds` = 90 (heartbeat); stuck = stale && running attempts worker'а |
| N8 | §9 drops: confirms latency, unroutable, incoming rate, completed per worker, duplicate suppression, blocked publisher | Додано у snapshot/rules: `outbox.confirmP50/P95`, `unroutable`, `expectedHour`, `workers[].completedHour`, `inbox.supersededHour` + `Consumers[].duplicates`, `broker_blocked` |
| N9 | §9 явні відкладення | Таблиця «Deferred from §9» у ADR-0012/handoff: DB pool wait (P16), readiness (P16), backfill volume/forecast (P14), silent source/faulty collector (P14 — `/ops/collectors` вже показує помилки), LLM budget 80 % (P16), quorum membership (P16) |
| N10 | Audit: `RecordWaiverAsync` лише останній; `RetryAsync` без reason | `RetryAsync(id, actor, reason)`; усі команди пишуть `control_audit`; `waiver` лишається; CHECK `state IN (...)`, індекс `(subscription_id, lane, at DESC)`; actor = free text, RBAC = token (ролі P16) |
| N11 | O03/O04 у Messaging.Tests, код у `OpsEndpoints` недоступний | `OpsSnapshotService`, `AlarmRules`, `MessageExplorer` → `Puluj.Infrastructure/Messaging/Ops/`; DTO у `Puluj.Contracts` (Infrastructure → Contracts посилання, без циклу) |
| N12 | Explorer без меж | `hours` default 24, text ILIKE ≤ 168 год без `sourceId`/key, `statement_timeout 10s`, `limit ≤ 100`; картка ≤ 200 подій, ≤ 20 attempts per delivery, envelope quarantine обрізаний; url лише `http(s)`; текст — React text nodes |
| N13 | Немає reader'а `completion-manifest.json` | Completion = кожна **зареєстрована** expected гілка має `completed|noop|waived`; `quarantined` → `needs_attention`; manifest-reader — не в P13 (ADR) |
| N14 | `KindOf("messaging-…")` = other; `ScaleAsync` лише processor | `messaging` kind + match; `ScaleAsync(service, replicas)` з allow-list `{processor, messaging}` (`Docker:ScalableServices`) |
| N15 | Тестові knobs | `Messaging:Consumer:ControlPoll` TimeSpan (5 с; fixture 200 мс); fake heartbeat через `SettingsStore` + видалення у `finally`; running attempt — SQL |
| N16 | Колізії з public UI | A03/A04 — власний `mockAdmin`; `helpers.ts`/`fixtures/api.ts`/`client.ts`/`types.ts` не редагуються |
| Q1 | Poll vs NOTIFY | Poll 5 с (реакція ≤ 5 с + in-flight, сказано в UI); NOTIFY — опційна оптимізація (ADR) |
| Q2 | SLO vs ADR-0007 | `Ops:Slo`: `OldestAgeSeconds{live 300, history 3600, replay 3600}`, `OutboxUnconfirmedSeconds 60`, `OutboxCriticalSeconds 300`, `StaleHeartbeatSeconds 90`, `InflightStuckSeconds 300`; узгоджено з ADR-0007 |
| Q3 | Management API у compose | `Messaging__Broker__ManagementUrl` (opt-in) для `admin` у `deploy/docker-compose.yml` + fork-deployment; UI позначає джерело `db|management` |
| Q4 | `retry` лічильник | `retryHour` = attempts `failed|interrupted` за годину; `adminRetryHour` = з `retry_of_attempt_id`; `pending` = deliveries NULL; `inFlight` = attempts running (+ management unacked) |
| Q5 | Rollback/старий worker | Poll ловить `42P01` → один лог, lanes = active; `Down` задокументовано |
