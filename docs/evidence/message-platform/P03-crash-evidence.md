# P03 — crash evidence (ADR-0004 → P03-C01…C10, gate G01–G05)

Джерело чисел: `P03-crash-evidence.json` (пишеться тестами `tests/Puluj.Messaging.Tests/Integration` наприкінці прогону; кожне значення
стоїть після відповідного assert на committed стан БД). Середовище: Windows 11, .NET SDK 10.0.401 / runtime 10.0.12, Docker 29.8.0,
`postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (server 4.3.5), `RabbitMQ.Client` 7.2.2, Testcontainers 4.15.0. TRX:
`test-results/p03-integration-run1.trx` (15/15, до review), `p03-integration-run2.trx` (після review-правок), `p03-unit-run1.trx`
(16/17: `Parsing_keeps_unknown_fields…` очікував текст «event_id» у помилці десеріалізації — хибне очікування тесту, виправлено),
`p03-unit-run2.trx` (17/17), `p03-unit-run3.trx` (після review-правок). Усі команди — під `scripts/with-lock.ps1`.

Конфігурація тестів (`MessagingFixture.Options`): relay confirm timeout 2 s, lease 3 s, unroutable retry 0.5 s; consumer prefetch 10,
backoff 100 ms…500 ms; reconciliation overdue 0 (кожна expected без receipt — overdue), outbox grace 1 s, inbox retention 1 s.

| Test | Вікно ADR-0004 | Сценарій | Committed outcome (assert) | Результат |
|---|---|---|---|---|
| P03-C01 | W1b/W2 | Ingest з bridge без relay; потім relay і archive consumer | raw 1 + outbox 1 unconfirmed + expected `archive` 1 в одній tx; `normalizer`/`message-analytics` (planned) не очікуються; envelope v2, `causation_id == event_id`, `producer raw-writer@p03-test`; reconciliation бачить outbox age 1.2 s і 1 overdue; relay 1/1; events 1, receipt `completed`, attempt `succeeded` | pass |
| P03-C02 | W2 + lease (N3) | Рядок leased «мертвим» relay на 1.5 s | пасс relay: leased 0; після lease: leased 1, confirmed 1, `lease_owner` NULL, transport attempts 2; той самий `event_id` в events | pass |
| P03-C03 | W3/W12b | Relay crash після confirm до mark (`markConfirmed:false`), другий прохід після lease | 2 публікації одного `event_id` (queue 2); consumer: delivered 2, inbox 1, events 1, receipt 1, duplicates 1 | pass |
| P03-C04 | W6a-1/W6a-2 | Handler завжди падає, max attempts 3; crash до commit receipt; crash після commit до nack | attempts failed 3 (без 4-ї), receipt `quarantined/attempts_exhausted`, quarantine 1 open, inbox `quarantined`, events 0; deliveries seen 5; DLQ 1 → DlqConsumer ACK без нового quarantine (quarantine rows 1, DLQ 0) | pass |
| P03-C05 | W6b | Quarantine (max 2) → outbox видалено, events 0 → `SubscriptionAdmin.RetryAsync` → relay → здоровий consumer; stale DLQ-копія | redelivery row `target_queue=puluj.archive.live`, receipt назад expected з actor; quarantine `retried` + `retry_outbox_id`; після relay: receipt `completed`, events 1, нова attempt `succeeded` з `retry_of_attempt_id` = остання стара, 2 старі `superseded`; DLQ 0, quarantine rows 1 | pass |
| P03-C06 | W9a | Подія з `topology_version=1` вставлена в outbox напряму (expected set v1 порожній — archive був planned) поруч із v2-подією через bridge | overdue лише v2-подія; deliveries v1-події 0; registry має рядки v1 (`planned`) і v2 (`active`). Межа тесту: доводить, що reconciliation не очікує ретроактивно (expected рядки — durable з моменту публікації), а не обчислення expected set під v1 кодом | pass |
| P03-C07 | W9b | `archive` → paused, 3 події, relay; waiver | expected 3, backlog у черзі 3, overdue 3; `WaiveAsync` → 3 `waived` з reason/actor, overdue 0, `waiver` jsonb з actor; статус paused → active | pass |
| P03-C08 | W11 | `QueueUnbind` archive.live ← `puluj.live.raw.stored`; relay; reconciliation re-declare | relay: leased 1, confirmed 0, unroutable 1, `last_error` містить `basic.return` (unroutable), queue 0; після re-declare relay confirmed 1; events 1 | pass |
| P03-C09 | W12a | Handler кидає `NpgsqlException` на 1-й apply (DB outage до commit) | attempts: failed 1 (error містить «DB outage»), succeeded 1; requeued 1; events 1; inbox `completed`; quarantine 0 | pass |
| P03-C10 | W14a | Той самий envelope опубліковано 3× напряму | delivered 3, duplicates 2, inbox 1, events 1, receipt 1, attempts 1 | pass |
| G01 | gate хвилі 2 | 200 ingest → 1 relay batch → 2 репліки archive | relay leased/confirmed 200/200; replica_1 100, replica_2 100; events 200; duplicates 0; attempts succeeded 200 | pass |
| G02 | issue: archive/replay source ≠ outbox | 2 archived + 1 confirmed без архіву; grace минув; cleanup; inbox retention | outbox deleted 2, kept 1 (без archive receipt); events 2 з повними envelope; inbox deleted 2, quarantined лишився | pass |
| G03 | ADR-0002 reconciliation | expected без receipt, receipt від невідомої підписки, open quarantine | overdue 1 (`archive`), unknown `[ghost-subscription]`, quarantine 1, outbox unconfirmed 1 (age > 1 s), declare failed 0, missing required queues 0 | pass |
| G04 | bridge атомарність | дубль `(source, source_message_id)`; trigger-помилка на insert deliveries; history load | дубль: raw 1, outbox 1; помилка → `PostgresException`, raw не зберігся (1), outbox 1; `enqueue:false` → lane `history`, routing `puluj.history.raw.stored`, 2 відкриті runs | pass |
| G05 | §5.1 несумісна schema | `schema_version 2.0`, невідомий `event_type`, не-JSON | quarantine 3 (`incompatible_schema`, `unknown_event`, `invalid_payload`), attempts 0, events 0, DLQ 3 | pass |

Не тестовано в P03 (свідомо): restart брокера/alarm (P02 C03/C05/C09 — брокерна частина, той самий клієнт), loss of quorum (P16),
DB outage relay між confirm і mark як реальний kill (модельовано `markConfirmed:false` — те саме committed вікно), LLM fencing (P06),
collector crash W1a/W1c (P04), management API bindings check (drift лікує re-declare — C08).
