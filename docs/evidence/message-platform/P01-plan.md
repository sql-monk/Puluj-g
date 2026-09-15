# P01 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/3. Початок: 2026-09-15.
Base: `6822541` (master, clean working tree). Залежність P00 — done, evidence у `P00-handoff.md`.
Виконавець: Claude Code; незалежний reviewer: субагент `p01_review` (план і результат окремо).

## Аналіз задачі

P01 — хвиля 1 «ADR і transport spike», частина без брокера: **рішення й контракти**, на які
спираються P02 (RabbitMQ spike), P03 (schema/inbox/outbox), P07 (catalog), P14 (runs), P15 (analytics).
Код транспорту, міграції та Compose **не входять** до обсягу: P01 має дати machine-readable контракти,
registry очікуваних підписок, compatibility fixtures і перевірку crash windows (§3–6, §15.1).

Що вже є в коді й що враховують рішення:

- `PulujEvent` — NOTIFY payload `{type,id,at}` без event_id/correlation/version; best-effort, без ACK.
  Лишається для API/SignalR під час bridge (§11); ADR-0002 фіксує mapping на нові події.
- Raw identity: unique `(source_id, source_message_id)` **і** unique content `hash`
  (`ix_raw_messages_hash`). Telegram кодує редакцію в id: `"{id}:e{editUnix}"` — справжня ревізія
  того самого поста. alerts.in.ua кодує **фазу**: `"{uid}:start"` і `"{uid}:end"` — два різні source
  messages з різним payload, не ревізії. `EndAsync` ставить `PublishedAt = now`, тому content hash
  повторного `:end` завжди інший і dedup тримається лише на `(source_id, source_message_id)`.
  Telegram checkpoint не просувається для edits (`isEdit ? null : m.id`), backfill іде за `min_id`:
  edit update, для якого `IngestAsync` не закомітився, DB checkpoint не відновлює; лише WTelegram
  `.updates` state (локальний файл, не durable за §6.1) — межа відновлення до P04.
- Один pipeline на raw з `ProcessingStatus`, claim/lease `ClaimLease` 5 хв **без fencing token**.
- Analytics — та сама БД, схема `analytics` (окремий migrations history). Отже inbox/outbox для
  analytics живуть у тій самій PostgreSQL без distributed transaction.
- P00 виміряв serial ceiling Store (1.34× на 4 workers) і live p95 ×4.94 при history: SLO proposal
  мусить це цитувати як baseline, а не як ціль.

Рішення §15.1, які закриває P01: **event schemas та маршрутизація**, **completion semantics**
(manifest очікуваних гілок + terminal outcomes), **контракт runs/generations** (для P14),
**analytics lifecycle schema** (для P15), **retention/SLO proposal** (пропозиція, підтвердження — P03/P16).

Що P01 **не вирішує** (фіксує початкове припущення і власника): broker/client versions, queue
arguments і deployment profile — P02; доля unique hash index, spool для non-replayable джерел,
identity для нових джерел — P04; partition boundaries — P09; індекси таблиць — P03/P15 за EXPLAIN.

## Registry: event types і subscriptions (зміст `topology.json`)

Kind `event` — факт, може мати кількох підписників; `command` — рівно один owner-subscription.
Routing key: `puluj.{lane}.{event_type}`; lane ∈ `live|history|replay`.

| Event type | Kind | Producer (subscription/role) | Required subscriptions |
|---|---|---|---|
| `ingress.received` | event | collectors (producer-side outbox) | `raw-writer`, `archive` |
| `raw.stored` | event | `raw-writer` | `normalizer`, `message-analytics`, `archive` |
| `message.normalized` | event | `normalizer` | `parser` |
| `parse.completed` | event | `parser` | `finalizer` |
| `llm.requested` | command | `parser` | `llm-worker` (owner) |
| `llm.completed` | event | `llm-worker` | `finalizer` |
| `llm.failed` | event | `llm-worker` | `finalizer` |
| `observations.recorded` | event | `finalizer` | за manifest: `track-worker` / `alert-worker` / `incident-worker`; завжди `archive` |
| `message.analysis.completed` | event | `finalizer` | `message-analytics`, `archive` |
| `track.changed` | event | `track-worker` | `projection`, `message-analytics` |
| `alert.changed` | event | `alert-worker` | `projection`, `message-analytics` |
| `incident.changed` | event | `incident-worker` | `projection`, `message-analytics` |
| `track.expiry.requested` | command | `watchdog` (producer-side) | `track-worker` (owner) |
| `alert.expiry.requested` | command | `watchdog` (producer-side) | `alert-worker` (owner) |

