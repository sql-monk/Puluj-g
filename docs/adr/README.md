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
| [0002](ADR-0002-topology-subscriptions.md) | Topology, registry підписок, «видалити після всіх», DLQ, waiver | accepted (P02; runtime P03) | P01, P02, P03 | Management API bindings check, health — P13 |
| [0003](ADR-0003-identities-versioning.md) | Identities (event, raw, run), timestamps, schema/topology versions, сумісність | proposed (bridge-правила P03) | P01, P03 | Unique hash migration, справжній causation — P04 |
| [0004](ADR-0004-delivery-guarantees.md) | Outbox/inbox, ACK після commit, crash windows W1–W14 | accepted (P03) | P01, P02, P03 | W1 — P04; W8 fencing — P06 |
| [0005](ADR-0005-runs-completion.md) | Runs/generations/lanes, completion manifest, terminal outcomes, workflow status | proposed (receipts/runs мінімум — P03) | P01, P03 | Orchestration — P14 |
| [0006](ADR-0006-data-model.md) | Логічна модель `messaging.*`, `processing.*`, `analytics.message_*` | accepted (messaging/processing — P03) | P01, P03 | Analytics DDL — P15; партиціювання — P16 |
| [0007](ADR-0007-slo-retention-proposal.md) | SLO орієнтири з P00 baseline, quota, retention, deletion eligibility | proposed | P01 | Абсолютні SLO — P16; outbox/inbox cleanup — P03, решта retention — P16/Ops |

Шаблон: Контекст → Рішення → Альтернативи → Наслідки → Відкрите (task ID) → Перевірка.
