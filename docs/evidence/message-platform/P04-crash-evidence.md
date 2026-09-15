# P04 — crash evidence (ADR-0004 W1a/W1b, ADR-0003 identity, §6.1 outbox+checkpoint)

Джерело чисел: `messaging-crash-evidence.json` (пишеться `tests/Puluj.Messaging.Tests/Integration` наприкінці прогону; містить також P03-записи —
той самий fixture; кожне значення стоїть після відповідного assert на committed стан БД). Середовище: Windows 11, .NET SDK 10.0.401,
Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (4.3.5). TRX: `test-results/p04-integration-run1.trx` (18/5/1 — 5 хибних
очікувань тестів після v3/precision, див. нижче), `p04-integration-run2.trx` (23/0/1), `p04-messaging-run3.trx` (43/0/1, весь проєкт після
переробки G01), `p04-messaging-run4.trx` (43/0/1, після review-правок B1/N1/N7), `p04-unit-run1.trx` (20/0). Усі команди — під `scripts/with-lock.ps1`.

Конфігурація тестів: `Messaging:Ingress:Enabled=true` (fixture `CollectorIngress` іде через `IngressWriter`), drain timeout 3 s, raw-writer insert
timeout 20 s; relay/consumer як у P03.

| Test | Вікно | Сценарій | Committed outcome (assert) | Результат |
|---|---|---|---|---|
| P04-C01 | W1a | Той самий пост опубліковано двічі (re-read після «crash» до commit) з checkpoint | 2 `ingress.received` (1 correlation), expected raw-writer 2 + archive 2, `collector_states.last_source_message_id` = id, raw 0 до raw-writer; після relay/raw-writer/archive: raw 1 (key/rev/hash заповнені, Pending), receipts raw-writer completed 1 + noop 1, 2 `raw.stored` з `is_new` true/false, `causation_id` = відповідний ingress id, `content_hash` = `raw_messages.hash`, archive 4 events | pass |
| P04-C02 | §6.1 | Trigger-помилка на `collector_states` під час `PublishAsync`; потім успіх; далі history-сторінка лише з cursor/датою | помилка → outbox 0, collector_states 0; успіх → outbox 1 + `last_source_message_id`; cursor-checkpoint не затер id, старіша дата не відкотила `last_message_at`, cursor записаний, `last_error` NULL | pass |
| P04-C03 | §5.2 | Новий id з тим самим текстом; redelivery того самого id; Telegram edit (key той самий, rev `e…`) | ingress 4 → raw 3; 2 рядки з однаковим `hash`; redelivery → noop 1; edit → окремий raw, `source_message_id = key:rev`; correlation спільна для оригіналу й редакції, різна між постами | pass |
| P04-C04 | ADR-0003 | alerts `31:start`/`31:end` + повторний end; той самий пост direct (`RawMessageIngestor`) і через ingress | raw start/end по 1, повтор end → noop; `hash` direct == ingress (cross-mode, review B1); повторний direct → `IngestResult` = існуючий id, `IsNew=false`, raw без змін | pass |
| P04-C05 | lane/drain | 3 пости `live:false` | outbox lane `history` (`puluj.history.ingress.received`), pending raw-writes 3; `WaitForDrainAsync` false до consumer (timeout 3 s), true після; raw 3 Pending; 3 `raw.stored` history | pass |
| P04-C06 | W1c | Telegram edit update до commit | **explicit skip** — потребує MTProto; межа: WTelegram update state (локальний файл) до появи durable spool | skipped |
| P04-C07 | Reset lock (review B2) | `LOCK TABLE raw_messages ACCESS EXCLUSIVE` під час доставки | 3 s: attempt `running` 1, receipt 0; після зняття lock: raw 1, attempts 1 (`succeeded`), failed 0, quarantine 0 | pass |
| P04-G01 | N8 | Фаза 1: consumer з post-commit hook, що кидає (3 копії); фаза 2: 2 репліки на 3 копії іншого поста | фаза 1: delivered 3, черга 0 (ACK), raw 1, noop 2; фаза 2: raw_total 2, is_new true 2, sum delivered 3, quarantine 0. Розподіл між репліками у прогоні 3/0 (prefetch 10 — перша репліка забрала всі; паралельний конфлікт INSERT не спровоковано, evidence не заявляє race) | pass |
| G-migration | D1 | Окремий PostGIS: міграція до `AddMessagingSchema`, 5 legacy рядків (`48213`, `48213:e…`, `31:start`, `31:end`, `weird:e12x`) → Up → Down → Up | key/revision за identity-cases; identity unique (дубль → `PostgresException`), два рядки з однаковим hash вставляються; нові колонки **без default** — INSERT старого writer без них падає (review B1); індекси: identity unique, hash non-unique, legacy unique; Down: колонки зникли, 6 рядків цілі, hash лишився non-unique; повторний Up — backfill ідемпотентний | pass |
| Unit | контракт | `IngressWriter.BuildEnvelope` для Telegram post/edit, alerts start/end (history lane) проти `envelope.schema.json` + `ingress.received.schema.json`; `content_hash` = `ComputeHash` на оригінальному JSON; identity fallback з legacy id | valid; `causation_id` присутній і null; без `raw_message_id` | pass (20/20) |

Проміжний прогін run1 (18/5): CrashTests C01/C06/C07 очікували `topology_version = 2` (тепер 3), C02 порівнював timestamp з мікросекундами
(Postgres зберігає мкс, .NET — 100 нс) → порівняння в мс, migration test очікував порядок вставки за `raw_message_id` (JOIN дав інший) → порівняння
за словником. Помилки — у тестах, не в коді.

Не тестовано (свідомо): реальні `TelegramCollector`/`AlertsInUaCollector` end-to-end (MTProto/токен) — перевірено `CollectorIngress` і
`IngressWriter`, якими вони користуються; паралельний race двох raw-writer реплік на одному identity (G01 фаза 2 не спровокувала; захист —
unique index + `ExistingIdAsync`); W1c.
