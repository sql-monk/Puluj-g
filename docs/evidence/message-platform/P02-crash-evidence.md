# P02 — crash evidence на реальному RabbitMQ

Suite: `tests/Puluj.Transport.Spike.Tests` (xUnit, `RabbitMQ.Client` 7.2.2, `Testcontainers.RabbitMq` 4.15.0,
образ `rabbitmq:4.3-management` → сервер 4.3.5, single node, quorum queues). Кожен test class — власний контейнер.
Факти з фінального прогону — [`P02-crash-evidence.json`](P02-crash-evidence.json) (пишеться тестами),
TRX — [`test-results/p02-Puluj.Transport.Spike.Tests.trx`](test-results/p02-Puluj.Transport.Spike.Tests.trx),
smoke — [`P02-spike-metrics.json`](P02-spike-metrics.json). Outbox/inbox/receipts — файлова заглушка `FileStore`
(«commit» = запис файлу), не БД P03.

## Gate хвилі 1 (plan §12)

| Test | Що доведено | Факти |
|---|---|---|
| G00 | Topology з `topology.json`: exchange, 32 quorum черг + 32 DLQ, bindings за lanes, readiness (`MissingRequiredQueuesAsync` = ∅), повторний declare idempotent | server 4.3.5, topology_version 1 |
| G01 | Fan-out: одна публікація `raw.stored` → `normalizer`, `message-analytics`, `archive` по 1 ефекту; `parser` (без binding) — 0 | duplicates 0, outbox confirmed |
| G02 | Competing consumers: 200 повідомлень, 2 репліки `normalizer` | 100 / 100, effects 200, duplicates 0; `archive.live` backlog 200 без consumer |
| G03 | Lanes: `history` не потрапляє в live; `track.changed` у replay отримує лише `message-analytics.replay`, `projection` без replay черги | parser.live Δ0, parser.history Δ1 |
| G04 | Business failures: attempts у «БД» до `max_delivery_attempts`=5 → receipt `quarantined` (commit) → `nack(requeue=false)` → DLQ; transfer з DLQ ідемпотентний | deliveries 5, effects 0, `x-delivery-count` не інкрементувався (0) |
| G04b | Crash loop: consumer закриває канал без ACK на кожній доставці → після `x-delivery-limit`=4 redeliveries брокер dead-letter'ить | deliveries 5, max `x-delivery-count` 4, DLQ 1 |
| G05 | **Skip**: loss of quorum потребує 3-node cluster | — |

## Crash windows ADR-0004 (брокерна частина)

| Test | Вікно | Сценарій | Факти |
|---|---|---|---|
| C01 | W4 | Consumer «падає» до commit (канал закрито без ACK) | effect після crash 0, ready 1, redelivered=true у репліки 2, effect 1, duplicates 0 |
| C02 | W5 | Commit є, «падіння» до ACK | effect після crash 1, redelivery → inbox hit → ACK, duplicates_suppressed 1, effect 1 |
| C03 | W7a / W2 / W3 | `docker stop/start` брокера під час relay 300 повідомлень | перший прохід перервано винятком connection loss (`first_pass_confirmed: -1`, кількість confirmed до падіння не фіксується), 275 unconfirmed → republish тим самим `event_id`, effects 300, lost 0, duplicates 0 |
| C04 | W7b (client-simulated) | Confirm «не встиг» (timeout 1 tick) → outbox лишається unconfirmed → republish | outbox attempts 2, deliveries 2, effect 1, duplicates_suppressed 1 |
| C05 | W7c | Restart брокера з unacked повідомленням у повільного consumer; новий «процес» | ready після restart 1, redelivered=true, effect 1 |
| C06 | W10a | Publish до старту consumer | ready 50 до consumer, effects 50, duplicates 0 |
| C07 | W10b | `archive` offline, `normalizer` працює; потім archive стартує | archive backlog 40 при consumers=0, після старту effects 40 |
| C08 | W11 | (1) Unroutable: жодної bound черги для routing key → `basic.return`, outbox `Returned`, не confirmed-as-delivered. (2) Відсутня required **черга** (`QueueDeleteAsync`) → readiness через passive declare `missing=[puluj.archive.history]` → declare відновлює. (3) Drift **binding** живої черги (`QueueUnbindAsync` archive.history ← ingress.received): passive declare **не бачить** (missing = ∅), management API `/bindings` бачить; публікація при цьому **confirmed і не returned**, бо raw-writer.history ще bound — archive.history копії не отримує (видно лише reconciliation); declare відновлює binding | unbind_seen_by_passive_declare=false, unbind_seen_by_management_bindings=true, publish_after_unbind_confirmed_not_returned=true |
| C09 | W13 | `set_vm_memory_high_watermark 0.0000001` → `connection.blocked`, confirm не приходить, outbox тримає рядок; після скидання → confirmed, effect 1; заблокована публікація дійшла після зняття alarm → дублікат поглинуто | confirmed_while_blocked 0, confirmed_after_clear 1, duplicates_suppressed 1 |

Не тестовано у P02 (явно): loss of quorum / 3 вузли (G05), disk alarm окремо (memory alarm дає той самий
`connection.blocked` шлях), automatic connection recovery клієнта (C05 моделює рестарт процесу), DB outage (W12 — P03),
DLQ transfer failure з падінням DLX (W6a-1/2 частково: G04 демонструє порядок receipt → nack), LLM lease (W8 — P06),
readiness bindings через management API `/api/queues/…/bindings` + reconciliation expected-vs-receipts як runtime-механізм
(C08 лише показує сліпу зону passive declare; реалізація — P03).

## Smoke load (не SLO)

5 000 повідомлень × 2 KiB × 2 підписки × 2 репліки, prefetch 50, **послідовний** await confirm на кожне повідомлення
(фінальний прогін; числа залежать від навантаження хоста, бо три контейнери працюють паралельно): publish+confirm 36.2 s
(≈138/s), consumer drain ≈36.2 s (≈276 ефектів/s — consumers встигали за publisher; 2 500 на кожну з 4 реплік), 10 000 ефектів,
0 дублів, 0 redelivered. Без p50/p95 латентності та без assert на DLQ=0 (N7). Висновок для P03: relay має публікувати
батчами й чекати confirms пакетно (per-message round trip домінує); throughput брокера тут не вимірювався.

## Ключове спостереження для P03

RabbitMQ ≥ 4.3 ([Poison Message Handling](https://www.rabbitmq.com/docs/quorum-queues); ліміт за delivery-count,
до 4.3 — за acquired-count): `x-delivery-count` інкрементують `basic.reject(requeue=true)` та crash/закриття каналу
чи з'єднання з unacked; **не** інкрементують `basic.nack(requeue=true)` (лише `x-acquired-count`) і consumer timeout.
Spike підтвердив емпірично: `basic.get`+`reject` → dead-letter після 4 redeliveries; consumer `nack(requeue=true)` →
12 000+ redeliveries без dead-letter. Тому бізнес-retries з backoff і їх ліміт живуть у `processing.attempts`
(ADR-0004 §6.3) і виконуються саме через `basic.nack(requeue=true)` (з `reject` ліміт 4 спрацював би раніше за
`max_delivery_attempts` 5 і receipt `quarantined` не записався б), а `x-delivery-limit` — запобіжник crash loop.
Зафіксовано в ADR-0001 п.8, ADR-0002 «Queue policy», `topology.json.queue_policies.$comment`.