Subscriptions (consumer roles): `raw-writer`, `normalizer`, `parser`, `llm-worker`, `finalizer`,
`track-worker`, `alert-worker`, `incident-worker`, `projection`, `message-analytics`, `archive`.
Producer-side roles без черги: `collectors`, `watchdog`, `outbox-relay`, `reconciliation`.
Кожна required subscription має `dlq: true`, `ttl: none`, `drop_oldest: false`; `message-analytics`,
`archive` і `projection` мають `emits: []` (§6.3). Для кожної підписки — статус `planned` (жодна не active до P02/P03).

## Артефакти

| Артефакт | Шлях | Призначення |
|---|---|---|
| ADR index | `docs/adr/README.md` | Формат, статуси, перелік; mermaid inline — прийнятий формат діаграм для P01 |
| ADR-0001 | `docs/adr/ADR-0001-transport.md` | RabbitMQ topic exchange + quorum; альтернативи NATS/PostgreSQL; deployment profile як початкове припущення (P02); без Compose-змін до P02 |
| ADR-0002 | `docs/adr/ADR-0002-topology-subscriptions.md` | Іменування exchange/routing keys/queues, registry, required/optional, DLQ, «видалити після всіх», topology versioning, readiness, drain/transfer/waiver, mapping `PulujEvent` bridge |
| ADR-0003 | `docs/adr/ADR-0003-identities-versioning.md` | `event_id` (UUIDv7, стабільний при republish), raw identity `(source_id, source_message_key, source_revision)` з правилом для Telegram/alerts.in.ua, hash як similarity, timestamps (source published / received / occurred / published / processing), `traceparent` W3C, schema_version semver + additive rule (без `additionalProperties:false` на верхньому рівні), pipeline/topology versions, payload limits як proposal |
| ADR-0004 | `docs/adr/ADR-0004-delivery-guarantees.md` | Outbox/inbox, ACK після commit, at-least-once, **crash windows W1–W14** із sequence diagrams, таблиця «сценарій §13 → вікно → test ID (P02/P03)»; fencing token для attempts; `request_id` у `llm.requested` |
| ADR-0005 | `docs/adr/ADR-0005-runs-completion.md` | Runs: `run_id`/`generation_id`, lane, state machine `created→running→paused→verified→promoted\|rolled_back\|cancelled`, checkpoints, «старі результати не перезаписуються»; completion manifest, terminal outcomes `completed/noop/quarantined/waived`, workflow status, analysis terminal ≠ domain ≠ analytics catchup |
| ADR-0006 | `docs/adr/ADR-0006-data-model.md` | **Логічна** модель `messaging.*`, `processing.*`, `analytics.message_*`: таблиці, natural/unique keys, ownership, retention class, rebuildable; lifecycle stages enum з `unavailable`, upsert key, out-of-order reconciliation; колонки — proposal для P03/P15, індекси не фіксуються |
| ADR-0007 | `docs/adr/ADR-0007-slo-retention-proposal.md` | SLO orientирs із P00 baseline, рівні quota live/replay (queue/DB/CPU/LLM), retention per table, deletion eligibility, disk/backup — proposal |
| AsyncAPI | `contracts/messaging/asyncapi.yaml` | Channels/operations/bindings, producer/consumer ownership, посилання на схеми |
| Envelope schema | `contracts/messaging/schemas/envelope.schema.json` | JSON Schema 2020-12 §5.1; per-event умовні required (`raw_message_id`, `aggregate_id/revision`) |
| Payload schemas | `contracts/messaging/schemas/events/*.schema.json` | Один файл на event type з таблиці вище |
| Topology registry | `contracts/messaging/topology.json` | Таблиця вище у machine-readable формі; queue policy абстрактна, broker args — P02 |
| Completion manifest | `contracts/messaging/completion-manifest.json` | Очікувані гілки за analysis outcome `completed/no_facts/unsupported/needs_review/failed`; terminal outcomes |
| Fixtures | `contracts/messaging/fixtures/{valid,invalid}/*.json`, `compatibility.json` | Приклад кожного event type; identity fixture на Telegram original/edit та alerts start/end; republish same `event_id`; analytics out-of-order pair; additive/incompatible; помилкові envelopes із причиною |
| Тести | `tests/Puluj.Messaging.Contracts.Tests/` | xUnit + `JsonSchema.Net` (test-only); контракти через `Content Link` з `contracts/messaging/` |
| Docs | `docs/README.md`, `docs/plan-message-platform.md` §16.1/§17, `contracts/messaging/README.md` | Покажчики, команда тестів, статус P01, інструкція «додати event/subscription» |
| Evidence | `docs/evidence/message-platform/P01-{plan,handoff}.md`, `test-results/`, `P01-build-manifest.json` | План, review, handoff §16.4, TRX, SHA production assemblies |

Без `src/`-проєкту: контракт = JSON, runtime-типи визначить P02/P03 після spike (B6 review).

