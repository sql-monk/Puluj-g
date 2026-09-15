# P03 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/4. Початок: 2026-09-15. Base: `6550555` (HEAD master, дерево чисте;
результати P00/P01/P02/P07 закомічені). Залежність P02 — done (`P02-handoff.md`, рішення **go**, ADR-0001/0002 accepted).
Виконавець: Claude Code (Opus 5); незалежний reviewer: субагент `p03_review` (план → результат).

## Аналіз задачі

Хвиля 2, «надійний фундамент»: перенести гарантії, доведені spike P02 на брокері, у production-код із
PostgreSQL як джерелом істини. Gate §12: **немає розриву commit → publish; повторна доставка не дублює raw/results**.
Критерії issue: commit/publish crash tests; inbox dedup; archive/replay source не залежить від очищеного outbox.

Що є (перевірено в коді на base):

- `contracts/messaging/topology.json` v1 (усі 11 підписок `planned`), envelope/payload schemas, fixtures, 54 контрактні тести.
- Spike `tests/Puluj.Transport.Spike.Tests/Spike/{Topology,OutboxPublisher,InboxConsumer,RabbitMqFixture}` — референс,
  не бібліотека (файлова «БД», `_channel!` race, послідовні confirms ≈138/s).
- `RawMessageIngestor.IngestAsync`: raw `INSERT … ON CONFLICT DO NOTHING` autocommit + best-effort `pg_notify`;
  `PgNotifyPublisher/Listener` — bridge для API/Processing (лишається за ADR-0002 §bridge).
- `WorkerOptions.Roles` (`migrate,telegram,alerts,processing`), `DatabaseInitializer` (міграції + seed під advisory lock).
- Compose: `rabbitmq` під profile `broker`, `RABBITMQ_*` у `.env.example`; жоден сервіс не споживає брокер.
- Пакети: `RabbitMQ.Client` 7.2.2, `Testcontainers.RabbitMq` 4.15.0, `Testcontainers.PostgreSql` 4.15.0, `JsonSchema.Net` 9.4.0.
- ADR-0004 (crash windows, test IDs P03-C01…C10), ADR-0006 (логічна модель `messaging.*`/`processing.*`), ADR-0005
  (runs/receipts), ADR-0007 (retention proposal, owner P03).

Що P03 **не** робить: collectors через `ingress.received`/raw-writer (P04), normalizer/parser consumers (P05), LLM
lease/fencing (P06), доменні writers (P09/P10), UI моніторингу (P13; тут — метрики/логи/SQL-видимість), replay
orchestration/promote (P14), analytics projections (P15), HA/3 nodes (P16). Legacy `ProcessingLoop`/NOTIFY не змінюються:
raw і далі обробляється claim-циклом; брокер отримує **копію** `raw.stored` (bridge §11).

## Рішення, прийняті планом (reviewer перевіряє окремо)

