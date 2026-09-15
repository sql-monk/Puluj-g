# P03 — надійний transport: inbox/outbox, relay, архів і reconciliation

Task: [P03 / issue #4](https://github.com/sql-monk/Puluj-g/issues/4).
Status: **done** — реалізація, тести й документація завершені; незалежне review результату (`p03_review`): approve after fixes →
blocking B1 і non-blocking N1/N2/N4/N5/N6/N8/N9/N11/N13 виправлено → повторний прогін зелений. Rollout не виконувався (default deploy без змін).
Owner: Claude Code (Opus 5), агент `p03`. Reviewer: план — субагент `p03_review` (approve after fixes, B1–B3 внесено до старту, `P03-plan.md`);
результат — субагент `p03_review` (approve after fixes, таблиця нижче).
Base commit: `6550555` (master, чисте дерево). Результат — локальні незакомічені файли (`P03-build-manifest.json`, 60+ файлів);
commit/PR не виконувалися (як P00–P07).

## Результат для споживача

- **Схеми `messaging` і `processing`** (міграція `20260915150214_AddMessagingSchema`, additive, `Down` дропає лише нові таблиці/схеми;
  ролі `puluj_reader`/`puluj_admin` отримують ті самі права, що на `public`): `messaging.outbox/inbox/events/event_links/subscriptions/topology_versions`,
  `processing.runs/generations/stage_results/attempts/deliveries/quarantine` (ADR-0006 → accepted, фізична модель у ADR).
- **DB-first bridge** (plan §11): `RawMessageIngestor` під `Messaging:Outbox:Enabled` комітить raw + `messaging.outbox(raw.stored)` + expected
  `processing.deliveries` однією `NpgsqlTransaction`; NOTIFY після commit як раніше. Flag off (default) → поведінка ingest без змін.
- **`src/Puluj.Messaging`** (RabbitMQ.Client 7.2.2, лише Worker посилається): `TopologyDeclarer` (exchange/DLX/quorum queues/bindings лише для
  active/paused підписок, idempotent), `OutboxRelay` (lease `FOR UPDATE SKIP LOCKED`, паралельні publisher confirms батчем — 200/батч у G01,
  mark після confirm, `basic.return` → `unroutable` без гарячого циклу, nack/timeout → backoff/lease), `SubscriptionConsumer` (parse → registry +
  schema MAJOR → inbox fast path → attempts ≥ max → attempt row → робота → tx(inbox re-check, ефект, receipt, outbox, attempt) → commit → ACK;
  transient → bounded backoff + `basic.nack(requeue=true)`; вичерпання/невалідний вхід → tx quarantine → `nack(requeue=false)` → DLQ),
  `ArchiveHandler` (`messaging.events`, тіло як отримано), `DlqConsumer` (crash-loop dead-letters → quarantine; вже записане/завершене → лише ACK),
  `ReconciliationService` (overdue expected, unknown receipts, outbox age, re-declare, cleanup outbox після archive receipt для `replay_source`,
  inbox retention), `MessagingMetrics` (`puluj.outbox.*`, `puluj.inbox.*`, `puluj.deliveries.overdue`, `puluj.quarantine.open`, `puluj.topology.declare_failed`).
- **Infrastructure (без брокера)**: `TopologyRegistry` (embedded `contracts/messaging/topology.json`), `TopologyRegistrar` (`messaging.topology_versions`/
  `subscriptions`, hash-перевірка версії; викликається з `DatabaseInitializer` і лениво), `OutboxWriter`, `Envelope`, `RawStoredEnvelope`,
  `SourceIdentity` (ADR-0003 identity + UUIDv5 correlation), `ProcessingRuns` (один відкритий run на lane), `SubscriptionAdmin`
  (`RetryAsync` з quarantine через outbox redelivery, `WaiveAsync`, `SetStatusAsync` — сервісні методи; UI/endpoints — P13).
- **Worker**: ролі `relay` (declare + relay + reconciliation) і `archive` (archive consumer + DLQ consumer), діють лише при `Messaging:Enabled`;
  секція `Messaging` в `appsettings.json` (усе вимкнено). Compose: сервіс `messaging` під profile `broker`; collectors отримують
  `Messaging__Outbox__Enabled=${MESSAGING_OUTBOX_ENABLED:-false}`; `.env.example`.
- **Контракти**: `topology.json` v2 — `archive` → `active` (README: черги/expected лише для active/paused); `asyncapi.yaml` перегенеровано.

Контракти/міграції/flags/конфігурація: `Messaging:Enabled`, `Messaging:Outbox:Enabled`, `Messaging:Broker:*`, `Messaging:Relay:*`
(`Lease` має перевищувати `ConfirmTimeout` — перевіряється при старті), `Messaging:Consumer:*`, `Messaging:Reconciliation:*`.
Docs: ADR-0004 → accepted (розділ «Реалізація»), ADR-0006 → accepted («Фізична модель»), ADR-0002/0003/0005/0007 доповнено, `docs/adr/README.md`,
`contracts/messaging/README.md`, `docs/fork-deployment.md` (профіль `broker`), plan §16.1 (команда suite), §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (4.3.5), Testcontainers 4.15.0.
Усі команди — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p03-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests …Unit` (run1 → run2 → run3 після review-правок) | 1 → 0 → 0 | 16/1/0 → 17/0/0 → **17/0/0** |
| `dotnet test tests/Puluj.Messaging.Tests …Integration` (run1; run2 після review-правок) | 0; 0 | 15/0/0; **15/0/0** — C01–C10, G01–G05 на реальних PostGIS + RabbitMQ |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (topology v2; повторно після правок) | 0 | **54/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` (ingestor/fixture змінено; повторно після правок) | 0 | **32/0/1** (skip — P00 baseline, як у P07) |
| `dotnet test tests/Puluj.Processing.Tests` | 0 | 105/0/0 |
| `dotnet test tests/Puluj.Api.Tests` | 0 | 30/0/0 |
| `dotnet test tests/Puluj.Admin.Tests` | 0 | 56/0/0 |
| `dotnet test tests/Puluj.Analytics.Tests` | 0 | 33/0/0 |
| `dotnet build Puluj.sln` (фінальний, після правок) | 0 | 0 warnings |
| `docker compose -f deploy/docker-compose.yml --env-file .env.example [--profile broker] config --services` | 0 | `messaging`/`rabbitmq` лише з профілем |

`tests/Puluj.Transport.Spike.Tests` (P02) не запускався — не змінювався і не посилається на `src/`; web — без змін.
Unit run1: одна хибна перевірка тесту (очікував «event_id» у тексті помилки десеріалізації при відсутніх required полях) — виправлено очікування.

## Race/crash/load evidence

[`P03-crash-evidence.md`](P03-crash-evidence.md) / [`P03-crash-evidence.json`](P03-crash-evidence.json): C01–C10 (W1b/W2, W2+lease, W3/W12b,
W6a-1/2, W6b, W9a, W9b, W11, W12a, W14a), G01 (200 подій, 2 репліки 100/100, 0 дублів), G02 (cleanup лише після archive receipt), G03
(reconciliation), G04 (bridge атомарність через trigger-fail → raw rollback; history lane), G05 (schema/unknown/invalid → quarantine без attempts).
Load/SLO не вимірювалися (P16); smoke P02 (≈138 publish/s послідовно) → у G01 батч 200 підтверджено одним проходом.

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | §17/ADR-0004 заявляли `done`/handoff до завершення review | handoff написано після правок; §17 — фактичний вердикт | docs |
| N1 | `DlqConsumer` міг перезаписати inbox `completed` (stale копія після republish) на `quarantined` | перевірка `messaging.inbox.outcome IN ('completed','noop')` → лише ACK | integration run2 |
| N2 | Тіло без `event_id` отримувало випадковий id → другий quarantine при crash до nack | `BodyEventId` = UUIDv5 від SHA-256 тіла | G05, run2 |
| N4 | `waived` receipt не ставав `completed` після реальної обробки backlog | upsert дозволяє `quarantined`/`waived` → `completed` | run2 |
| N5 | `StartAttemptAsync` позначав `interrupted` живу спробу іншої репліки | interrupt лише для `running` старших за `Messaging:Consumer:StaleAttempt` (5 хв) | run2 |
| N6 | `_registered` кешувався всередині ingest-транзакції (rollback → статуси з файлу) | реєстрація з tx викликача йде окремим autocommit-з'єднанням | G04 rollback, run2 |
| N8 | `Lease ≤ ConfirmTimeout` не валідувався | `OutboxRelay.ValidateOptions` при старті/проході | build |
| N9 | `Down` не дропав схеми | `DROP SCHEMA IF EXISTS messaging/processing` у Down | migration |
| N11 | `EnsureRegisteredAsync` поза retry-циклом → host stop при тимчасово недоступній БД | виклик усередині retry-циклів relay/consumer/DLQ/reconciliation | run2 |
| N13 | overdue gauge капався LIMIT 1000 | окремий `count(*)` → `Report.OverdueCount` | G03 |
| N3, N7, N10, N12 | backoff у dispatch loop; hash mismatch зупиняє ingest/migrate; C06 доводить лише ретроактивність; retired без drain | зафіксовано нижче як межі (P13/P16) | — |

Reviewer підтвердив за §16.2: п.1 (embedded topology = файл, envelope валідний проти schema, MAJOR check), п.2 (одна tx raw+outbox+deliveries;
inbox re-check у tx; ACK після commit; quarantine до nack; lease/SKIP LOCKED; return без гарячого циклу), п.3 (identity ADR-0003, correlation
UUIDv5, retry supersedes attempts), п.6 (additive, індекси за запитами, grants, TRUNCATE у PipelineFixture), п.7 (тіло як отримано, causal колонки,
cleanup після archive), п.10 (пароль не логується, bounded tags), п.12 (evidence = json, TRX є, пропуски названі).

## Відомі обмеження / невиконані перевірки

- Bridge до P04: дубль `(source, source_message_id)` не публікує `raw.stored{is_new:false}`; `causation_id = event_id` (root); live-пости під час
  history load отримують lane `history` (`TelegramCollector` `enqueue: !_loadingHistory`); collector crash W1a/W1c не покриті.
- Hash mismatch `topology.json` під зареєстрованою версією зупиняє ingest (при `Outbox:Enabled`) і `migrate` — навмисно (ADR-0002: bump версії),
  але це операційний ризик deploy: оновлювати `topology_version` при кожній зміні файлу.
- Backoff transient-помилок виконується на dispatch loop каналу (`ConsumerDispatchConcurrency=1`): інші prefetched доставки того самого каналу
  чекають ≤ `Messaging:Consumer:MaxBackoff` (30 s) — bounded head-of-line stall; окремий таймер/канал — при потребі в P05+.
- `SetStatusAsync("retired")` не вимагає drain/waiver backlog — expected рядки лишаються overdue до `WaiveAsync`; `subscriptions.waiver` зберігає
  лише останній audit-запис (повний audit — P13). Readiness брокера — логи/метрики, без health endpoint (P13); management API bindings check не
  реалізовано — drift bindings лікує re-declare у reconciliation (C08).
- Retention: лише outbox/inbox cleanup; `events`/`deliveries`/`attempts`/`quarantine` без видалення; партиціювання/EXPLAIN на production-shaped
  даних — P16. `fencing_token` завжди 0 (P06). `processing.generations`/`stage_results` — таблиці без writers (P14/P05).
- C06 доводить durable expected set (ретроактивно нічого не очікується), а не обчислення під v1-кодом. Restart брокера/alarm/loss of quorum —
  P02 evidence / P16. GitHub Project card — токен `gh` без scope `project`; статус — коментарі в issue та §17.

## Rollout / rollback / input ownership

Default deploy без змін: `Messaging:Enabled=false`, `Messaging:Outbox:Enabled=false`; міграція додає лише нові схеми (застосується
`migrate`-контейнером при наступному deploy — безпечно). Увімкнення: `docker compose --profile broker up -d` (rabbitmq + `messaging`), потім
`MESSAGING_OUTBOX_ENABLED=true` для collectors. Rollback: flag off → collectors пишуть як раніше; зупинити профіль; outbox/receipts лишаються
(не читаються legacy-шляхом); `Down` міграції — за потреби. Input ownership: `contracts/messaging/` (P01–P03), `src/Puluj.Messaging` і
`src/Puluj.Infrastructure/Messaging` (P03), міграція/DI/Compose/appsettings (P03 — один власник, як вимагає §12).

## Чекбокси issue #4

- [x] Commit/publish crash tests (C01–C10); inbox dedup (C03/C10/G01); archive/replay source не залежить від очищеного outbox (G02, C05)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (topology v2), міграція/rollback, конфігурація (appsettings, Compose, `.env.example`), документація (ADR-0002…0007, README, plan) оновлені

## Наступний task

**P04** — collector outbox/checkpoint і raw-writer через `ingress.received`: замінити bridge у `RawMessageIngestor` на producer outbox collectors
(W1a/W1b/W1c), справжній `causation_id`, `raw.stored{is_new:false}` при повторі, зняття unique hash і `(source_id, source_message_key, source_revision)`.
Паралельно можливий P05 (normalizer/parser consumers на `SubscriptionConsumer` + `IDeliveryHandler`, активація підписок → topology v3) і P13
(admin endpoints над `SubscriptionAdmin`/`processing.quarantine`, health readiness брокера).
