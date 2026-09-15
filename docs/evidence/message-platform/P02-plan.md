# P02 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/2. Початок: 2026-09-15. Base: `6822541` + локальні незакомічені
результати P01 (`contracts/messaging/`, `docs/adr/`). Залежність P01 — done (`P01-handoff.md`).
Виконавець: агент `p02` (Claude Code fork); незалежний reviewer: субагент `p02_review` (план і результат).
**Паралельно виконується P07 у тому самому робочому дереві** — див. «Правила паралельної роботи».

## Аналіз задачі

Хвиля 1, spike: підтвердити рекомендацію ADR-0001 (RabbitMQ topic exchange + quorum queues) на реальному
брокері й дати **go/no-go** з відтворюваним integration demo та crash evidence. Це не реалізація outbox/inbox
у БД (P03) і не перенесення collectors (P04). Spike має довести саме те, що обіцяє ADR-0004 для брокера:
W4, W5, W7a/b/c, W10a/b, W11, W13 (test IDs P02-C01…C09), плюс gate хвилі 1 (§12): «два типи consumer
отримують подію, дві репліки одного ділять jobs; offline consumer наздоганяє».

Що є: `contracts/messaging/topology.json` (exchange, routing/queue/dlq patterns, 11 підписок × lanes,
`queue_policies` абстрактні), envelope/payload schemas + fixtures (valid), `Testcontainers.PostgreSql` 4.15.0
у `Directory.Packages.props`, `scripts/with-lock.ps1` (machine-wide build mutex), Compose без RabbitMQ.
NuGet на 2026-09-15: `RabbitMQ.Client` 7.2.2, `Testcontainers.RabbitMq` 4.15.0.

Рішення §15.1, які закриває P02: **broker/client versions і resilience profile** (ADR-0001 → accepted або
rejected), broker arguments для `queue_policies` (ADR-0002 «Відкрите»), реальний deployment profile (dev).
Не вирішує: DB outbox/inbox/relay (P03), 3-node HA/loss of quorum (потребує кластера — P16, або явний пропуск).

## Артефакти

| Артефакт | Шлях | Призначення |
|---|---|---|
| Spike suite | `tests/Puluj.Transport.Spike.Tests/` (xUnit, `RabbitMQ.Client`, `Testcontainers.RabbitMq`; **без ProjectReference на `src/`**; `contracts/messaging/*.json` через Content Link як у P01) | Відтворюваний demo + crash tests |
| Spike code (усередині test project, папка `Spike/`) | `TopologyDeclarer` (з `topology.json`: exchange, queues `puluj.{sub}.{lane}` quorum + DLX/DLQ, bindings), `OutboxPublisher` (persistent, confirms, stable `event_id`, простий durable outbox у файлі/SQLite-подібному JSON — не БД P03), `InboxConsumer` (manual ACK після «commit», prefetch, inbox dedup у файлі), `BrokerMetrics` (management API: ready/unacked/redelivered/dlq depth) | Мінімальний код, який P03 може переписати; не production |
| Crash tests | P02-C01 consumer crash до commit → redelivery; C02 після commit до ACK → inbox dedup; C03 broker restart під час publish → republish тим самим event_id; C04 delayed confirm → duplicate поглинуто; C05 broker restart під час consume → `redelivered`; C06 publish до старту consumer → backlog; C07 один consumer offline, інші працюють, потім наздоганяє; C08 unroutable/missing binding → `mandatory` return/alarm; C09 memory/disk alarm → blocked publisher, нічого не втрачено | Evidence ADR-0004 |
| Gate tests | G01 fan-out: одна публікація → 2 підписки (напр. `normalizer`, `archive`) отримують по копії; G02 competing consumers: 2 репліки `normalizer` ділять N повідомлень без дублів (сума = N); G03 lanes: `history` не потрапляє в `live` чергу; G04 DLQ: після `x-delivery-limit` повідомлення в DLQ, receipt `quarantined` (у файлі) | Gate хвилі 1 |
| Smoke load | 2 підписки × 2 репліки, N≈5 000 повідомлень 2 KiB: publish/confirm rate, e2e p50/p95, redelivered=0, dlq=0 — evidence, не SLO | `P02-spike-metrics.json` |
| Compose | `deploy/docker-compose.yml`: сервіс `rabbitmq` (management image, healthcheck, volume, `profiles: ["broker"]` — default deploy не змінюється); `.env.example`: `RABBITMQ_*` | Dev profile ADR-0001 |
| Docs | ADR-0001 → `accepted`/`rejected` з версіями (server image, client, Testcontainers) і dev profile; ADR-0002/`topology.json`: `queue_policies.*.broker_arguments` (`x-queue-type: quorum`, `x-dead-letter-exchange`, `x-delivery-limit`, prefetch); plan §16.1 команда; §17; `docs/README.md` не чіпати (P07) | |
| Evidence | `P02-plan.md`, `P02-handoff.md`, `P02-crash-evidence.md` (таблиця W → test → результат/лог), `P02-spike-metrics.json`, TRX у `test-results/p02-*.trx`, `P02-build-manifest.json` | §16.4 |

## Кроки

1. **Статус.** Коментар в issue #2 «розпочато»; §17 P02 → `in_progress` (лише свій рядок). Project card —
   якщо `gh auth status` показує scope `project`; інакше зафіксувати, що недоступно.
2. **Незалежне review плану** (`p02_review`, read-only): повнота проти §3, §6, §13 «Надійність», ADR-0001/0002/0004;
   чи test IDs C01–C09 відповідають вікнам; чи spike не підмінює P03; ризики Testcontainers (restart контейнера,
   alarms через `rabbitmqctl`). Внести blocking findings до старту.