| # | Рішення | Мотив / межі |
|---|---|---|
| D1 | **Bridge = `RawMessageIngestor`**: raw insert + `messaging.outbox(raw.stored)` + expected `processing.deliveries` — **одна** транзакція (`NpgsqlTransaction`); NOTIFY після commit, як зараз. Вмикається `Messaging:Outbox:Enabled` (default `false`) | §11 «запис raw і outbox атомарно»; ADR-0004 W2. Default deploy без змін; rollback = вимкнути flag |
| D2 | Bridge-`raw.stored` без попереднього `ingress.received` (після review B3): `source_message_key`/`source_revision` — за правилами `fixtures/identity-cases.json` з legacy `source_message_id` (`{id}:e{unix}` → key `{id}`, revision `e{unix}`; інакше key = id, revision `0`); **`correlation_id` детермінований** — UUIDv5 від `(source_id, source_message_key)` (оригінал і редакція ділять correlation, ADR-0003); **`causation_id = event_id`** (root без `ingress.received`; правило «causation == event_id ⇔ bridge root», resolvable в архіві); `producer = raw-writer@{instance}`; `processing_run_id` — run з `processing.runs`; `published_at` = момент запису в outbox (перша публікація; republish не змінює, transport timestamps — `outbox.last_attempt_at`) | Envelope schema вимагає non-null `causation_id`; `ingress.received` не існує до P04. Зафіксувати в ADR-0003 + README контрактів (unit-тест проти identity-cases); P04 замінює на справжній causation. Межа (N9): дубль `ON CONFLICT` не дає `raw.stored{is_new:false}` до P04 |
| D3 | `lane`: `enqueue=true` → `live`, `enqueue=false` (history load) → `history`; один відкритий run на lane (`processing.runs`, kind = lane, state `running`), створюється lazily під advisory lock | ADR-0005; replay lane — P14 |
| D4 | Expected set при публікації = required підписки події, чиї `lanes` містять lane події, з `status ∈ {active, paused}` у `messaging.subscriptions` **за поточною `topology_version`** (SELECT у тій самій tx; таблиця мала); expected рядки `processing.deliveries` пишуться при публікації — саме вони фіксують набір за версією; `planned` — не очікуються і **черги для них не оголошуються** | ADR-0002 «новий consumer отримує лише нові події після активації»; інакше 9 planned черг накопичували б backlog без власника, а reconciliation мала б постійний alarm |
| D5 | `topology.json`: `archive.status: active`, `topology_version: 2`. Registry (після review B1): `TopologyRegistrar.EnsureRegisteredAsync` — idempotent insert `messaging.topology_versions` (version, hash) + `messaging.subscriptions` (bindings/lanes/required з embedded json; `status` початково з json, далі **джерело — БД**: pause/waiver пишуть у БД); викликається з `DatabaseInitializer` (роль `migrate`, під advisory lock) і лениво при першому зверненні `OutboxWriter`/relay/consumer; hash mismatch при тій самій версії → помилка у будь-якому процесі | README контрактів: зміна expected set = +1 версія. W9a тест бере події v1 (seed) і v2 |
| D6 | Новий проєкт **`src/Puluj.Messaging`** (RabbitMQ.Client): connection, `TopologyDeclarer`, `OutboxRelay`, `SubscriptionConsumer` base, `ArchiveConsumer`, `DlqConsumer`, `ReconciliationService`, health. Схема/entities/`OutboxWriter`/`TopologyRegistry` (без залежності від RabbitMQ) — у `Puluj.Infrastructure` | Api/Admin посилаються на Infrastructure і не мають тягнути клієнт брокера |
| D7 | Worker roles `+relay` (declare topology, relay, reconciliation, cleanup) і `+archive` (archive consumer + DLQ consumer). Обидві діють лише при `Messaging:Enabled=true`; інакше пропускаються з warning (порожній `Roles` = усі — локальний dev без брокера має працювати) | Незалежне масштабування; default без змін |
| D8 | Relay: lease batch (`FOR UPDATE SKIP LOCKED`, `lease_until` > confirm timeout батчу, N3), publish persistent+mandatory з **паралельними confirms у батчі** (`Task.WhenAll` по `BasicPublishAsync` з confirmation tracking), `UPDATE … confirmed_at` батчем; `basic.return`/`PublishReturnException` → `last_error=unroutable`, alarm-метрика, backoff без гарячого циклу; nack/timeout → bounded exponential backoff + jitter у `next_attempt_at`. Crash після confirm до mark → republish тим самим `event_id` | ADR-0001 п.8, ADR-0002 «Відкрите», P02 ≈138/s |
| D9 | Consumer: receive → registry/major-version check → inbox fast-path → handler work поза tx → **коротка tx**: re-check inbox, ефект, inbox completion, receipt, outbox → commit → ACK. Transient помилка → `processing.attempts` (+`interrupted`/`failed`), bounded backoff **утримуючи unacked** (prefetch обмежує; cap `Messaging:Consumer:MaxBackoff` ≤ 30 s ≪ consumer timeout 30 хв, N8), потім `basic.nack(requeue=true)`; attempts ≥ `max_delivery_attempts` **або** несумісна schema → tx: receipt `quarantined` + `processing.quarantine` + inbox `quarantined` → commit → `nack(requeue=false)` → DLQ. Повторна доставка з receipt `quarantined` → лише nack (ідемпотентно) | ADR-0004 §6.2, W6a-1/2; ADR-0001 п.8 (nack, не reject) |
| D10 | `DlqConsumer` на `puluj.{sub}.{lane}.dlq`: якщо для `(subscription_id, event_id)` уже є quarantine-рядок (навіть resolved — stale копія після retry) → лише ACK; інакше (crash-loop delivery-limit без receipt) tx: `quarantine` (повний envelope + headers) + receipt `quarantined` (`reason: delivery_limit`) + inbox `quarantined` → ACK. Джерело істини — `processing.quarantine`; DLQ — операційна копія | ADR-0004 W6a-2 |
| D11 | Reconciliation (у ролі `relay`, період `Messaging:Reconciliation:Interval`): (a) expected без receipt старше SLO → alarm-лог + gauge; (b) receipts від підписок поза registry → audit-лог; (c) outbox unconfirmed age > threshold → alarm; (d) **idempotent re-declare** topology (відновлює drift bindings, W11); (e) outbox cleanup: confirmed + grace, для `replay_source` — лише за наявності receipt `archive=completed`. Management API bindings check — не в P03 (re-declare покриває drift; зафіксувати). Re-declare — свіжий канал на спробу, `PRECONDITION_FAILED` → alarm-метрика без retry-шторму; усі змінювані параметри лишаються в `x-*` arguments (policies потребують management API/rabbitmqctl) — зміна = нова назва черги + transfer (N1) | ADR-0002 Readiness; §15.2 |
| D12 | Admin retry з quarantine (W6b) — сервісний метод `QuarantineService.RetryAsync(quarantineId, actor)`, без UI (P13): одна tx — inbox-рядок підписки знімається, receipt повертається в expected з `retry_of_attempt_id`, quarantine отримує `resolved_at`, у outbox додається **redelivery-рядок** того самого `event_id` з `target_queue` = черга підписки (publish через default exchange, минаючи fan-out); envelope береться з **`processing.quarantine.envelope`** (review B2: outbox може бути вичищений, `events` для події, quarantined самим archive, не існує). Тому outbox має surrogate PK `outbox_id` + unique `(event_id) WHERE target_queue IS NULL`; relay обробляє обидва види рядків однаково | ADR-0004 W6b; durable transfer через outbox, не прямий publish з admin-процесу |
| D13 | Retention (ADR-0007 owner P03): реалізується лише outbox cleanup (D11e) + `inbox` cleanup після `completed_at` + 30 днів; `events`/`deliveries`/`attempts`/`quarantine` — без автоматичного видалення у P03 (політика зафіксована в ADR-0007, deletion job — P16/ops) | Не видаляти evidence без backup/policy |
| D14 | Runtime-валідація payload проти JSON Schema — **не** в production (немає `JsonSchema.Net` у `src/`); перевірки: `event_type` у registry, `schema_version` MAJOR = supported. Повні schema-перевірки згенерованих envelope — у unit tests (`JsonSchema.Net`) | §16.2 п.1 без drift; без нової runtime-залежності |
| D15 | Commit/PR не виконуються (як P00–P07); результат — незакомічені файли + manifest | Правило сесії |

