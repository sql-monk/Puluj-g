# ADR-0004 — Гарантії доставки: outbox/inbox, ACK після commit, crash windows

Статус: **proposed** (P01). Вимоги: plan §6, §13 «Надійність», §15.2, §16.2 п.2. Реалізація/тести — P02 (spike), P03 (inbox/outbox/relay).

## Рішення (коротко)

1. **At-least-once + ідемпотентні ефекти.** «Exactly once» не обіцяємо ані для транспорту, ані для LLM.
2. **Transactional outbox.** Producer записує бізнес-результат і вихідні події в `messaging.outbox` **однією
   транзакцією**. Relay публікує з outbox, чекає publisher confirm, позначає `confirmed_at`. До confirm рядок
   доступний для повтору з тим самим `event_id`.
3. **Inbox.** Consumer у транзакції результату вставляє `messaging.inbox (subscription_id, event_id)`; конфлікт →
   ефект уже застосований → ACK без повторного ефекту. Inbox completion, stage result, deliveries receipt і
   outbox вихідних подій — **одна** транзакція.
4. **ACK після commit.** Порядок: receive → перевірка schema/compat/inbox → робота поза довгою транзакцією →
   коротка транзакція (re-check inbox/lease/revision, застосувати, receipt, outbox) → commit → ACK.
5. **Retry у БД, не в пам'яті.** `processing.attempts` веде спроби; transient → bounded exponential backoff + jitter;
   invalid payload/schema → quarantine без гарячого циклу. Після `max_delivery_attempts` — DLQ + receipt `quarantined`.
6. **Lease + fencing.** Довгі jobs (LLM) мають persisted lease з `fencing_token`; пізній результат старого
   виконавця відкидається за токеном.
7. **Broker ACK і DB commit не атомарні; relay confirm і mark не атомарні** — обидва вікна поглинає inbox.
8. Audit/analytics consumers не публікують lifecycle-події про власні lifecycle-події.

## Нормальний шлях

```mermaid
sequenceDiagram
    participant P as Producer (consumer попереднього етапу)
    participant DB as PostgreSQL
    participant R as Outbox relay
    participant B as RabbitMQ
    participant C as Consumer (підписка S)
    P->>DB: BEGIN; результат; inbox(S_prev, e_in); deliveries receipt; outbox(e_out); COMMIT
    P->>B: ACK(e_in)
    R->>DB: SELECT outbox WHERE confirmed_at IS NULL (lease)
    R->>B: basic.publish(e_out, persistent) + confirm
    B-->>R: ack
    R->>DB: UPDATE outbox SET confirmed_at
    B->>C: deliver(e_out) до черги S
    C->>DB: schema/compat check; SELECT inbox(S, e_out)?
    C->>C: робота поза транзакцією
    C->>DB: BEGIN; re-check inbox/lease; ефект; inbox(S,e_out); receipt(S,e_out,completed); outbox(next); COMMIT
    C->>B: ACK(e_out)
```

## Crash windows

Позначення: **Стан** — що вже durable в БД/брокері на момент падіння; **Повтор** — хто повторює;
**Поглинання** — що робить повтор безпечним; **Видно** — оператору. Test ID — для P02 (broker spike)
або P03 (inbox/outbox/relay); P0x-Cxx означає обов'язковий crash test відповідної задачі.

