# ADR-0002 — Topology, registry підписок і семантика «видалити після всіх»

Статус: **accepted** (P02 spike підтвердив declare/bindings/DLQ/lanes на RabbitMQ 4.3; запропоновано P01). Machine-readable джерело: [`contracts/messaging/topology.json`](../../contracts/messaging/topology.json)
(`topology_version` = 1), AsyncAPI — [`asyncapi.yaml`](../../contracts/messaging/asyncapi.yaml). Вимоги: plan §3.2, §4, §6.3, §15.2.

## Контекст

`raw.stored` потрібен нормалізатору, аналітиці й архіву одночасно; три parser-репліки — три виконавці
однієї підписки, а не три розбори. «Усі зацікавлені отримали» не можна реалізувати через один глобальний ACK:
кожна підписка має власну копію та власний ACK. Потрібен реєстр очікуваних підписок, зафіксований версією,
щоб зупинений required consumer лишався «зацікавленим», а readiness/reconciliation могли це перевірити.

## Рішення

### Іменування

| Об'єкт | Шаблон | Приклад |
|---|---|---|
| Exchange | `puluj.events` (topic, durable) | — |
| Routing key | `puluj.{lane}.{event_type}` | `puluj.live.raw.stored` |
| Queue | `puluj.{subscription_id}.{lane}` | `puluj.normalizer.live` |
| DLQ | `puluj.{subscription_id}.{lane}.dlq` | `puluj.normalizer.live.dlq` |
| Binding | queue ← `puluj.{lane}.{event_type}` для кожного event type із `bindings` підписки | `puluj.finalizer.replay` ← `puluj.replay.llm.failed` |

Lane є частиною routing key і назви черги: live/history/replay мають окремі черги та окремі
concurrency quotas (ADR-0007), а `projection` не має replay-черги (shadow generation має окремі проєкції, plan §11).

### Registry (`topology.json`)

- `events.{type}`: `kind` (`event` | `command`), `producer`, `schema`, `schema_version`, `scope`
  (`message` | `aggregate`), `replay_source`, `required_subscriptions`, `optional_subscriptions`,
  для `observations.recorded` — `conditional_subscriptions.by_manifest` (ADR-0005), для команд — `owner`.
- `subscriptions.{id}`: `bindings`, `lanes`, `emits`, `required`, `queue_policy`, `idempotency`,
  `owner_task`, `status` (`planned` → `active` після реалізації; `paused`/`retired` — з audit). З P03 (`topology_version` 2)
  `archive` — `active`, з P04 (`topology_version` 3) — `raw-writer`, з P05 (`topology_version` 4) — `normalizer` і `parser` (тоді `finalizer` і
  `llm-worker` були `paused`, щоб черги існували до реалізації), з P06 (`topology_version` 5) — `finalizer` і `llm-worker` `active`. Черги оголошуються й deliveries очікуються **лише** для `active`/`paused` підписок поточної версії
  (статус після першого insert належить БД — `messaging.subscriptions`, команди `SubscriptionAdmin`).
- `producer_roles`: collectors, watchdog, outbox-relay, reconciliation — публікують через outbox, черг не мають.
- Правила консистентності (тести T03–T09 у `tests/Puluj.Messaging.Contracts.Tests`): кожен event має
  producer, schema і ≥1 required підписку; команда — рівно одного owner; кожен binding/emit — на відомий тип;
  граф `subscription → emits → subscribers` ациклічний; `archive` required для всіх `replay_source` подій;
  `message-analytics`, `archive`, `projection` мають `emits: []` (§6.3 — без lifecycle-of-lifecycle).

### Events vs commands

`event` — факт, кілька підписників (`raw.stored`). `command` — доручення з рівно одним owner
(`llm.requested` → `llm-worker`; `track.expiry.requested` → `track-worker`). Команда нічого не змінює сама:
owner перевіряє `expected_revision`/час і або застосовує зміну, або дає `noop` receipt. Watchdog не є
другим власником стану (plan §7).

### «Видалити після всіх»

- Копія в черзі підписки видаляється після **її власного** ACK; глобального ACK немає.
- Набір очікуваних підписок для конкретної події фіксується `topology_version` в envelope на момент
  публікації; його не змінює кількість запущених процесів. Зупинений required consumer лишається
  зацікавленим: його черга накопичує backlog, оператор бачить «відсутній required consumer».
- Для доменних гілок `observations.recorded` очікуваний набір — з `expected_branches` payload
  (за completion manifest); fan-out не означає очікування кожного типу воркера для кожного поста.
- Журнал `processing.deliveries` (ADR-0006) тримає expected/terminal receipts: `completed`, `noop`,
  `quarantined`, `waived`. Workflow «завершено», коли всі required гілки мають terminal receipt;
  `quarantined` — **не** успіх → `needs_attention`.
- Канонічний оригінал (`raw_messages`) і архів (`messaging.events`) не видаляються разом із транспортними копіями.

### Queue policy

`queue_policies.required`: durable, `ttl: none`, `drop_oldest: false`, `dlq: true`, `max_delivery_attempts: 5`.
Для required черг заборонені message TTL / max-length з drop-head, що тихо викидають недоставлене; при
disk/memory alarm брокера producer отримує backpressure, непідтверджене лишається в outbox, alarm видимий
(ADR-0004 W13).