## Артефакти

| Артефакт | Шлях | Призначення |
|---|---|---|
| Entities + конфігурації | `src/Puluj.Domain/Entities/Messaging/{OutboxMessage,InboxEntry,ArchivedEvent,EventLink,SubscriptionRegistration,TopologyVersion}.cs`, `…/Processing/{ProcessingRun,ProcessingGeneration,StageResult,ProcessingAttempt,Delivery,QuarantineEntry}.cs`; `src/Puluj.Infrastructure/Persistence/Configurations/{Messaging,Processing}Configuration.cs` (схеми `messaging`, `processing`, snake_case) | ADR-0006 → фізична модель |
| Міграція | `src/Puluj.Infrastructure/Persistence/Migrations/2026091xxxxxxx_AddMessagingSchema.cs` (+Designer через `dotnet ef migrations add`), additive; `Down` дропає лише нові схеми | Rollback = `Down` або не читати |
| Registry/envelope (без брокера) | `src/Puluj.Infrastructure/Messaging/Topology/{TopologyRegistry,TopologyDocument}.cs` (embedded `contracts/messaging/topology.json`), `Envelope.cs`/`EnvelopeFactory.cs`, `OutboxWriter.cs` (insert outbox + expected deliveries на відкритому `NpgsqlTransaction`), `ProcessingRuns.cs` (live/history run), `MessagingOptions.cs` | Bridge і майбутні producers |
| Bridge | `RawMessageIngestor`: транзакція raw + outbox + deliveries при `Messaging:Outbox:Enabled` | §11 |
| Broker runtime | `src/Puluj.Messaging/{BrokerConnection,TopologyDeclarer,OutboxRelay,SubscriptionConsumer,ArchiveConsumer,DlqConsumer,ReconciliationService,QuarantineService,MessagingMetrics,DependencyInjection}.cs`; `Puluj.sln` +проєкт | D6–D12 |
| Worker | `WorkerOptions` +`relay`, `archive`; `Program.cs` реєструє за `Messaging:Enabled`; `appsettings.json` секція `Messaging` (вимкнено) | D7 |
| Compose/env | `deploy/docker-compose.yml`: сервіс `messaging` (roles `relay,archive`) під profile `broker`; `.env.example`: `MESSAGING_*` | Opt-in |
| Тести (unit, без інфраструктури) | `tests/Puluj.Messaging.Tests/Unit/*`: registry (expected set за status/version), envelope проти `envelope.schema.json`+`raw.stored.schema.json`, backoff, major-version check | §16.2 п.1 |
| Тести (PostGIS + RabbitMQ Testcontainers) | `tests/Puluj.Messaging.Tests/Integration/*` з `MessagingFixture` (обидва контейнери, міграції, `TRUNCATE`); crash tests P03-C01…C10 + gate | ADR-0004, issue criteria |
| Контрактні тести | `TopologyRegistryTests`: version 2, `archive` active | topology.json змінено |
| Docs | ADR-0004 → accepted (P03, таблиця test → результат), ADR-0006 → accepted (фізична модель, індекси), ADR-0002/0003/0005/0007 — рядки «Відкрите» P03 + bridge note, `docs/adr/README.md`, `contracts/messaging/README.md`, plan §16.1 (команда нового suite), §17 P03, `.env.example`, `docs/fork-deployment.md` (profile `broker` + `messaging`) | §16.2 п.6–7 |
| Evidence | `P03-plan.md`, `P03-handoff.md`, `P03-crash-evidence.md/.json`, `P03-build-manifest.json`, `test-results/p03-*.trx` | §16.4 |