## Crash windows (ADR-0004)

| ID | Сценарій (§13) | Що падає / де |
|---|---|---|
| W1 | Collector crash до/після durable checkpoint | між ingest/outbox і checkpoint; межа: Telegram edits не в checkpoint |
| W2 | Raw-writer commit без publish | outbox committed, relay ще не публікував |
| W3 | Relay після confirm без mark published | duplicate publish; inbox поглинає |
| W4 | Consumer до commit | redelivery; повторна робота без ефекту |
| W5 | Consumer після commit до ACK | redelivery; inbox → ACK без бізнес-ефекту |
| W6 | DLQ transfer failure та replay із DLQ після виправлення | at-least-once dead-lettering; admin retry зберігає зв'язок зі спробою |
| W7 | Broker restart / loss of quorum / delayed confirm | outbox лишається unconfirmed; retry після timeout; late confirm |
| W8 | LLM lease takeover, late result | fencing token; late result не перезаписує новішу версію |
| W9 | Topology change з backlog; paused/removed subscription; audit waiver | drain/transfer/waiver → terminal `waived` |
| W10 | Публікація до запуску consumer; consumer offline, інші працюють | durable queue зберігає backlog; наздоганяння |
| W11 | Unroutable message / відсутня required binding | readiness + reconciliation; не «успіх» |
| W12 | DB outage у consumer/relay між кроками §6.2 | lease expiry; no partial commit |
| W13 | Broker disk limit / blocked publisher | backpressure в outbox, alarm; нічого не викидається |
| W14 | Duplicate deliveries / out-of-order між чергами (analytics раніше raw) | upsert часткового запису + reconciliation |

## Кроки

1. **Статус.** GitHub Project «Puluj-g refactoring» → P01 `In Progress` (потребує scope `project`
   у `gh`); коментар в issue; §17 → `in_progress`.
2. **Незалежне review плану** — виконано; findings B1–B7 внесено (розділ нижче).
3. **ADR-0001…0007** українською, кожен: контекст → рішення → альтернативи → наслідки → відкрите
   і хто закриває (task ID). Посилання на офіційні джерела RabbitMQ з §3.1.
4. **Crash windows W1–W14** (ADR-0004): стан БД/брокера до й після падіння, хто повторює, що поглинає
   повтор, що видно оператору, test ID для P02/P03; таблиця покриття §13.
5. **Schemas + registry + manifest + fixtures** за таблицями вище.
6. **Тести** (`tests/Puluj.Messaging.Contracts.Tests`, xUnit, `JsonSchema.Net` test-only):
   - T01 кожен valid fixture проходить envelope schema + payload schema свого `event_type`;
   - T02 кожен invalid fixture падає, і повідомлення містить очікуваний `reason`;
   - T03 hard-coded перелік event types і required subscriptions (таблиця вище) присутній у registry;
   - T04 усі bindings/emits — на відомі types; кожен event має producer і schema file;
   - T05 кожна `command` має рівно один owner; `event` — ≥1 required subscription;
   - T06 archive є required для всіх подій, з яких будується replay (позначка `replay_source`);
   - T07 граф `subscription → emits → subscriptions` без циклів; `message-analytics`/`archive` `emits: []`;
   - T08 required subscriptions: `ttl: none`, `drop_oldest: false`, `dlq: true`;
   - T09 manifest: кожен analysis outcome має рядок; кожна гілка — required subscription;
     `message.analysis.completed` очікуваний для всіх outcomes;
   - T10 compatibility: additive fixture проходить стару схему; видалене required/змінений тип — падає;
     невідома major → `Quarantine` (визначено у `compatibility.json`), не exception;
   - T11 per-event умовні required: `ingress.received` без `raw_message_id`; `raw.stored`+ з ним;
     `*.changed` з `aggregate_id`/`aggregate_revision`;
   - T12 republish fixture: той самий `event_id`/`published_at`, інший transport timestamp → envelope ідентичний;
   - T13 identity fixtures: Telegram original/edit, alerts start/end відображаються у `(key, revision)` за ADR-0003;
   - T14 AsyncAPI посилається лише на існуючі schema files і всі event types registry;
   - T15 analytics out-of-order sequence: обидві події валідні, один raw, частковий upsert без перезапису.
7. **Build і запуск.** `dotnet build Puluj.sln`; `dotnet test tests/Puluj.Messaging.Contracts.Tests`;
   unit suites Processing/Analytics/Admin/Api; integration — лише за наявності Docker, інакше
   явний пропуск у handoff. TRX у `test-results/`; SHA production assemblies у `P01-build-manifest.json`
   (зміна `Directory.Packages.props` не повинна змінити production збірки).
