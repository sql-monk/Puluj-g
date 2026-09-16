# Architecture Decision Records — платформа повідомлень

ADR фіксують рішення програми [`plan-message-platform.md`](../plan-message-platform.md) з мотивами,
альтернативами та межами. Кожен ADR розрізняє **прийнято** (закриває рішення) і **початкове припущення**
(хто і в якій задачі закриває). Machine-readable частина контрактів — [`contracts/messaging/`](../../contracts/messaging/README.md).

Статуси: `proposed` — написано в межах задачі, чекає приймання наступною задачею; `accepted` — підтверджено
реалізацією/spike; `superseded` — замінено іншим ADR (посилання обов'язкове).

Діаграми в ADR — mermaid inline (прийнято для P01; drawio/PNG у `docs/diagrams/` описують поточну систему).

| ADR | Тема | Статус | Задача | Закриває / відкрите |
|---|---|---|---|---|
| [0001](ADR-0001-transport.md) | Транспорт: RabbitMQ topic exchange + quorum queues; альтернативи | accepted (P02, go) | P01, P02 | HA profile/loss of quorum — P16; batch confirms — P03 |
| [0002](ADR-0002-topology-subscriptions.md) | Topology, registry підписок, «видалити після всіх», DLQ, waiver | accepted (P02; runtime P03; v4 P05) | P01, P02, P03, P05 | Management API bindings check, health — P13 |
| [0003](ADR-0003-identities-versioning.md) | Identities (event, raw, run), timestamps, schema/topology versions, сумісність | accepted (P04) | P01, P03, P04 | Analytics на нових колонках — P15 |
| [0004](ADR-0004-delivery-guarantees.md) | Outbox/inbox, ACK після commit, crash windows W1–W14 | accepted (P03; W1 — P04; W8 — P06) | P01–P04, P06 | W1c spool — за потребою; quarantine safety net — P13/P16 |
| [0005](ADR-0005-runs-completion.md) | Runs/generations/lanes, completion manifest, terminal outcomes, workflow status | proposed (реалізовано: receipts P03, stage_results P05, finalizer/extraction P06; формальний accept — owner ADR) | P01, P03, P05, P06 | Orchestration/generations — P14 |
| [0006](ADR-0006-data-model.md) | Логічна модель `messaging.*`, `processing.*`, `analytics.message_*` | accepted (messaging/processing — P03; extractions/observations — P06) | P01, P03, P05, P06 | Analytics DDL — P15; партиціювання — P16 |
| [0007](ADR-0007-slo-retention-proposal.md) | SLO орієнтири з P00 baseline, quota, retention, deletion eligibility | proposed | P01 | Абсолютні SLO — P16; outbox/inbox cleanup — P03, решта retention — P16/Ops |
| [0008](ADR-0008-event-catalog.md) | Каталог event kinds, legacy mapping/backfill, правила розпізнавання (версії, resolver, shadow) | accepted (P07; rules — P08) | P07, P08 | Incidents — P10; catalog у API/UI — P11/P12; analytics — P15 |
| [0009](ADR-0009-aggregate-ownership.md) | Володіння агрегатами: track/alert writers, lock hierarchy (Store shared → track → category / alert region), revisions, watchdog-команди, cutover, SQL trigger ownership | accepted (P09; incident owner — P10) | P09, P10 | projection/NOTIFY — P11; stats/links поза fact path — P11/P15; replay lane агрегатів — P14 |
| [0011](ADR-0011-read-side-realtime.md) | Read-side incidents: additive DTO/`/api/incidents`, precision з `LocationKind`, history mode `effective` / `recorded&asOf`, query/payload budgets, роль `projection` (v8), NOTIFY як backplane (at-most-once + `Resync`/reload), клієнтські правила revision/catalog adapter | accepted (P11) | P11 | track/alert push через projection, feed incidents, keyboard-мапа — P12/P16; checkpoint projection — P14 |
| [0010](ADR-0010-incidents.md) | Incidents: схема (incidents/links/revisions), owner `incident-worker`, kind locks, policy `incident-1` (вікно/slack/confirms з `dedup_policy`, ambiguity → review), echo/supports/confirms, admin-команди через той самий writer, live generation | accepted (P10) | P10 | projection/NOTIFY — P11; generation orchestration/replay — P14; `independent_source_count`, crossover policy — після даних |

Шаблон: Контекст → Рішення → Альтернативи → Наслідки → Відкрите (task ID) → Перевірка.