## Тестова матриця (ADR-0004 → P03)

| Test | Вікно | Сценарій | Assert (committed outcome, не «прийшло») |
|---|---|---|---|
| P03-C01 | W1b/W2 | Ingest з bridge; relay не запущений | raw + outbox(`raw.stored`, unconfirmed) + 1 expected delivery (`archive`) в одній tx; outbox age метрика > 0; після старту relay → `confirmed_at`, events row |
| P03-C02 | W2 | Relay падає (cancel) до publish; рестарт; підкейс: relay впав після lease, до publish → інший relay бере рядок лише після `lease_until` | той самий `event_id`, 1 events row; до закінчення lease рядок не публікується |
| P03-C03 | W3/W12b | Relay `markConfirmed=false` (crash після confirm) → другий прохід | 2 доставки, 1 inbox, 1 events row, duplicate counter = 1 |
| P03-C04 | W6a-1/2 | Handler завжди падає (transient); max attempts = 5; crash до commit receipt на 5-й; redelivery; crash до nack після receipt; redelivery | `attempts` = 5 (без 6-ї), receipt `quarantined`, `quarantine` row 1, DLQ depth → 0 після `DlqConsumer`/або 1 без нього; повторний nack без нової attempt |
| P03-C05 | W6b | `QuarantineService.RetryAsync` після «виправлення» handler; **outbox попередньо вичищено, events row відсутній** | нова attempt з `retry_of_attempt_id`, receipt `completed`, quarantine `resolved_at`; stale DLQ-копія → DlqConsumer ACK без нового quarantine |
| P03-C06 | W9a | Подія з `topology_version=1` (seed v1: expected set v1) при registry v2 | expected для події — лише з v1; нова підписка v2 не очікується для старої події |
| P03-C07 | W9b | `archive` → `paused` з backlog; `WaiveAsync(reason, actor)` | receipts `waived{reason, actor}` на кожну expected; workflow не «completed» без waiver |
| P03-C08 | W11 | `QueueUnbind` archive.live → relay | `basic.return` → outbox `last_error=unroutable`, `unconfirmed`; reconciliation re-declare → bindings відновлено → republish → confirmed, events row |
| P03-C09 | W12a | Handler apply кидає transient `NpgsqlException` на 1-й commit | attempt `failed` + retry → `completed`; effects = 1 |
| P03-C10 | W14a | Той самий envelope опубліковано 3× напряму | inbox 1, events 1, duplicates 2, ACK усі |
| G-inbox | — | 2 репліки archive ділять 200 подій | 200 events rows, sum delivered = 200 + duplicates 0 |
| G-archive | issue | Cleanup outbox після archive receipt; cleanup без receipt | Rows з receipt видалено, без — лишились; backfill `events` → envelope повний |
| G-recon | ADR-0002 | expected без receipt старше SLO; receipt від невідомої підписки | reconciliation повертає gap list / audit |
| G-bridge | D1 | Ingest дубля (`ON CONFLICT`) | без outbox row; ingest при штучній помилці outbox → raw rollback |
| Unit | D14 | Envelope `raw.stored` bridge проти JSON schema; registry expected set; backoff bounds; major check | schema valid; таблиці |

## Кроки