3. **Пакети/проєкт.** `Directory.Packages.props`: `RabbitMQ.Client`, `Testcontainers.RabbitMq` (версії з NuGet, не з пам'яті);
   `Puluj.sln` +проєкт (solution folder `tests`). Обидва файли — ownership P02.
4. **TopologyDeclarer** з `topology.json` (quorum, DLX/DLQ, bindings за lanes); readiness-перевірка bindings
   через management API або passive declare.
5. **Publisher/consumer/inbox** + метрики; Testcontainers fixture (`rabbitmq:<4.x>-management`).
6. **G01–G04, C01–C09**: кожен тест логує стан черг до/після і асертить committed outcome (inbox/receipts/dlq),
   не «повідомлення прийшло». Broker restart — через Testcontainers `StopAsync/StartAsync` або `docker restart`;
   alarm — `rabbitmqctl set_vm_memory_high_watermark`/`set_disk_free_limit` усередині контейнера (`ExecAsync`).
   Loss of quorum (3 вузли) — **не** тестується: явний skip із причиною.
7. **Smoke load** → `P02-spike-metrics.json` (середовище, версії, throughput, percentiles, redelivered, dlq).
8. **Запуск.** Усі `dotnet build/test` **лише** через `pwsh -File scripts/with-lock.ps1 …`. Spike suite з реальним
   RabbitMQ; контрактні тести (`topology.json` змінено); `dotnet build Puluj.sln` (packages змінено); unit suites
   не обов'язкові, якщо `src/` не змінено — зафіксувати. TRX → `test-results/p02-*.trx`.
9. **Docs/ADR/Compose/env** за таблицею; рішення go/no-go з мотивами й межами (single node).
10. **Незалежне review результату** (§16.2 п.2, 12 + evidence C01–C09); blocking → виправити → повторити.
11. **Handoff §16.4**, коментар в issue, §17 → `done` (або `blocked`, якщо no-go), Project card за наявності scope.
    Commit/PR не виконуються.

## Правила паралельної роботи (P02 ∥ P07, одне робоче дерево)

- Ownership P02: `tests/Puluj.Transport.Spike.Tests/`, `Directory.Packages.props`, `Puluj.sln`, `deploy/`, `.env.example`,
  `docs/adr/ADR-0001`, `ADR-0002`, `contracts/messaging/topology.json` (+`tests/Puluj.Messaging.Contracts.Tests` за потреби),
  `docs/evidence/message-platform/P02-*`. **Не чіпати** `src/`, `data/`, міграції, `docs/README.md`, `ADR-0008` (P07).
- `docs/plan-message-platform.md`: лише рядок P02 у §17 і один рядок у §16.1; targeted replace, re-read перед edit.
- Кожен `dotnet build`/`dotnet test`/`npm` — через `scripts/with-lock.ps1`. Не запускати `dotnet build Puluj.sln`
  частіше, ніж потрібно; не використовувати `--no-build` після зміни коду.
- Не робити `git checkout/stash/reset`; не видаляти чужі незакомічені файли; `git status` перед handoff.

## Самоперевірка плану

- Spike доводить брокерні гарантії, а не DB-атомарність (P03): inbox/outbox у файлі позначені як заглушка.
- Go/no-go базується на C01–C09 + G01–G04 + smoke, з явним переліком нетестованого (HA, DB outbox).
- Версії фіксуються за NuGet/Docker на момент виконання й записуються в ADR-0001.
- Compose-зміна під profile не змінює поточний deploy; rollback = прибрати service/env.

## Незалежне review плану — p02_review

Обмеження: агент-виконавець P02 (fork) не має права запускати субагентів, тому цей прохід виконано тим самим
агентом окремим read-only проходом за §3/§6/§13/ADR-0004 **до** реалізації; повноцінне незалежне review результату
координатор запускає окремо (див. handoff). Findings, внесені до плану:

1. **Dead-lettering має бути at-least-once (W6a-2).** Quorum queues за замовчуванням dead-letter'ять at-most-once;
   потрібні `x-dead-letter-strategy: at-least-once` + `x-overflow: reject-publish` + `x-dead-letter-exchange`;
   DLQ теж quorum. Зафіксувати в `queue_policies.required.broker_arguments` і перевірити G04.
2. **C04 «delayed confirm» симулюється на клієнті** (короткий confirm timeout → republish того самого `event_id`),
   бо брокер не дає керованої затримки confirm; це тест поглинання дубліката, не самої затримки — записати явно.
3. **C05 моделює restart процесу consumer** (нова connection після рестарту брокера), а не automatic recovery
   клієнта; automatic recovery — окремий non-blocking експеримент, якщо лишиться час.
4. **Management API** потребує `WithPortBinding(15672, true)`; credentials — з builder (`rabbitmq/rabbitmq` за
   замовчуванням Testcontainers), не guest/guest.
5. **C09 alarm** через `rabbitmqctl set_vm_memory_high_watermark 0.0000001` (memory alarm → `connection.blocked`);
   зняти через `set_vm_memory_high_watermark 0.4`. Disk alarm аналогічно (`set_disk_free_limit`) — один із двох достатньо.
6. **C03 restart під час publish**: `StopAsync/StartAsync` того самого контейнера зберігає дані quorum queue у fs
   контейнера; фіксувати, що volume не використовується (evidence про durability — у межах одного контейнера).
7. **Unroutable (C08)**: `mandatory: true` → `BasicReturnAsync`; confirm при цьому все одно приходить — тест має
   асертити саме return, а не відсутність confirm.
8. **Loss of quorum (3 вузли)** — explicit skip у suite з причиною «single-node Testcontainers»; не заявляти HA.

