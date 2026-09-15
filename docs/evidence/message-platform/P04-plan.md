# P04 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/5. Початок: 2026-09-15. Base: `d1bf1b0` (master; P03 закомічено користувачем, дерево чисте).
Залежність P03 — done (`P03-handoff.md`: outbox/relay/consumer base/archive/reconciliation, topology v2).
Виконавець: Claude Code (Opus 5); незалежний reviewer: субагент `p04_review` (план → результат). Результат цього разу **комітиться** (вимога користувача).

## Аналіз задачі

Хвиля 3, «єдиний ingress»: collectors перестають писати `raw_messages` напряму; вони комітять `ingress.received` у producer outbox
**разом із checkpoint джерела** однією транзакцією (§6.1), relay публікує, **raw-writer** (нова підписка) зберігає оригінал з
identity `(source_id, source_message_key, source_revision)` і публікує `raw.stored{is_new}` атомарно з raw. Content hash перестає бути
unique — лишається індексом схожості (§5.2). Критерії issue: усі поточні collectors + reconnect/backfill; новий пост з тим самим текстом
зберігається, redelivery — ні; міграція identity з rollback.

Що є (перевірено на base):

- Collectors: `TelegramCollector` (live updates, backfill за `collector_states.last_source_message_id`, whole-history load з cursor і
  `enqueue:false` + `ReprocessService.Pause/Reset/Resume`), `AlertsInUaCollector` (poll active feed, cursor = active set у `collector_states.cursor`,
  `{id}:start`/`{id}:end`), `AlertsInUaHistoryCollector` (one-off, state в `app_settings`). Усі кличуть `RawMessageIngestor.IngestAsync`
  і **окремо** `CollectorStateStore.MarkSuccessAsync` (два autocommit-записи — W1a вікно).
- `RawMessageIngestor`: `INSERT … ON CONFLICT DO NOTHING` (unique `(source_id, source_message_id)` **і** unique `hash`); дубль → `IngestResult(null,false)`;
  P03 bridge (`Messaging:Outbox:Enabled`) додає outbox `raw.stored` в ту саму tx. Admin `/dev/ingest` теж через ingestor.
- P03: `OutboxWriter`, `SubscriptionConsumer`/`IDeliveryHandler`, `TopologyRegistrar`, `SourceIdentity.FromLegacy` (правила identity-cases), `ProcessingRuns`.
- Контракт `ingress.received` (schema + 4 fixtures: telegram, edit, alerts start/end): root event (`causation_id: null`, без `raw_message_id`),
  payload з `legacy_source_message_id`, `collector{name,version,instance}`, `checkpoint`. `topology.json` v2: `raw-writer` planned (bindings
  `ingress.received`, lanes live/history/replay, emits `raw.stored`).
- Analytics `RawMessageReader` парсить `:e{ts}` з legacy id (P15 перейде на нові колонки — не в scope).

Що P04 **не** робить: normalizer/parser (P05), LLM (P06), blob storage/`payload_ref` для вкладень (поріг 256 KiB не досягається текстом
Telegram; фіксуємо як межу), retire legacy `ProcessingLoop` (P05/P16), UI collectors (P13), analytics на нових колонках (P15).

## Рішення, прийняті планом