| ID | Вікно | Стан | Повтор | Поглинання | Видно | Test |
|---|---|---|---|---|---|---|
| **W1a** | Collector впав **до** durable checkpoint (outbox row + checkpoint в одній транзакції не закомічені) | Нічого | Reconnect/backfill джерела за checkpoint | `(source_id, key, revision)` unique у raw-writer; повторний `ingress.received` → `raw.stored{is_new:false}` | collector `LastError`, backfill range | P04-C01 |
| **W1b** | Collector впав **після** commit (outbox+checkpoint), до publish | outbox row unconfirmed | Relay | Той самий `event_id` | outbox unpublished age | P03-C01 |
| **W1c** | Межа: Telegram edit update отримано, `IngestAsync` не закомітився (crash/DB outage), поки collector пише raw напряму (bridge) | raw для edit відсутній; DB checkpoint (`min_id`, лише оригінали) edits не відновлює | Лише WTelegram update state (`SessionPath + ".updates"`, локальний файл, getDifference після рестарту) — не durable за §6.1 | — | Відома межа до P04 collector outbox | P04-C02 |
| **W2** | Raw-writer закомітив raw + outbox(`raw.stored`), relay не публікував | outbox unconfirmed | Relay | `event_id` стабільний | outbox unpublished age > threshold → alarm | P03-C02 |
| **W3** | Relay отримав confirm, впав до `confirmed_at` | Повідомлення у чергах; outbox unconfirmed | Relay публікує вдруге | Inbox кожної підписки дедуплікує (`event_id`); archive — unique `event_id` | duplicate suppression counter | P03-C03 |
| **W4** | Consumer впав до commit (робота зроблена, транзакція не закомічена) | Нічого нового | Broker redelivery (unacked) | Робота повторюється; ефекти лише в транзакції | attempts +1, `interrupted` attempt | P02-C01 |
| **W5** | Consumer закомітив, впав до ACK | inbox/receipt/outbox committed | Broker redelivery | Inbox hit → ACK без ефекту | duplicate suppression | P02-C02 |
| **W6a-1** | Attempts вичерпано, consumer впав до commit receipt `quarantined` | attempts ≥ max у БД; receipt відсутній; копія unacked | Broker redelivery | Consumer бачить attempts ≥ max → commit receipt `quarantined` (без нової спроби) → nack | attempts, `needs_attention` | P03-C04 |
| **W6a-2** | Receipt `quarantined` committed, nack/DLX не виконано (broker/DLX недоступний, crash до nack) | receipt `quarantined`; копія в основній черзі unacked | Broker redelivery | Повторний nack ідемпотентний; джерело істини — `processing.quarantine`, broker DLQ — операційна копія | DLQ depth, `needs_attention` | P03-C04 |
| **W6b** | Replay із DLQ після виправлення (admin retry) | receipt `quarantined`, оригінал є | Admin команда → нова спроба з `retry_of_attempt_id` | Нова версія правил → новий run; зв'язок зі старою спробою збережено | attempts chain у картці повідомлення | P03-C05, P13 |
| **W7a** | Broker restart / втрата вузла quorum під час publish | outbox unconfirmed (confirm не прийшов) | Relay після reconnect | `event_id` | broker health, confirms latency | P02-C03 |
| **W7b** | Delayed confirm: relay timeout, потім confirm приходить | Повідомлення в черзі; outbox unconfirmed | Relay публікує вдруге | Inbox | duplicate counter | P02-C04 |
| **W7c** | Broker restart під час consume (unacked) | Нічого | Redelivery після reconnect | = W4/W5 | redelivered flag | P02-C05 |
| **W8** | LLM job: lease минув, takeover іншим виконавцем; старий виконавець повертає результат пізніше | `processing.attempts` з `fencing_token` n; новий lease n+1 | Новий виконавець | Finalizer приймає результат лише з актуальним токеном; старий → `noop` receipt + audit | late-result counter, LLM cost audit (оплачений повтор) | P06-C01 |
| **W9a** | Зміна `topology_version` з backlog у старій підписці | Старі events з `topology_version=n` | — | Expected set береться з версії події; нова підписка отримує лише нові події | reconciliation gap | P03-C06 |
| **W9b** | Paused/removed required subscription з backlog | Черга є; consumer немає | Drain/transfer/waiver (ADR-0002) | receipt `waived{reason,actor}` на кожну expected delivery | «required consumer відсутній», waiver audit | P03-C07, P13 |
| **W10a** | Публікація до запуску consumer | Durable queue існує (pre-declared), повідомлення накопичуються | Consumer стартує | Порядок не гарантується між підписками | backlog/oldest age | P02-C06 |
| **W10b** | Один consumer offline, інші працюють | Тільки його черга росте | Consumer стартує і наздоганяє | Інші підписки не чекають | «offline required consumer» alarm | P02-C07 |
| **W11** | Unroutable message / відсутня required binding | Relay: `mandatory` return або немає bound queue | Readiness блокує publish до declare; reconciliation знаходить expected без receipt | Publish не вважається успіхом бізнес-обробки | returned/unroutable counter, missing binding alarm | P02-C08, P03-C08 |
| **W12a** | DB outage у consumer між роботою і commit | Нічого | Consumer: transient retry; lease/claim expiry | Inbox/lease re-check після повернення БД | DB pool wait, attempts | P03-C09 |
| **W12b** | DB outage у relay після confirm, до mark | = W3 | Relay після повернення БД | Inbox | — | P03-C03 |
| **W13** | Broker disk/memory alarm → blocked publisher | outbox накопичується unconfirmed; нічого не викидається (required без TTL/drop-head) | Relay після зняття alarm | `event_id` | broker alarm, outbox age; checkpoint collector продовжує (DB-транзакція outbox+checkpoint успішна), outbox росте; pause collector — лише за явним high-water policy (P04) | P02-C09 |
| **W14a** | Duplicate deliveries (будь-яка причина) | — | — | Inbox `(subscription_id, event_id)` | duplicate counter | P03-C10 |
| **W14b** | Out-of-order між чергами: analytics отримує `message.analysis.completed` раніше за `raw.stored` | — | — | Upsert часткового запису за `(raw_message_id, run_id)`; reconciliation дозаповнює | pending partial rows | P15-C01 (fixture `sequences/analytics-out-of-order.json`) |

