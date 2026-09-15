# P04 — єдиний ingress, raw-writer та ідентичність повідомлень

Task: [P04 / issue #5](https://github.com/sql-monk/Puluj-g/issues/5).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p04_review`): approve after fixes → B1 (default на identity-колонках),
N1, N6, N7, N8 виправлено → повторний прогін зелений. Rollout не виконувався (default deploy без змін: `Messaging:Ingress:Enabled=false`).
Owner: Claude Code (Opus 5), агент `p04`. Reviewer: план — субагент `p04_review` (approve after fixes; B1 content hash, B2 Reset lock → внесено до старту,
`P04-plan.md`); результат — субагент `p04_review` (approve after fixes, таблиця нижче).
Base commit: `d1bf1b0`; результуючий commit — див. `git log` (цей handoff комітиться разом із кодом; `P04-build-manifest.json` перелічує файли).

## Результат для споживача

- **Identity raw** (ADR-0003 → accepted): міграція `AddRawMessageIdentity` — `raw_messages.source_message_key`/`source_revision` з backfill із legacy
  `source_message_id` (`{id}:e{unix}` → key/revision, інакше revision `0`), unique `(source_id, key, revision)`, `hash` → non-unique індекс схожості,
  legacy unique `(source_id, source_message_id)` лишається. Нові колонки без default (review B1): образ до P04 без `Down` падає гучно, не губить пости.
  `Down` не відновлює unique hash (свідомо). `RawMessageIngestor` конфліктує лише за identity і при повторі повертає **існуючий raw id** (`IsNew=false`);
  новий пост з тим самим текстом зберігається.
- **Producer outbox collectors** (§6.1): `IngressWriter.PublishAsync` — `ingress.received` (root, `causation_id: null`, `correlation_id` UUIDv5, payload з
  `legacy_source_message_id`, `content_hash` на оригінальному JSON, `collector{name,version,instance}`, `checkpoint`) + expected deliveries (raw-writer,
  archive) + upsert `collector_states` (лише передані поля) — **одна транзакція**. `CollectorIngress` (Collectors) перемикає режим за `Messaging:Ingress:Enabled`:
  `false` → direct (`RawMessageIngestor` + окремий `MarkSuccess`, P03 bridge за `Outbox:Enabled`), `true` → ingress. Telegram: checkpoint id з кожним
  повідомленням (edit не рухає id), history — page cursor з останнім stored повідомленням сторінки, drain перед `ResetAsync`; alerts.in.ua: start/end через
  ingress, cursor (active set) з останнім publish циклу, `:end` для «open in DB» не повторюється в межах сесії; history collector — ingress без checkpoint.
  Admin `/dev/ingest` іде тим самим шляхом.
- **Raw-writer** (topology v3, роль Worker `raw-writer`): `RawWriterHandler` — INSERT raw за identity (`ON CONFLICT DO NOTHING` → існуючий id), `raw.stored{is_new}`
  у тій самій tx (`causation_id` = ingress event, той самий correlation, identity з envelope, `producer raw-writer@{instance}`), після commit NOTIFY для
  live/is_new (legacy `ProcessingLoop` без змін), history — Pending до rebuild. INSERT з `Messaging:RawWriter:InsertTimeout` (25 хв) чекає lock `ResetAsync`.
  `SubscriptionConsumer` +`DeliveryResult.AfterCommit` (помилка → лог, ACK).
- Compose `messaging` roles `relay,archive,raw-writer`; collectors і admin отримують `MESSAGING_INGRESS_ENABLED`; `appsettings.json` секції `Messaging:Ingress`,
  `Messaging:RawWriter`; Program.cs — warning, якщо ingress увімкнено без relay/raw-writer у цьому процесі.
- Контракти: `topology.json` v3 (`raw-writer` active), `ingress.received.schema.json` +`content_hash`, fixture, README; asyncapi перегенеровано.

Docs: ADR-0003 → accepted (identity, bridge/ingress causation), ADR-0004 (W1a/W1b → P04 tests, «Реалізація (P04)», межі InsertTimeout/drain), ADR-0002 (v3),
`docs/adr/README.md`, `contracts/messaging/README.md`, `docs/fork-deployment.md` (ingress, rollback-правило), plan §16.1/§17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (4.3.5). Усі під `scripts/with-lock.ps1`.
TRX — `test-results/p04-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests` Unit (run1) | 0 | 20/0/0 |
| `dotnet test tests/Puluj.Messaging.Tests` Integration run1 → run2 | 1 → 0 | 18/5/1 (хибні очікування тестів: v3, мкс, порядок) → 23/0/1 |
| `dotnet test tests/Puluj.Messaging.Tests` (весь проєкт) run3 (після переробки G01) → run4 (після review-правок) | 0 → 0 | **43/0/1** → **43/0/1** (skip — P04-C06 W1c, explicit) |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v3; повторно після правок) | 0 | **54/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` (ingestor/entity/міграція; повторно після правок) | 0 | **32/0/1** (skip — P00 baseline, як раніше) |
| `dotnet test tests/Puluj.Processing.Tests` | 0 | 105/0/0 |
| `dotnet test tests/Puluj.Api.Tests` | 0 | 30/0/0 |
| `dotnet test tests/Puluj.Admin.Tests` | 0 | 56/0/0 |
| `dotnet test tests/Puluj.Analytics.Tests` | 0 | 33/0/0 |
| `dotnet build Puluj.sln` (фінальний) | 0 | 0 warnings |
| `docker compose … [--profile broker] config` | 0 | `Messaging__Ingress__Enabled` у collectors/admin; roles `relay,archive,raw-writer` |

`tests/Puluj.Transport.Spike.Tests` (P02) не запускався (не змінювався); web без змін.

## Race/crash/load evidence

[`P04-crash-evidence.md`](P04-crash-evidence.md) / [`messaging-crash-evidence.json`](messaging-crash-evidence.json): P04-C01 (W1a re-read → 1 raw, `is_new` true/false,
causation), C02 (outbox+checkpoint атомарно, upsert зберігає інші поля), C03 (identity ≠ similarity, edit = revision), C04 (alerts keys, cross-mode hash,
існуючий id при повторі), C05 (history lane, drain), C07 (Reset lock → wait, не quarantine), G01 (AfterCommit-throw → ACK; 2 репліки → 1 raw), G-migration
(Up→Down→Up на legacy рядках, без default). Load не вимірювався (P16).

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | `AddColumn(defaultValue: "")` лишав `DEFAULT ''` → старий writer після Up без Down зберігав би все під identity `('', '')` і тихо губив пости | `ALTER COLUMN … DROP DEFAULT` після backfill; тест: `column_default IS NULL`, INSERT без колонок → `PostgresException`; правило в `fork-deployment.md` | migration test, run4 |
| N1 | `raw.stored` identity перевиводилась з legacy id | `stored.SourceMessageKey/Revision` = з ingress envelope | run4 |
| N6 | admin без `Messaging__Ingress__Enabled` → `/dev/ingest` direct при collectors через ingress | env у сервісі `admin` | compose config |
| N7 | producer `raw.stored` без `@instance` | `RawWriterHandler.Producer = raw-writer@{instance}` (DI) | run4 |
| N8 | header evidence `task: P03` для спільного файлу | `P03+P04` | json |
| N2 | `InsertTimeout` 25 хв при prefetch 10: rebuild довший → перша доставка = attempt, решта чекають; 5 спроб → quarantine | межа в ADR-0004; варіант prefetch 1 для raw-writer — за потребою | docs |
| N3 | drain не рахує quarantined/waived раw-writer → rebuild без цих raw | межа в ADR-0004 (видно у `processing.quarantine`) | docs |
| N4, N5, N9 | лог «ended it» до publish; `HistoryCursor.Stored` рахує й дублі/сервісні; `IngestResult(null,false)` при конфлікті лише legacy-індексу | інформаційне; названо тут | — |

Reviewer підтвердив §16.2: п.2 (одна tx outbox+checkpoint, C02; AfterCommit після commit до ACK), п.3 (ON CONFLICT + READ COMMITTED → `ExistingIdAsync`
бачить рядок конкурента; C03/C04), п.5 (у режимі ingress raw пише лише raw-writer; direct-режим еквівалентний попередньому), п.6 (backfill ≡ `FromLegacy`,
ідемпотентний, Up→Down→Up), п.7 (legacy id збережено, causation/correlation), п.12 (drain по fan-out рядку; evidence = json, race чесно не заявлено).

## Відомі обмеження / невиконані перевірки

- W1c: Telegram edit update між отриманням і commit — лише WTelegram update state (локальний файл), durable spool не робився (P04-C06 explicit skip).
- Реальні collectors end-to-end (MTProto/токен) не запускалися; перевірено `CollectorIngress`/`IngressWriter`, якими вони користуються.
- `_endedFromDatabase` in-memory: після рестарту один повтор `:end` → `is_new:false` (ідемпотентно). Drain timeout → rebuild усе одно (status
  `history: drain timeout`), ризик обробки поза чергою для запізнілих raw. `HistoryCursor.Stored` — інформаційний лічильник (рахує accepted).
- Backfill міграції — один UPDATE (для дуже великої таблиці потрібен batched backfill). Analytics `RawMessageReader` ще парсить legacy id (P15).
- Bridge P03 (`Outbox:Enabled`) лишається fallback; критерій видалення — canary P16. Project card — токен без scope.

## Rollout / rollback / input ownership

Deploy: міграція `AddRawMessageIdentity` застосовується `migrate`-контейнером (додає колонки, backfill, індекси; існуючий direct-шлях працює далі).
Увімкнення: профіль `broker` (`messaging` з `raw-writer`), потім `MESSAGING_INGRESS_ENABLED=true` для collectors/admin. Rollback поведінки — флаг off (collectors
знову пишуть raw напряму з identity); відкат образу до P04 — лише після `Down` міграції. Ownership: `src/Puluj.Collectors` (ingress adapter), `IngressWriter`,
`RawWriterHandler`, міграція/DI/Compose (P04).

## Чекбокси issue #5

- [x] Усі поточні collectors через `CollectorIngress` (+ reconnect/backfill: checkpoint у tx, re-read → `is_new:false`, drain); новий пост з тим самим текстом зберігається (C03), redelivery — ні (C01/C03/C04)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски (W1c, e2e collectors, race реплік) зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (v3, `content_hash`), міграція/rollback (Up→Down→Up тест, без default), конфігурація (Compose/env/appsettings), документація (ADR-0002/0003/0004, README, plan) оновлені

## Наступний task

**P05** — normalizer і rules/structured parser як підписки на `SubscriptionConsumer` (`raw.stored` → `message.normalized` → `parse.completed`), persisted
stage status у `processing.stage_results`, винесення alert-writes з parser; активація підписок → topology v4. Паралельно P13 (admin над quarantine/deliveries,
health) або P15 (analytics на `source_message_key/revision`).