Broker arguments (закріплено P02, `topology.json.queue_policies.*.broker_arguments`):
`x-queue-type: quorum`, `x-delivery-limit: 4` (= `max_delivery_attempts − 1` redeliveries після crash loop),
`x-dead-letter-strategy: at-least-once` + `x-overflow: reject-publish` (обов'язкова пара для at-least-once
dead-lettering), `x-dead-letter-exchange: puluj.dlx` (direct) + `x-dead-letter-routing-key = <dlq name>` — додає
declarer; DLQ — теж quorum. Delivery-limit (RabbitMQ ≥ 4.3, [Poison Message Handling](https://www.rabbitmq.com/docs/quorum-queues)):
`x-delivery-count` інкрементують `basic.reject(requeue=true)` і crash/закриття каналу з unacked; **не** інкрементують
`basic.nack(requeue=true)` (лише `x-acquired-count`) і consumer timeout. Бізнес-retries та їх ліміт — у `processing.attempts`
через `basic.nack(requeue=true)` (ADR-0001 п.8); `x-delivery-limit` — запобіжник crash loop.

Інваріанти declare для P03: (1) `x-*` queue arguments незмінні після створення — повторний declare з іншими
аргументами дає `PRECONDITION_FAILED`; змінювані параметри (delivery-limit, overflow тощо) задавати **policies**, у
arguments лишати лише `x-queue-type` і DLX routing, або мігрувати чергу через нову назву + transfer; (2) DLX і DLQ
оголошуються **до** основної черги: at-least-once dead-lettering тримає повідомлення в source queue до підтвердження DLQ.

### Readiness і reconciliation

- Readiness воркера = усі required queues **і bindings** своєї `topology_version` існують; інакше not ready
  і alarm «missing required binding». Passive declare черги (spike C08) бачить лише відсутню чергу, **не** drift
  bindings живої черги — bindings перевіряти через management API `/api/queues/{vhost}/{queue}/bindings` (як G00)
  або `queue.bind` idempotent при старті + reconciliation (P03). `mandatory`/confirms не доводять існування
  очікуваних підписок.
- Reconciliation (producer-side роль) звіряє `processing.deliveries` з registry: expected без receipt довше
  за SLO → alarm; receipts від підписок, яких немає в registry поточної версії → аудит.
- Новий consumer/binding отримує лише нові події після активації; минулі — окремим backfill із
  `messaging.events` (не з брокера). Під час backfill durable підписки реєструються **до** публікації.

### Відключення підписки з backlog

Тільки через явний: **drain** (доопрацювати), **transfer** (перемістити в іншу підписку з новим
`subscription_id` і receipts), або **audited waiver** (receipt `waived` з `reason`, `actor`, `waived_at`
на кожну expected delivery). Просте видалення черги заборонене policy та runbook.

### Bridge на legacy NOTIFY

На час compatibility window projection/API можуть перетворювати `track.changed` → `TrackUpserted|TrackClosed`,
`alert.changed` → `AlertChanged`, `observations.recorded` → `TargetCreated`, `raw.stored` → `RawMessageStored`
(`topology.json.bridge`). NOTIFY не є transport event і не має receipts; дата вимкнення — P11.

## Альтернативи

- Одна черга на event type з `x-consumer-groups`: RabbitMQ такого не має; streams — інша модель (без per-message DLQ).
- Fanout exchange на кожен event type: більше exchange-об'єктів без переваг; topic з lane у ключі простіший.
- Глобальний лічильник «N ACK з M» у брокері: не існує; реалізується журналом deliveries у БД.

## Наслідки

- Кількість черг = підписки × lanes (зараз 11 × ≤3). Це нормально для RabbitMQ; declare — idempotent при старті.
- Кожна нова підписка = зміна `topology_version` + запис у registry + тести; без цього readiness не пропустить.
- `projection` без replay lane означає, що shadow generation потребує окремих проєкцій (P14).

## Відкрите

| Питання | Задача |
|---|---|
| ~~Prefetch за роллю, batch confirms у relay~~ — done P03: `Messaging:Consumer:Prefetch` (default 10), relay публікує батч паралельно з confirmation tracking (200/батч у G01) | P03 |
| ~~Relay розрізняє `PublishException`~~ — done P03: `IsReturn` → `last_error = basic.return …`, retry лише через `UnroutableRetry` після re-declare; nack/timeout → backoff/lease | P03 |
| Readiness: required queues — passive declare (`TopologyDeclarer.MissingRequiredQueuesAsync`); drift bindings — **idempotent re-declare** у кожному проході reconciliation (P03-C08), не management API; policies не використовуються — усі параметри в `x-*` arguments, зміна = нова назва черги + transfer | P03 → P13/P16 (management API check) |
| ~~Runtime registry loader, reconciliation job, receipts~~ — done P03: `TopologyRegistry` (embedded `topology.json`), `TopologyRegistrar` (`messaging.topology_versions`/`subscriptions`), `ReconciliationService`, `processing.deliveries` | P03 |
| Health endpoint / heartbeat-інтеграція readiness брокера (зараз — логи + метрики) | P13 |
| Чи потрібна окрема `history` черга для `projection` (зараз так) чи достатньо live з event-time | P11 |
| Retire NOTIFY bridge | P11 |