### Діаграми критичних вікон

```mermaid
sequenceDiagram
    title W3 / W7b — relay confirm без mark
    participant R as Relay
    participant B as Broker
    participant DB as DB
    participant C as Consumer
    R->>B: publish(e, event_id=X)
    B-->>R: confirm
    Note over R: crash / DB недоступна
    B->>C: deliver(X) #1
    C->>DB: inbox(S,X) insert → ok; commit; ACK
    R->>DB: (після рестарту) outbox X unconfirmed
    R->>B: publish(X) знову
    B->>C: deliver(X) #2
    C->>DB: inbox(S,X) insert → conflict
    C->>B: ACK без ефекту
```

```mermaid
sequenceDiagram
    title W8 — lease takeover і пізній результат
    participant Q as llm.requested
    participant A as LLM worker A (token 1)
    participant DB as DB
    participant Bw as LLM worker B (token 2)
    participant F as Finalizer
    Q->>A: deliver(request R)
    A->>DB: lease(R, token=1, until T)
    Note over A: провайдер відповідає повільно; T минає
    Bw->>DB: lease expired → takeover(R, token=2)
    Bw->>DB: outbox llm.completed{fencing_token:2}
    A->>DB: outbox llm.completed{fencing_token:1} (пізно)
    F->>DB: attempts(R).current_token == 2 → результат token 1 → receipt noop + audit "late result"
```

```mermaid
sequenceDiagram
    title W6a-2 — DLQ transfer at-least-once
    participant C as Consumer
    participant DB as DB
    participant B as Broker
    C->>DB: attempts(e) = 5 (max) → BEGIN; receipt quarantined; COMMIT
    C->>B: nack(requeue=false) → DLX → DLQ
    Note over B: DLX недоступний / crash до nack
    B->>C: redelivery(e)
    C->>DB: receipt already quarantined → повторити nack (ідемпотентно)
```

## Покриття обов'язкових сценаріїв §13

| §13 сценарій | Вікна |
|---|---|
| Collector crash до/після checkpoint; повторне читання не губить/не дублює identity | W1a, W1b, W1c |
| Публікація до запуску consumer | W10a |
| Restart брокера, втрата вузла quorum | W7a, W7c |
| Unroutable message, відсутня required binding | W11 |
| Worker crash до commit / після commit до ACK | W4, W5 |
| Relay crash після confirm | W3, W12b |
| Duplicate deliveries | W14a, W3, W5, W7b |
| DB/broker outage, disk limit, delayed confirm, retry transfer failure | W12a, W12b, W7a, W13, W7b, W6a-1, W6a-2 |
| Replay із DLQ після виправлення | W6b |
| Довгий LLM job і lease takeover | W8 |
| Один consumer offline; backlog наздоганяє | W10b |
| Нова/видалена/paused підписка, зміна topology version, waiver, reconciliation | W9a, W9b, W11 |

## Що ADR не обіцяє

- Абсолютну безвтратність при одночасній втраті всіх durable копій (БД + outbox + брокер).
- Порядок між різними підписками або між lanes; порядок усередині агрегату — через `aggregate_revision` і
  стратегію конкуренції P09, не через брокер.
- Ідемпотентність платного LLM-виклику на боці провайдера — тільки `request_id` + audit + budget.

## Відкрите

| Питання | Задача |
|---|---|
| Реальні crash tests W4/W5/W7/W10/W11/W13 на Testcontainers RabbitMQ | P02 |
| Outbox relay (lease, batch, confirm timeout), inbox, receipts, reconciliation, W2/W3/W6/W9/W12/W14a | P03 |
| Collector outbox/checkpoint, W1 | P04 |
| Fencing tokens у attempts, W8 | P06 |