8. **Docs.** `docs/README.md` → ADR/contracts; §16.1 нова команда; §17 P01; `contracts/messaging/README.md`.
9. **Незалежне review результату** за §16.2 п.1,2,3,6,7,12 + покриття §13 crash windows.
   Blocking → виправити → повторити тести.
10. **Handoff §16.4** у `P01-handoff.md` з чекбоксами issue #3; коментар в issue; Project → `Done`;
    §17 → `done`. Commit/PR — не виконуються без вказівки; зміни локальні, як у P00.

## Самоперевірка плану

- P01 не впроваджує брокер, міграції, DI, Compose чи runtime-бібліотеку: усе це — P02/P03.
  Єдина зміна поза docs/contracts/tests — `Directory.Packages.props` (тестовий пакет).
- «Registry очікуваних підписок» — machine-readable `topology.json`; тести T03–T09 перевіряють
  його наявність і консистентність проти hard-coded очікувань, не лише самопосилання.
- Compatibility fixtures містять негативні випадки та невідому major версію.
- ADR розрізняє *прийнято в P01* / *початкове припущення, закриває Pxx*.
- Raw identity узгоджена з реальними collectors: phase ≠ revision; межі відновлення названі.
- SLO — proposal з цитуванням P00 baseline; абсолютні числа не «затверджуються» P01.
- RabbitMQ/PostGIS тести для P01 не потрібні; це записано явно.

## Незалежне review плану — p01_review

Blocking findings і як вони внесені:

1. **B1** crash windows не покривали §13 (offline consumer, unroutable/missing binding, DB outage,
   disk limit, delayed confirm, replay із DLQ, waiver) → розширено до W1–W14 + таблиця покриття.
2. **B2** не було переліку event types/subscriptions → додано таблицю registry, включно з archive,
   projection, watchdog commands `*.expiry.requested`, producer-side roles без черги.
3. **B3** alerts `:start/:end` — фаза, не ревізія; `EndAsync` кладе `now`; edits не в checkpoint →
   аналіз виправлено; ADR-0003 фіксує key/revision mapping для обох collectors і межі відновлення;
   доля unique hash index лишається за P04.
4. **B4** тести могли пройти при неповному registry → T03 hard-coded перелік, T09 manifest per outcome,
   T07 `emits: []`, T08 queue policy, T10 compat із quarantine, T11 conditional required, T12 republish,
   T13 identity, T14 AsyncAPI.
5. **B5** над-специфікація → ADR-0006 логічний рівень без індексів; queue policy абстрактна;
   deployment profile — початкове припущення P02.
6. **B6** runtime-бібліотека поза scope → `src/`-проєкт вилучено; `JsonSchema.Net` лише в тестах;
   контракти читаються з `contracts/messaging/` через Content Link.
7. **B7** run/analytics контракти не конкретизовані → зміст ADR-0005/0006 розписано (state machine,
   lifecycle stages, upsert key, reconciliation, quota levels).

Non-blocking внесено: timestamps в envelope, `traceparent`/UUIDv7, payload limits proposal, W8 fencing
і `request_id`, `PulujEvent` mapping, mermaid inline, build manifest, чекбокси issue в handoff.

## Незалежне review результату — p01_review

Вердикт першого проходу: **approve after fixes**. Blocking findings і виправлення:

1. **B1** W1c описував межу відновлення Telegram edit хибно («між ingest і checkpoint»); насправді після
   commit `IngestAsync` втрачати нічого, а WTelegram тримає update state у `SessionPath + ".updates"` →
   переписано в ADR-0004/ADR-0003/плані: вікно — edit update без committed ingest; `.updates` не durable за §6.1.
2. **B2** W6a таблиця vs діаграма описували різні durable стани → розділено на W6a-1/W6a-2; джерело істини
   quarantine — `processing.quarantine`, broker DLQ — операційна копія.
3. **B3** compatibility case «1.3» посилався на fixture «1.1» → виправлено; T10 тепер звіряє case ↔ fixture.
4. **B4** alerts fixtures мали `raw_payload.type`, а `Wrap()` пише `{kind, at, alert}` → виправлено; T13 перевіряє.
5. **B5** `alert.changed` мав causation на observations без гілки `alert-worker` → окремий ланцюг від
   `ingress.received.alerts-start` (новий `observations.recorded.alerts-start.json`); T09 перевіряє causal chain
   кожного `*.changed` (producer ∈ `expected_branches` події-causation, той самий correlation, observation_ids ⊆).

Non-blocking N1–N10 внесено (T15, access-path hints, ERD, terminal states run, унікальні raw/extraction id у
fixtures, W13/ADR-0007 backpressure, spans, assert для `:end`). Повторний прогін: 54 passed.

Повторна перевірка reviewer після fixes: B1–B5 закриті, N1–N10 внесено — **approve**.
Фінальні результати й handoff — `P01-handoff.md`.
