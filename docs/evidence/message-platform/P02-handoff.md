# P02 — RabbitMQ/.NET spike: fan-out, ACK/confirm, відновлення після збоїв

Task: [P02 / issue #2](https://github.com/sql-monk/Puluj-g/issues/2).
Status: **done** — реалізація й тести завершені, рішення **go**; незалежне review результату (`p02_review`):
approve after fixes → fixes внесено → повторна перевірка: **approve** (B1–B2 закриті evidence). Rollout не виконувався.
Owner: агент `p02` (Claude Code fork). Reviewer: план — self-review `p02_review` (див. `P02-plan.md`); результат — незалежний субагент `p02_review` (approved).
Base commit: `6822541` + P01 commit `d4ec6be` (HEAD); результат — локальні незакомічені файли (`P02-build-manifest.json`).
Паралельно в тому самому дереві виконувався P07 (`src/`, `data/`, міграції) — файли P07 не зачіпалися.

## Результат для споживача

- **Go для RabbitMQ** (ADR-0001 → `accepted`): на реальному RabbitMQ 4.3.5 (Testcontainers, single node, quorum queues)
  пройшли gate хвилі 1 і брокерна частина crash windows ADR-0004. Деталі й числа — [`P02-crash-evidence.md`](P02-crash-evidence.md).
- Закріплені версії: образ `rabbitmq:4.3-management`, `RabbitMQ.Client` 7.2.2, `Testcontainers.RabbitMq` 4.15.0.
- `contracts/messaging/topology.json`: `queue_policies.*.broker_arguments` (`x-queue-type: quorum`, `x-delivery-limit: 4`,
  `x-dead-letter-strategy: at-least-once`, `x-overflow: reject-publish`) і секція `broker`; DLX `puluj.dlx` + DLQ на чергу.
  Контрактні тести P01 незмінні й проходять (54).
- **Ключове спостереження для P03** (spike + офіційна документація quorum queues, RabbitMQ ≥ 4.3): `x-delivery-count`
  інкрементують `basic.reject(requeue=true)` і crash/закриття каналу; **не** інкрементують `basic.nack(requeue=true)`
  (лише `x-acquired-count`) і consumer timeout → `x-delivery-limit` — запобіжник crash loop; бізнес-retries і їх ліміт —
  у `processing.attempts` **через `basic.nack`** (з `reject` ліміт 4 спрацював би раніше за `max_delivery_attempts` 5),
  вичерпання → receipt `quarantined` (commit) → `nack(requeue=false)` → DLQ. Зафіксовано в ADR-0001 п.8, ADR-0002, `topology.json`.
- Readiness: passive declare бачить лише відсутню чергу; drift binding живої черги (C08) — ні, і публікація при цьому
  confirmed/не returned (інша підписка ще bound) → P03 має перевіряти bindings через management API + reconciliation.
- Dev profile: Compose сервіс `rabbitmq` під `profiles: ["broker"]` (default deploy не змінюється; перевірено
  `docker compose config` з профілем і без), `RABBITMQ_*` у `.env.example`.

Контракти/міграції/flags: schema БД, DTO, `src/` — без змін. `Directory.Packages.props`: +`RabbitMQ.Client` 7.2.2,
+`Testcontainers.RabbitMq` 4.15.0 (лише тестовий проєкт). `Puluj.sln`: +`tests/Puluj.Transport.Spike.Tests`.
Docs: ADR-0001/0002 (accepted, версії, retry semantics, go/no-go), `docs/adr/README.md` (статуси), plan §16.1 (команда), §17.

## Spike-код (не production)

`tests/Puluj.Transport.Spike.Tests/Spike/`: `Topology` (declare з `topology.json`, readiness через passive declare),
`OutboxPublisher` (persistent + mandatory + confirms, стабільний `event_id`, outbox-заглушка), `InboxConsumer`
(manual ACK після «commit», inbox dedup, crash hooks, attempts), `FileStore` (JSON-файл як «БД»), `RabbitMqFixture`
(Testcontainers, management API, restart, `rabbitmqctl`), `Evidence`. Без ProjectReference на `src/`. P03 переписує
на `messaging.outbox/inbox` + `processing.attempts/deliveries`, relay з batch confirms.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows, .NET SDK 10.0.401, Docker engine 29.8.0, `rabbitmq:4.3-management` (server 4.3.5); три контейнери
паралельно (gate / crash / load collections). Усі команди — під `scripts/with-lock.ps1`.

| Команда | Exit | Passed / Failed / Skipped | TRX |
|---|---|---|---|
| `dotnet test tests/Puluj.Transport.Spike.Tests/...` (фінальний, після review fixes: C08 перейменовано + підкейс unbind) | 0 | **16 / 0 / 1** (G05 explicit skip: loss of quorum потребує 3 вузлів) | `test-results/p02-Puluj.Transport.Spike.Tests.trx` |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests/...` (після переформатування `topology.json`) | 0 | 54 / 0 / 0 | `p02-Puluj.Messaging.Contracts.Tests.trx` |
| `dotnet build Puluj.sln` (після зміни `Directory.Packages.props`; включно з незавершеними змінами P07 у дереві) | 0 | 0 warnings | — |
| `docker compose -f deploy/docker-compose.yml --env-file .env.example [--profile broker] config --services` | 0 | rabbitmq лише з профілем | — |

Проміжні прогони не приховані: `p02-spike-run1.trx` — **7 passed / 8 failed** (C01/C02: закриття каналу зсередини consumer
callback дедлочило — винесено в `Task.Run`; C03/C05→C06/C07/C09: після `docker stop/start` Testcontainers дає нові
host-порти — management client перестворюється; G04: `x-delivery-limit` не спрацював на `nack(requeue=true)` — див.
спостереження, тест перебудовано на attempts у «БД» + доданий G04b crash loop). `p02-spike-run2.trx` — 16 passed / 1 skipped
(без evidence-файлу); `p02-spike-run3.trx` — 16 passed (перший фінальний, до review); `p02-spike-run4.trx` — **15 passed /
1 failed**: новий підкейс C08 очікував `basic.return` після unbind, але публікація була confirmed, бо raw-writer.history ще
bound — очікування виправлено (це і є доказ §3.2 «mandatory/confirms не доводять існування кожної підписки»); фінальний —
`p02-Puluj.Transport.Spike.Tests.trx` з `P02-crash-evidence.json`. Помилки — у spike-коді/тестах, не в брокері.

Unit suites Processing/Analytics/Admin/Api/Integration **не** запускалися P02: `src/` не змінювався, а паралельний P07
змінює ці проєкти й запускає їх сам; `dotnet build Puluj.sln` зелений.

## Self-review за §16.2 (п.2, п.12) — обмеження: той самий агент

- п.2 (crash windows): W4, W5, W7a/b/c, W10a/b, W13 доведені на брокері з committed outcome у FileStore; W11 — unroutable
  (return), відсутня required черга (passive declare) і drift binding живої черги (невидимий для passive declare, видимий
  management API; публікація confirmed) — runtime readiness/reconciliation лишається P03; W6a — порядок receipt → nack → DLQ
  (G04) і delivery-limit (G04b), але падіння DLX не симульовано; W2/W3 — через C03 (unconfirmed → republish);
  W1/W8/W12/W14 — поза брокером (P04/P06/P03/P15). ACK після commit — у коді consumer.
- п.12: тести реально виконані на актуальній збірці, TRX є, проміжні невдачі названі; чисел «з пам'яті» немає —
  усі з `P02-crash-evidence.json`/`P02-spike-metrics.json`.
- Ризики, які має перевірити незалежний reviewer: (1) C04 — client-simulated delayed confirm, не серверна затримка;
  (2) C09 — memory alarm, не disk; (3) паралельні collections ділять CPU — timing-чутливі очікування мають запас
  (30–60 s); (4) `x-delivery-limit: 4` vs `max_delivery_attempts: 5` — семантика «1 + 4 redeliveries» задокументована.

## Відомі обмеження / невиконані перевірки

- Single node: HA/loss of quorum, 3 fault domains, `x-quorum-initial-group-size` — не тестовано (G05 skip; P16).
- Outbox/inbox — файлова заглушка в одному процесі; атомарність з БД, relay lease, reconciliation — P03.
- Confirms послідовні (≈138 publish/s у фінальному прогоні); throughput брокера і baseline §13 не вимірювалися; smoke — не SLO.
- Automatic connection recovery клієнта не використовувалась (нові connections після restart).
- Readiness bindings через management API та reconciliation expected-vs-receipts — не реалізовано (P03); C08 показує сліпу зону.
- Evidence-літерали (N5): частина полів у `P02-crash-evidence.json` — константи з тесту (напр. `effects_per_subscription = 1`,
  `dlq_ready = 1`), а не зчитані значення; вони стоять після відповідних assert, але reviewer має це враховувати.
- Smoke (N7): без p50/p95 латентності та без assert DLQ = 0; числа залежать від паралельних контейнерів на хості.
- C04 (N8): delayed confirm симульовано на клієнті timeout 1 tick — потенційно flaky на дуже швидкому брокері (confirm
  може встигнути); тоді тест впаде на `Assert.Equal(0, …)` і його треба переписати через штучну затримку.
- Spike-код (N11): `InboxConsumer` використовує `_channel!` у callback; при prefetch > 1 і одночасному Crash() можливий
  race на закритому каналі — прийнятно для spike, не для P03.
- Незалежне review результату: виконано координатором (`p02_review`, approve after fixes — див. таблицю нижче).
- GitHub Project card — токен `gh` без scope `project`; статус — коментарі в issue та §17.

## Review findings → виправлення → повторна перевірка

Review плану — self-review `p02_review` (8 findings, у `P02-plan.md`). Review результату — незалежний `p02_review`
(координатор): **approve after fixes**, go підтримано.

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | Семантика delivery-limit неповна (лише «nack не рахується») | ADR-0001 п.8, ADR-0002 «Queue policy», `topology.json.queue_policies.$comment`: RabbitMQ ≥ 4.3, `reject(requeue=true)`/crash інкрементують, `nack(requeue=true)`/consumer timeout — ні; P03 робить бізнес-retry через `basic.nack`; посилання на офіційну документацію | docs |
| B2 | C08 тестував unroutable + відсутню чергу, а не drift bindings; формулювання «missing binding» неточне | Тест перейменовано `C08_UnroutableOrMissingRequiredQueue_…_UnbindInvisibleToPassiveDeclare`, додано підкейс `QueueUnbindAsync`: passive declare не бачить, management API бачить, публікація confirmed/не returned, declare відновлює; переформульовано crash-evidence, handoff, ADR-0002 Readiness; «не тестовано → P03» доповнено | spike suite re-run: run4 15/1 (хибне очікування return у підкейсі), run5 (фінальний) 16/0/1 |
| N1 | `topology.json` переформатовано (diff ~400 рядків) | Відновлено стиль P01 (компактні масиви/об'єкти) з HEAD-версії + семантичні зміни; diff 17 рядків, семантика ідентична (assert у скрипті) | контрактні тести 54 passed |
| N2 | Застарілі «proposed/до P02» у asyncapi/генераторі/README/topology | `status: accepted`, тексти оновлено, `gen-asyncapi.py` перегенеровано | T14 у контрактних тестах |
| N3 | «9 confirmed» — виведене число | Написано як `first_pass_confirmed: -1` (виняток), 275 unconfirmed | crash-evidence.md |
| N4 | Base commit | `6822541` + P01 `d4ec6be` (HEAD) | handoff/manifest |
| N6 | `PublishException` не розрізняється | TODO-коментар у `OutboxPublisher.cs`; рядок у ADR-0002 «Відкрите» (P03) | — |
| N9 | Незмінність `x-*` arguments, DLX-first | ADR-0002 «Інваріанти declare для P03» (policies для змінюваних параметрів; PRECONDITION_FAILED; DLX/DLQ до основної черги) | docs |
| N5/N7/N8/N11 | Evidence-літерали, smoke без p50/p95/DLQ assert, C04 flaky-ризик, `_channel!` | Згадано у «Відомі обмеження» | — |

## Rollout / rollback / input ownership

Rollout не потрібен: жоден сервіс не споживає брокер. Compose-сервіс під профілем `broker` — opt-in; rollback = не вмикати
профіль або прибрати service/volume `rabbitmq` і `RABBITMQ_*`. Пакети — лише тестовий проєкт. Ownership: `contracts/messaging/`
(P01/P02), `tests/Puluj.Transport.Spike.Tests` (P02, тимчасовий до P03), ADR-0001/0002.

## Чекбокси issue #2

- [x] Відтворюваний integration demo + crash evidence; рішення go/no-go — **go** (single node)
- [x] Необхідні тести виконані на актуальній збірці з реальним RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — `p02_review`: approve after fixes → виправлено → повторна перевірка approve
- [x] Контракти (`topology.json`), конфігурація (Compose profile, `.env.example`), документація (ADR-0001/0002, §16.1, §17) оновлені

## Наступний task

**P03** — inbox/outbox/relay/receipts/reconciliation у PostgreSQL за ADR-0004/0006 з batch confirms; DLQ consumer, що ставить
receipt `quarantined` для crash-loop dead-letters; runtime `TopologyDeclarer`/readiness з `topology.json`; W2/W3/W6/W9/W12/W14
crash tests на PostGIS + RabbitMQ Testcontainers. Spike-код можна брати як референс, не як бібліотеку.
