# ADR-0001 — Транспорт подій: RabbitMQ topic exchange + quorum queues

Статус: **accepted** (P02 spike, 2026-09-15; go). Запропоновано P01. Джерело вимог: plan §1, §3.1, §15.1.
Evidence: [`P02-handoff.md`](../evidence/message-platform/P02-handoff.md), [`P02-crash-evidence.md`](../evidence/message-platform/P02-crash-evidence.md).

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
6. **Deployment profile (закріплено P02):** dev — один контейнер `rabbitmq:4.3-management` у Compose під
   profile `broker` (`docker compose --profile broker up -d rabbitmq`; default deploy не змінюється, жоден сервіс
   не споживає брокер до P03), змінні `RABBITMQ_*` у `.env.example`. Production з вимогою переживати втрату вузла —
   quorum queues на трьох незалежних вузлах/доменах відмови; цей профіль **не** перевірявся (P16).
7. **Версії (закріплено P02 spike):** сервер `rabbitmq:4.3-management` (перевірено 4.3.5), клієнт `RabbitMQ.Client`
   7.2.2 (async API, publisher confirmation tracking), `Testcontainers.RabbitMq` 4.15.0. Broker arguments черг —
   `contracts/messaging/topology.json` → `queue_policies.*.broker_arguments` і `broker` (ADR-0002).
8. **Retry semantics (spike + [офіційна документація quorum queues, Poison Message Handling](https://www.rabbitmq.com/docs/quorum-queues);
   стосується RabbitMQ ≥ 4.3, де ліміт рахується за delivery-count; до 4.3 — за acquired-count):** `x-delivery-count`
   інкрементують `basic.reject(requeue=true)` та crash/закриття каналу чи з'єднання з unacked повідомленнями; **не**
   інкрементують `basic.nack(requeue=true)` (лише `x-acquired-count`) і consumer timeout. Отже `x-delivery-limit` —
   запобіжник crash loop, а бізнес-retries з backoff і їх ліміт живуть у `processing.attempts` (ADR-0004 §6.3). P03 має
   робити бізнес-retry саме через **`basic.nack(requeue=true)`**: з `basic.reject` ліміт `x-delivery-limit: 4` спрацював би
   раніше за `max_delivery_attempts: 5` і receipt `quarantined` не був би записаний. Вичерпання → receipt `quarantined`
   (commit) → `basic.nack(requeue=false)` → DLX → DLQ (доведено G04/G04b).

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

## Рішення go/no-go (P02)

**Go.** На реальному RabbitMQ 4.3.5 (Testcontainers, single node) пройшли gate хвилі 1 (G00–G04b) і crash-сценарії
брокерної частини ADR-0004 (C01–C09): fan-out на всі required підписки, competing consumers без дублів (100/100 із 200),
offline consumer наздоганяє, restart брокера під час publish (275 із 300 unconfirmed → republish тим самим `event_id`,
0 втрат, 0 дублікатів ефектів), restart під час consume (redelivered), delayed confirm/duplicate поглинуто inbox,
unroutable → `basic.return`, відсутня required черга → readiness missing, drift binding живої черги невидимий для passive
declare (потрібні management API/reconciliation — P03), memory alarm → `connection.blocked` і outbox тримає рядок, DLQ через
delivery-limit (crash loop) і через business attempts. Smoke 5 000 × 2 KiB × 2 підписки × 2 репліки: 10 000 ефектів,
0 втрат/дублів. Межі: single node (HA/loss of quorum не тестовано), outbox/inbox — файлова заглушка (P03), confirms
послідовні (≈138 publish/s — P03 має батчити confirms). Fallback PostgreSQL deliveries лишається запасним варіантом, якщо P16
покаже проблеми HA/ops.

## Відкрите

| Питання | Задача |
|---|---|
| Реальний HA profile (3 вузли, `x-quorum-initial-group-size`), loss of quorum, disk alarm thresholds | P16 |
| Batch publisher confirms у relay, prefetch за роллю (за вимірами) | P03 |
| DLQ consumer/reconciliation, що ставить receipt `quarantined` для crash-loop dead-letters | P03 |

## Перевірка

P02: `pwsh -File scripts/with-lock.ps1 dotnet test tests/Puluj.Transport.Spike.Tests/Puluj.Transport.Spike.Tests.csproj`
(Testcontainers RabbitMQ, ≈3.5 хв, 3 контейнери); результати — `docs/evidence/message-platform/P02-*`.
Посилання: [publish/subscribe](https://www.rabbitmq.com/tutorials/tutorial-three-dotnet),
[confirms](https://www.rabbitmq.com/docs/confirms), [quorum queues](https://www.rabbitmq.com/docs/quorum-queues).