| # | Рішення | Мотив / межі |
|---|---|---|
| D1 | **Міграція `AddRawMessageIdentity`**: `raw_messages.source_message_key varchar(512)`, `source_revision varchar(64)`; backfill SQL за правилами ADR-0003 (`^(.+):(e[0-9]+)$` → key/revision, інакше key = id, revision `0`); NOT NULL після backfill; unique `(source_id, source_message_key, source_revision)`; unique `hash` → звичайний індекс; unique `(source_id, source_message_id)` лишається (legacy id = key або `key:revision`). `Down`: дропає нові колонки/індекс; unique `hash` **не** відновлюється (після зміни можуть існувати рядки з однаковим контентом — rollback коду без rollback даних) | §5.2 «hash — індекс схожості»; ADR-0003; «повідомлення, раніше відкинуті content-dedup, з hash не відновлюються» — лише backfill джерела |
| D2 | `RawMessage` +`SourceMessageKey`/`SourceRevision`; `IncomingMessage` +optional `SourceMessageKey`/`SourceRevision` (collectors задають явно; fallback `SourceIdentity.FromLegacy(SourceMessageId)`); `RawMessageIngestor` конфліктує лише за identity і повертає **існуючий raw id** (`IngestResult(existingId, IsNew:false)`) | §5.2 «при повторі отримуємо існуючий raw id»; PipelineTests (`duplicate.IsNew == false`) лишаються валідними |
| D3 | **`IngressWriter`** (Infrastructure/Messaging): одна tx — outbox `ingress.received` (+ expected deliveries: `raw-writer`, `archive`) + upsert `collector_states` (checkpoint: `last_source_message_id`, `last_message_at`, `cursor`, `last_success_at`, reset failures). Envelope: root (`causation_id = null`, без `raw_message_id`), `correlation_id` = UUIDv5(source_id, key) (той самий, що P03 bridge — ланцюг сумісний), `producer = collectors@{instance}`, payload за schema (`legacy_source_message_id`, `collector{name,version,instance}`, `checkpoint`) | §6.1 «outbox + checkpoint однією транзакцією»; W1a: crash до commit → ні outbox, ні checkpoint → re-read джерела; W1b: relay |
| D4 | **`CollectorIngress`** (Collectors) — єдиний вхід для collectors з двома режимами за `Messaging:Ingress:Enabled`: `false` → як зараз (`RawMessageIngestor` + окремий `MarkSuccessAsync`; bridge P03 за `Outbox:Enabled`), `true` → `IngressWriter` (checkpoint у tx, raw пише raw-writer). Повертає `IngressResult(Accepted, RawMessageId?, IsNew?)`: у режимі ingress `IsNew` невідомий синхронно (collectors використовують його лише для лічильників/логів) | Один cutover-flag; default deploy без змін; rollback = flag off |
| D5 | Collectors: `TelegramCollector.StoreAsync` → `CollectorIngress.PublishAsync(msg, checkpoint: last id/date)` — live: checkpoint з кожним повідомленням (як `MarkSuccess` зараз), backfill: checkpoint = id повідомлення (монотонно), history: cursor сторінки з останнім повідомленням сторінки (сторінка без stored → окремий `MarkSuccess`, як зараз); `AlertsInUaCollector.ReconcileAsync` → start/end через ingress, cursor (active set) з **останнім** publish циклу або окремим `MarkSuccess`, якщо нічого не публікувалося; `AlertsInUaHistoryCollector` → ingress без checkpoint (state в `app_settings`, як зараз). Identity: Telegram key `{id}`/rev `0` або `e{editUnix}`; alerts key `{id}:start|end`/rev `0` | ADR-0003 таблиця; identity-cases fixture |
| D6 | **`RawWriterHandler`** (Puluj.Messaging, підписка `raw-writer`): `ApplyAsync` — INSERT raw (identity ON CONFLICT DO NOTHING RETURNING) або SELECT існуючий → `raw.stored{is_new}` в outbox тієї ж tx (`causation_id` = ingress `event_id`, той самий `correlation_id`, lane з envelope); `is_new:false` теж публікується (ADR-0004 W1a). Post-commit hook (`DeliveryResult.AfterCommit`): `NOTIFY RawMessageStored` для live/is_new — legacy `ProcessingLoop` прокидається як раніше; history — без NOTIFY (Pending до `ResetAsync`) | Raw-writer — єдиний писар raw у режимі ingress; legacy processing без змін |
| D7 | `SubscriptionConsumer`: +`DeliveryResult.AfterCommit` (виконується після commit, до ACK; помилка лише логується — ефект уже durable, NOTIFY best-effort як зараз) | Мінімальна зміна consumer base |
| D8 | **Telegram history drain**: перед `reprocess.ResetAsync()` у режимі ingress — `IngressWriter.WaitForDrainAsync(sourceIds, lane history, timeout)`: poll `processing.deliveries` (raw-writer, outcome NULL) ⋈ `messaging.outbox` (lane history, envelope source_id) до 0 або timeout (warning і продовження) | Без цього `ResetAsync` міг би стартувати до того, як raw-writer записав усі history raw → порушення порядку rebuild |
| D9 | `topology.json` v3: `raw-writer` → `active`; Worker роль `raw-writer` (`WorkerOptions.BrokerRoles`), Compose `messaging` roles `relay,archive,raw-writer`; `.env.example` `MESSAGING_INGRESS_ENABLED` для collectors | ADR-0002: активація = нова версія |
| D10 | Межі (не змінюємо): W1c — Telegram edit update між отриманням і commit втрачається до наступного `getDifference` (WTelegram update state — локальний файл); alerts.in.ua active feed — джерело без re-read, cursor у tx дає повтор `:end` після рестарту; `payload_ref`/blob — не потрібен (текст/JSON ≪ 256 KiB); Admin `/dev/ingest` лишається на `RawMessageIngestor` | §6.1 «межі відновлення фіксуємо явно» |
| D11 | Bridge P03 (`Messaging:Outbox:Enabled`) лишається як fallback до P16; при `Ingress:Enabled` collectors його не використовують. Критерій видалення — canary P16 | §11 «тимчасова сумісність з критеріями видалення» |
| D12 | Commit наприкінці (після review approve і зеленого прогону): один commit `feat(messaging): P04 …` без push | Вимога користувача |

