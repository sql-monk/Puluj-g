# ADR-0001 — Транспорт подій: RabbitMQ topic exchange + quorum queues

Статус: **proposed** (P01, 2026-09-15). Приймання — після spike P02 (go/no-go). Джерело вимог: plan §1, §3.1, §15.1.

## Контекст

Сьогодні транспорт — PostgreSQL `NOTIFY` з payload `{type,id,at}` (`PgNotifyPublisher`, `PgNotifyListener`)
плюс poll `Pending` рядків `raw_messages`. Це best-effort: втрачене сповіщення компенсує poll, але немає
durable per-subscriber доставки, ACK, fan-out на кілька типів воркерів чи backlog для offline consumer.
Один claim/lease на весь pipeline змушує LLM/аналітику тримати транзакцію raw (P00 writer map).

Потрібно: копія кожній зацікавленій групі обробників, competing consumers усередині групи, ACK після
commit результату, backlog для зупиненого required consumer, окремі lanes для live/history/replay,
видимий стан черги для оператора.

## Рішення

1. **Брокер — RabbitMQ.** Один durable `topic` exchange `puluj.events`; на кожну логічну підписку
   (consumer role) і lane — окрема durable черга **quorum** типу з bindings за routing key
   `puluj.{lane}.{event_type}` (деталі імен — ADR-0002).
2. **Гарантії публікації:** persistent messages (`delivery_mode=2`), publisher confirms, публікація лише
   з transactional outbox (ADR-0004). Confirm від брокера і DB commit не оголошуються атомарними.
3. **Гарантії споживання:** manual ACK після commit результату; prefetch обмежений на consumer;
   `requeue` лише для transient помилок у межах ліміту спроб; далі dead-letter (ADR-0002).
4. **Topology створюється заздалегідь** (declare при старті з idempotent параметрами, без `auto-delete`,
   без `exclusive`); readiness воркера перевіряє наявність усіх required queues/bindings своєї версії topology.
5. **Один транспорт на реліз.** Подвійний брокер (NATS + RabbitMQ, або RabbitMQ + PostgreSQL deliveries)
   не входить до обсягу. `NOTIFY` лишається тільки як bridge для API/SignalR на час compatibility window
   (ADR-0002 §bridge), не як transport event.
6. **Deployment profile — початкове припущення:** локально/dev — один контейнер RabbitMQ у Compose
   (без Compose-змін до P02); production з вимогою переживати втрату вузла — quorum queues на трьох
   незалежних вузлах/доменах відмови. Версію сервера, .NET клієнта (`RabbitMQ.Client` 7.x як кандидат)
   та політики закріплює P02 за результатом spike.

## Альтернативи

| Варіант | Чому не зараз |
|---|---|
| **NATS JetStream** | Єдиний retained stream + consumer cursors — привабливо для replay із потоку; але наша replay-модель будується з durable архіву в БД (`messaging.events`, ADR-0006), а не з брокера; менше досвіду команди й ops-інструментів; повернемося, якщо retained stream стане пріоритетом. |
| **PostgreSQL deliveries** (таблиці черг + SKIP LOCKED) | Без нового сервісу, але transport I/O лишається в тій самій БД, що і hot rows (P00: serial ceiling Store); fan-out на N підписок множить записи; немає native backpressure/alarms брокера. Прийнятний fallback, якщо spike P02 покаже no-go. |
| **Kafka/Redpanda** | Partitioned log добре для ordering, але операційна вага і семантика consumer groups без per-message ACK/DLQ не відповідають вимозі «ACK після commit, quarantine з причиною» для повільних LLM jobs. |
| Лишити NOTIFY + poll | Не дає durable fan-out, backlog offline consumer, DLQ, метрик черги. |

## Наслідки

- Плюс: незалежні підписки й репліки, backlog для offline consumer, DLQ, метрики/alarms брокера, lanes.
- Мінус: новий stateful сервіс у deploy; дві системи durability (БД + брокер) → потрібні outbox/inbox,
  reconciliation і явний розбір crash windows (ADR-0004). Заміна транспорту **не** усуває конкуренцію за
  спільні рядки БД (plan §1 п.7, §7) — це P09.
- Бюджет підключень: consumers додають DB connections; scale caps — за DB pool і LLM limits, не за
  довжиною черги (plan §7).

## Відкрите

| Питання | Задача |
|---|---|
| Версії сервера/клієнта, quorum `x-quorum-initial-group-size`, `delivery-limit`, DLX policy, prefetch за роллю | P02 |
| Реальний HA profile (3 вузли чи 1), disk alarm thresholds, Compose/`.env` | P02, P16 |
| Go/no-go: два типи consumer отримують подію, дві репліки одного ділять jobs, offline consumer наздоганяє, crash-сценарії ADR-0004 | P02 |

## Перевірка

P02 spike з Testcontainers RabbitMQ: сценарії §13 «Надійність» + crash windows ADR-0004 з test IDs.
Посилання: [publish/subscribe](https://www.rabbitmq.com/tutorials/tutorial-three-dotnet),
[confirms](https://www.rabbitmq.com/docs/confirms), [quorum queues](https://www.rabbitmq.com/docs/quorum-queues).
