# P01 — ADR транспорту, topology, identities, контракти подій/runs, SLO/retention proposal

Task: [P01 / issue #3](https://github.com/sql-monk/Puluj-g/issues/3).
Status: **done** — локальна реалізація, тести та незалежне review завершені; commit/PR/rollout не виконувалися.
Owner: Claude Code. Reviewer: незалежний субагент `p01_review` (план і результат окремо).
Base commit: `6822541`; результат — локальні незакомічені файли (перелік із SHA-256 у `P01-build-manifest.json`).

## Що змінилось для користувача/споживача

Поведінка runtime **не змінилась**: `src/` без змін, NOTIFY/claims/lease працюють як у P00. Додано:

- **ADR-0001…0007** (`docs/adr/`, статус `proposed`): транспорт RabbitMQ topic exchange + quorum queues з
  альтернативами; topology/registry підписок, DLQ, «видалити після всіх» = terminal receipts усіх required
  гілок; identities `(source_id, source_message_key, source_revision)` з правилом для Telegram (edit = revision)
  і alerts.in.ua (start/end = фаза, не ревізія), чотири шкали часу, `MAJOR.MINOR` сумісність; outbox/inbox,
  ACK після commit, **crash windows W1–W14** із таблицею покриття §13 і test IDs для P02–P06/P15; runs/generations,
  completion manifest, terminal outcomes `completed/noop/quarantined/waived`, workflow stages; логічна модель
  `messaging.*`/`processing.*`/`analytics.message_*`; SLO orієнтири з P00 baseline, quotas, retention — proposal.
- **Machine-readable контракти** (`contracts/messaging/`): `topology.json` (14 event types, 11 subscriptions,
  4 producer roles, queue policies, bridge на `PulujEvent`), `completion-manifest.json`, JSON Schema 2020-12
  envelope + 14 payload schemas + common defs, `asyncapi.yaml` (генерується `tools/gen-asyncapi.py`), 23 valid /
  15 invalid fixtures, 2 sequences, `identity-cases.json`, `compatibility.json`, README з інструкцією додавання.
- **Тести** `tests/Puluj.Messaging.Contracts.Tests` (T01–T15): fixtures ↔ schemas (позитивні й негативні),
  hard-coded очікуваний registry, команди з одним owner, archive для replay-source, ациклічний граф, `emits: []`
  для audit/analytics/projection, queue policy required, manifest ↔ outcomes ↔ категорії, causal chain `*.changed`,
  сумісність major_equal + additive/breaking, republish identical, identity Telegram/alerts, AsyncAPI ↔ registry.

Контракти/міграції/flags: schema БД, DTO, конфігурація, Compose, `.env` — **без змін**. `Directory.Packages.props`:
+`JsonSchema.Net` 9.4.0 (лише тестовий проєкт). `Puluj.sln`: +тестовий проєкт. Docs: `docs/README.md` (покажчик),
`plan-message-platform.md` §14 (ADR/contracts), §16.1 (команда тестів), §17 (P01).

## Рішення §15.1, закриті P01

| Рішення | Результат |
|---|---|
| Event schemas та маршрутизація | ADR-0002/0003, `topology.json`, schemas, routing `puluj.{lane}.{event_type}` |
| Completion semantics | ADR-0005, `completion-manifest.json`: manifest гілок за категорією observation, terminal outcomes |
| Контракт runs (P14) | ADR-0005: run/generation/lane, state machine, «старі результати не перезаписуються» |
| Analytics lifecycle schema (P15) | ADR-0006 `analytics.message_lifecycle` upsert `(raw_message_id, run_id)`, `unavailable`, out-of-order fixture |
| Retention/SLO | ADR-0007 — **proposal**; абсолютні SLO — P16, retention/disk — P03 |

Не вирішено P01 (початкове припущення + власник): broker/client versions, queue arguments, deployment profile — P02;
unique hash migration, spool, payload storage — P04; partition boundaries — P09; індекси — P03/P15.

## Тести (команда → exit code → результат → середовище)

Build: `dotnet build Puluj.sln` → exit 0, 0 warnings. SDK 10.0.401, Debug/net10.0, Windows.

| Команда | Exit | Passed / Failed / Skipped | TRX |
|---|---|---|---|
| `dotnet test tests/Puluj.Messaging.Contracts.Tests/Puluj.Messaging.Contracts.Tests.csproj` (фінальний, після review fixes) | 0 | **54 / 0 / 0** | `test-results/p01-Puluj.Messaging.Contracts.Tests.trx` |
| `dotnet test tests/Puluj.Processing.Tests/...` `--no-build` | 0 | 98 / 0 / 0 | `p01-Puluj.Processing.Tests.trx` |
| `dotnet test tests/Puluj.Analytics.Tests/...` `--no-build` | 0 | 33 / 0 / 0 | `p01-Puluj.Analytics.Tests.trx` |
| `dotnet test tests/Puluj.Admin.Tests/...` `--no-build` | 0 | 56 / 0 / 0 | `p01-Puluj.Admin.Tests.trx` |
| `dotnet test tests/Puluj.Api.Tests/...` `--no-build` | 0 | 28 / 0 / 0 | `p01-Puluj.Api.Tests.trx` |
| `dotnet test tests/Puluj.Integration.Tests/...` `--no-build` (Testcontainers PostGIS, одноразова БД) | 0 | 26 / 0 / 1 explicit baseline skip (`PULUJ_RUN_BASELINE` opt-in) | `p01-Puluj.Integration.Tests.trx` |

Разом: **295 passed, 0 failed, 1 explicit skip**. Проміжні прогони не приховані: `p01-contracts-run1.trx` —
**2 passed / 51 failed** через помилку test harness (`JsonSchema.FromFile` сам реєструє схему в
`SchemaRegistry.Global`; повторна `Register` кидала в конструкторі fixture) і невірні шляхи fixtures;
виправлено переходом на collection fixture. Другий прогін 45/8 — помилки очікуваних error paths у invalid
fixtures та `:e` vs `:end` у T13; виправлено. Це помилки нових тестів/fixtures, не production.

Не запускалось і не заявляється: RabbitMQ (transport ще не в checkout; harness — P02), реальні LLM provider,
load/chaos, frontend (`web/` не змінювався).

## Review findings → виправлення → повторна перевірка

- **Review плану** (7 blocking, B1–B7): crash windows розширено до W1–W14 з покриттям §13; явний registry;
  identity phase ≠ revision і межі відновлення; доказові тести з hard-coded очікуваннями; ADR-0006/queue policy —
  логічний рівень; без runtime-бібліотеки (JsonSchema.Net test-only); run/analytics контракти конкретизовано.
- **Review результату** (approve after fixes, B1–B5 + N1–N10): W1c межа Telegram edit переписана за кодом
  (`WithUpdateManager(..., ".updates")`); W6a розділено на два durable стани; compatibility case ↔ fixture;
  alerts `raw_payload` = форма `Wrap()`; `alert.changed` перев'язано на ланцюг alerts-start і T09 перевіряє
  causal chain. Повторний прогін контрактних тестів: 54 passed. Деталі — [`P01-plan.md`](P01-plan.md).
- Підсумкове підтвердження reviewer після fixes — див. коментар в issue / кінець `P01-plan.md`.

## Відомі обмеження / невиконані перевірки

- Усі ADR `proposed`; жодна підписка не `active`. Приймання рішень транспорту — після spike P02 (go/no-go).
- Fixtures — синтетичні приклади контракту, не реальні raw samples; envelope/payload форму RabbitMQ (headers vs
  body) закріпить P02.
- SLO числа — орієнтири; retention — proposal без оцінки обсягу (метод у ADR-0007).
- `asyncapi.yaml` перевіряється regex-скануванням (без YAML-парсера/AsyncAPI validator); генератор — Python.
- GitHub Project status: токен `gh` без scope `project` — статус картки не змінено; статус зафіксовано коментарями
  в issue та §17.

## Rollout / rollback / input ownership

Rollout не потрібен: зміни docs/contracts/tests. Rollback = видалити нові файли й повернути 4 змінені
(`Directory.Packages.props`, `Puluj.sln`, `docs/README.md`, `docs/plan-message-platform.md`). Ownership:
`contracts/messaging/` і `docs/adr/` — transport/data потік; зміни — лише разом із тестами і `topology_version`.

## Чекбокси issue #3

- [x] Machine-readable schemas, compatibility fixtures, registry очікуваних підписок, review crash windows
- [x] Необхідні тести виконані на актуальній збірці (PostGIS integration виконано; RabbitMQ не потрібен для P01 — явно)
- [x] Незалежне code review завершене; blocking findings виправлені
- [x] Контракти, конфігурація (без змін), документація оновлені в межах задачі

## Наступний task

**P02** — RabbitMQ/.NET spike з Testcontainers: реалізувати crash tests P02-C01…C09 з ADR-0004, перевірити
fan-out/competing consumers/offline consumer за `topology.json`, закріпити версії й queue arguments,
go/no-go. Паралельно P07 (catalog) може стартувати від `common.schema.json` `eventKindCode`/`observation`.