## Артефакти

| Артефакт | Шлях |
|---|---|
| Міграція + entity | `Migrations/…_AddRawMessageIdentity.cs` (+Designer), `RawMessage.cs`, `RawMessageConfiguration.cs`, `IncomingMessage.cs`, `RawMessageIngestor.cs` |
| Ingress | `src/Puluj.Infrastructure/Messaging/{IngressWriter,IngressEnvelope}.cs`, `MessagingOptions.Ingress`; `src/Puluj.Collectors/CollectorIngress.cs`; зміни `TelegramCollector`, `AlertsInUaCollector`, `AlertsInUaHistoryCollector`, `Collectors/DependencyInjection.cs` |
| Raw-writer | `src/Puluj.Messaging/RawWriterHandler.cs`, `IDeliveryHandler.cs` (`AfterCommit`), `SubscriptionConsumer.cs`, `DependencyInjection.cs` (роль) |
| Worker/Compose | `WorkerOptions`, `Program.cs`, `appsettings.json`, `deploy/docker-compose.yml`, `.env.example` |
| Контракти | `topology.json` v3, `asyncapi.yaml`, README; `TopologyRegistryTests` (v3, raw-writer active); `Puluj.Messaging.Tests/Unit/TopologyRegistryTests` |
| Тести | `tests/Puluj.Messaging.Tests/Unit/IngressContractTests.cs` (envelope/payload проти `ingress.received.schema.json`, identity), `Integration/IngressTests.cs` (P04-C01…C06 + gate), `Integration/IdentityMigrationTests.cs` (окремий контейнер: legacy рядки → міграція → backfill, дублікат hash дозволений); оновлення `PipelineFixture`/`MessagingFixture` |
| Docs | ADR-0003 → accepted (identity міграція, bridge-правило `causation` замінено ingress), ADR-0004 (W1a/W1b → P04 tests, W1c межа), ADR-0002 (raw-writer active), README контрактів (v3), `fork-deployment.md` (`MESSAGING_INGRESS_ENABLED`), plan §17; evidence `P04-plan/handoff/crash-evidence.{md,json}`, `P04-build-manifest.json`, TRX |

## Тестова матриця