1. **Статус.** Коментар в issue #4 «розпочато (in_progress)», §17 P03 → `in_progress`. Project card — токен без scope `project` → зафіксувати.
2. **Незалежне review плану** (`p03_review`, read-only): повнота проти §5–6, §15.2, §16.2, ADR-0004 test IDs, D1–D15; blocking → виправити до старту.
3. **Схема**: entities, configurations, `dotnet ef migrations add AddMessagingSchema` (перевірити SQL: схеми, unique/partial індекси, `jsonb`), `Down`.
4. **Infrastructure messaging**: `TopologyRegistry` (embedded json), `EnvelopeFactory`, `ProcessingRuns`, `OutboxWriter`, `MessagingOptions`; bridge у `RawMessageIngestor`.
5. **Puluj.Messaging**: connection/declarer/relay/consumer base/archive/dlq/reconciliation/quarantine/metrics/DI; Worker roles; appsettings; Compose/env.
6. **Тести**: unit; `MessagingFixture` (PostGIS + RabbitMQ); C01–C10 + gates; оновити контрактні тести; `PipelineFixture` TRUNCATE +нові таблиці.
7. **Запуск** (усе через `pwsh -File scripts/with-lock.ps1`): `dotnet build Puluj.sln`; `dotnet test tests/Puluj.Messaging.Tests`; `tests/Puluj.Messaging.Contracts.Tests`; `tests/Puluj.Integration.Tests` (ingestor змінено); `tests/Puluj.Processing.Tests`; за потреби Api/Admin/Analytics (Infrastructure змінено → так). TRX → `test-results/p03-*.trx`.
8. **Docs/ADR/plan/env** за таблицею; evidence-файли.
9. **Незалежне review результату** (§16.2, фокус п.1, 2, 3, 6, 7, 12); blocking → виправити → повторний прогін.
10. **Handoff §16.4**, коментар в issue зі статусом, §17 → `done`; issue закрити після approve.

## Самоперевірка плану

- Атомарність: raw+outbox+deliveries і inbox+ефект+receipt — по одній DB-транзакції; ACK лише після commit; relay mark після confirm.
- Identity: `event_id` стабільний (outbox PK), archive PK, inbox unique — дублікати поглинаються (C03, C10).
- Archive незалежний від outbox: cleanup лише після archive receipt; backfill читає `events`.
- Additive: нові схеми; legacy таблиці/loop не змінюються; default deploy (`Messaging:Enabled=false`) без ефекту.
- Тести на реальних PostGIS + RabbitMQ; пропуски (management API bindings check, HA, LLM fencing) названі.

## Незалежне review плану — p03_review

Вердикт: **approve after fixes**. Blocking → правки внесено до старту реалізації:

| # | Finding | Правка в плані |
|---|---|---|
| B1 | Seed registry лише при старті relay: bridge у collector-процесі і C01 без relay лишили б outbox без expected deliveries | D5: `TopologyRegistrar.EnsureRegisteredAsync` у `DatabaseInitializer` (migrate) + лениво в `OutboxWriter`/relay/consumer; status — з БД; hash mismatch → fail у будь-якому процесі |
| B2 | Retry (W6b) не має джерела envelope після outbox cleanup; events для події, quarantined самим archive, немає; stale DLQ-копія створювала б другий quarantine | D12: envelope для redelivery — з `processing.quarantine.envelope`; D10: DlqConsumer ACK без нового quarantine, якщо рядок (навіть resolved) існує; C05 перевіряє retry після cleanup і без events row |
| B3 | Identity bridge-envelope: legacy `source_message_id` копіювався б у key; `correlation_id` UUIDv7 на raw розривав оригінал/редакцію; `causation_id = correlation_id` — dangling uuid | D2: key/revision за identity-cases (unit-тест проти fixture); `correlation_id` = UUIDv5(source_id, key); `causation_id = event_id` для bridge root; правило в ADR-0003/README |

Non-blocking N1–N9 прийняті: N1 (declare: arguments, свіжий канал, PRECONDITION_FAILED → alarm) → D11; N2 (lanes у expected set) → D4;
N3 (lease > confirm timeout, C02 підкейс) → D8/C02; N4 (`TelegramCollector` `enqueue: !_loadingHistory` → live-пости під час history
load отримають `lane=history` — відома межа до P04) → handoff; N5 (`published_at` = outbox insert) → D2; N6 (warning при
`Outbox:Enabled`, `job_key` в attempts) → реалізація; N7 (fixture відновлює registry/bindings після C07/C08; `PipelineFixture` TRUNCATE
нових схем) → тести; N8 (backoff cap) → D9; N9 (`is_new:false` не публікується до P04) → D2/handoff.