| Test | Вікно | Сценарій | Assert (committed) |
|---|---|---|---|
| P04-C01 | W1a | Той самий пост опубліковано двічі (re-read після «crash» до commit) | 2 `ingress.received` (різні event_id, той самий correlation), raw 1, 2 `raw.stored` (`is_new` true/false, `causation_id` = відповідний ingress id), archive 4 events, `raw_messages.source_message_key/revision` заповнені |
| P04-C02 | §6.1 | Trigger-помилка на `collector_states` під час `IngressWriter.PublishAsync` | ні outbox, ні checkpoint; без trigger — обидва в одній tx (`last_source_message_id`, `cursor`) |
| P04-C03 | §5.2 | Новий id з тим самим текстом; redelivery того самого id; Telegram edit | 2 raw з однаковим `hash`; redelivery → raw 1, `IngestResult` = існуючий id; edit → окремий raw, key той самий, revision `e…`, той самий correlation |
| P04-C04 | ADR-0003 | alerts `{id}:start` / `{id}:end`, повторний `:end` | 2 raw (різні key, rev `0`), повторний end → raw 2, `raw.stored{is_new:false}` |
| P04-C05 | lane | `enqueue:false` (history) через ingress | outbox lane `history`, raw Pending, `WaitForDrainAsync` повертається після raw-writer, timeout без raw-writer → false |
| P04-C06 | W1c | Telegram edit update до commit | **explicit skip**: потребує MTProto; межа задокументована |
| G-migration | D1 | legacy рядки (`48213`, `48213:e1789466500`, `31:start`, `31:end`) → міграція | key/revision за identity-cases; unique identity; два рядки з однаковим hash вставляються |
| G-ingest | D2 | `RawMessageIngestor` (direct/bridge) з identity | дубль → існуючий id; новий id з тим самим текстом → новий raw |
| Unit | D3 | `IngressEnvelope` проти envelope + `ingress.received` schema; identity Telegram/alerts | valid; `causation_id` null; без `raw_message_id` |

## Кроки

1. Статус: коментар в issue #5 (in_progress), §17 → `in_progress`.
2. Незалежне review плану (`p04_review`) → blocking правки.
3. Міграція/entity/ingestor (D1–D2); `IngressWriter` + envelope (D3); consumer `AfterCommit` + `RawWriterHandler` (D6–D7); `CollectorIngress` і collectors (D4–D5, D8); topology v3, роль, Compose/env (D9).
4. Тести (unit, migration, integration); контрактні; Integration/Processing/Api/Admin/Analytics suites (Infrastructure/Collectors змінено); `dotnet build Puluj.sln`; compose config.
5. Docs/ADR/plan; evidence; незалежне review результату → правки → повторний прогін; handoff; коментар + закриття issue; **commit**.

## Незалежне review плану — p04_review

Вердикт: **approve after fixes**. Blocking → правки внесено до старту:

| # | Finding | Правка |
|---|---|---|
| B1 | `ComputeHash` хешує оригінальний JSON payload; raw-writer отримав би payload з jsonb (нормалізований) → інший `hash` між direct і ingress режимами | `IngressWriter` рахує `content_hash` на боці collector і кладе в payload `ingress.received` (additive поле; schema/fixtures/README доповнити); `RawWriterHandler` бере `payload.content_hash` (fallback — обчислює). Cross-mode тест: один `IncomingMessage` direct і через ingress → однаковий `hash` |
| B2 | `ReprocessService.ResetAsync` тримає ACCESS EXCLUSIVE на `raw_messages` (до 30 хв); INSERT raw-writer з default timeout 30 с → transient → після 5 спроб quarantine | `Messaging:RawWriter:InsertTimeout` (default 25 хв, < broker consumer_timeout 30 хв) для INSERT raw; доставка чекає unacked; тест P04-C07: `LOCK TABLE raw_messages` під час доставки → після зняття raw записано, attempts 1, quarantine 0 |

Non-blocking прийняті: N1 drain без фільтра lane (усі `ingress.received` джерел без raw-writer receipt), timeout → status `history: drain timeout`
+ error-лог (Reset все одно виконується — задокументована межа); N2 upsert `collector_states` лише ненульових полів, edit не рухає
`last_source_message_id`; N3 `regexp_match` у backfill, зняти `IsUnique` з `Hash` у конфігурації, `source_message_id` з legacy або `key`/`key:rev`;
N4 migration test з `Down`; N5 startup-перевірка `Ingress:Enabled` (warning/error у логах + документація); N6 `/dev/ingest` через `CollectorIngress`;
N7 alerts: `:end` для «open in DB» не повторюється в межах сесії; N8 тести AfterCommit-throw → ACK, паралельні raw-writer репліки;
N9 `occurred_at = source_published_at`, `producer = collectors@{instance}`, run через `ProcessingRuns`.

