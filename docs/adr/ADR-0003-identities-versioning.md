# ADR-0003 — Ідентичності, час і версіонування контрактів

Статус: **proposed** (P01). Machine-readable: [`envelope.schema.json`](../../contracts/messaging/schemas/envelope.schema.json),
[`identity-cases.json`](../../contracts/messaging/fixtures/identity-cases.json), [`compatibility.json`](../../contracts/messaging/fixtures/compatibility.json).
Вимоги: plan §5.1, §5.2, §15.1, §15.2.

## Контекст (що є в коді)

- `raw_messages` має два unique ключі: `(source_id, source_message_id)` і content `hash`
  (`RawMessageIngestor.ComputeHash`: source code + text + payload, без message id). Однаковий текст під
  новим id від того самого джерела **не** отримує окремий raw (plan §2). `IngestResult(null, false)` при повторі.
- Telegram (`TelegramCollector.StoreAsync`): `source_message_id = "{id}"` для оригіналу і `"{id}:e{editUnix}"`
  для редакції; `PublishedAt = edit date ?? date`. Checkpoint не просувається для edits.
- alerts.in.ua (`AlertsInUaCollector`): `"{alert_id}:start"` і `"{alert_id}:end"` — два різні повідомлення з
  різним payload (`Wrap` → `{kind: alert.started|alert.finished, at, alert}`); для `:end` `PublishedAt = now` (час спостереження
  collector). Повторний `:end` дедуплікується лише за `(source_id, source_message_id)`, бо hash містить `now`.
- Lease processor: `claimed_by` + `claimed_at`, без fencing token; `ProcessingOptions.ClaimLease` = 5 хв.

## Рішення

### Raw identity: `(source_id, source_message_key, source_revision)`

| Джерело | `source_message_key` | `source_revision` | Legacy `source_message_id` |
|---|---|---|---|
| Telegram оригінал | `"{message_id}"` | `"0"` | `"{message_id}"` |
| Telegram редакція | `"{message_id}"` (той самий) | `"e{edit_unix}"` | `"{message_id}:e{edit_unix}"` |
| alerts.in.ua start | `"{alert_id}:start"` | `"0"` | `"{alert_id}:start"` |
| alerts.in.ua end | `"{alert_id}:end"` | `"0"` | `"{alert_id}:end"` |
| Джерело без ревізій | стабільний id джерела | `"0"` | id |
| Джерело з явною ревізією | стабільний id | стабільний рядок ревізії джерела | — |

- **Фаза ≠ ревізія.** `:start`/`:end` — різні джерельні повідомлення (різні `correlation_id`); Telegram edit —
  ревізія того самого поста (та сама `correlation_id`, окремий raw і окремий `event_id`).
- Content `hash` лишається **індексом схожості**, не причиною відкинути новий пост. Зняття unique constraint
  з `hash`, backfill `(key, revision)` із legacy `source_message_id` за таблицею вище, повернення існуючого
  raw id при повторі — **P04**; повідомлення, раніше відкинуті content-dedup, з hash не відновлюються.
- Hash-derived revision (для джерел, що редагують без ознаки) — лише за явним рішенням P04, не за замовчуванням.
- Межі відновлення (plan §6.1): Telegram edit update, для якого `IngestAsync` не закомітився, DB checkpoint
  (`min_id`, лише оригінали) не відновлює; відновлення можливе лише через WTelegram update state
  (`SessionPath + ".updates"`, локальний файл, не durable за §6.1) — до P04 collector outbox це відома межа;
  для alerts `:end` business time = час спостереження, не джерела, і має бути позначений у payload
  (`source_published_at` = час collector, `raw_payload.at`).

### Event identity

- `event_id` — UUIDv7, генерується **один раз** при вставці в `messaging.outbox`; повторна публікація того
  самого рядка (relay retry, redelivery) несе той самий `event_id`; inbox дедуплікує за `(subscription_id, event_id)`.
- `correlation_id` — один на джерельний пост (усі результати одного raw). Для aggregate-scoped подій —
  correlation повідомлення-причини; повний граф багатьох входів → `messaging.event_links` (ADR-0006).
- `causation_id` — `event_id` події, що безпосередньо запустила крок; `null` лише для `ingress.received`.
  **Bridge (P03, до P04):** `raw.stored`, який collector комітить напряму без `ingress.received`, є коренем ланцюга і
  має `causation_id = event_id` (self-causation, resolvable в архіві; правило «causation == event_id ⇔ bridge root»).
  `correlation_id` bridge — детермінований UUIDv5 від `(source_id, source_message_key)` (`SourceIdentity.CorrelationId`), тож
  оригінал і його редакції ділять correlation без lookup; `source_message_key`/`source_revision` виводяться з legacy
  `source_message_id` за таблицею вище (`SourceIdentity.FromLegacy`).
- `published_at` — момент запису в outbox (перша публікація з боку producer); transport-повтори relay видно в
  `messaging.outbox.attempts/last_attempt_at`, envelope не змінюється.
- `traceparent` — W3C Trace Context; новий span на кожен consumer, той самий trace на ланцюг.
- Replay: **новий** `processing_run_id`, нові `event_id`; зв'язок з оригіналом — через `raw_message_id`,
  `correlation_id` і `processing.runs.replays_run_id` (ADR-0005).

### Час (чотири шкали, plan §5.1, §8.5)

| Поле | Де | Значення |
|---|---|---|
| `payload.source_published_at` | `ingress.received`, `raw.stored` | Час публікації в джерелі (Telegram date/edit_date; alerts `started_at` для start, час спостереження для end) |
| `payload.received_at` | ті самі + `timings` | Коли collector отримав |
| `occurred_at` | envelope | Business/event time факту, який описує подія (для `raw.stored` = `stored_at`; для `track.changed` = `effective_at`) |
| `published_at` | envelope | **Перша** публікація `event_id`; republish не змінює; transport retry timestamps — в outbox attempts/headers |
| `recorded_at` / `effective_at` | payload агрегатів | Коли система дізналась / коли стан діє (as-of історія — P10/P11) |

### Версії

| Поле | Семантика |
|---|---|
| `schema_version` `MAJOR.MINOR` | Payload schema події. Той самий MAJOR — сумісно (consumer ігнорує невідомі поля, нові поля optional); інший MAJOR або невалідна версія → `quarantined` з причиною, не exception. Видалення/перейменування required поля, зміна типу → новий MAJOR. Тому схеми **не** мають `additionalProperties: false` на верхньому рівні envelope/payload (T10). |
| `topology_version` | Версія `topology.json`, що фіксує очікувані підписки на момент публікації. |
| `pipeline_version` | Build identity producer (напр. `2026.09.15+6822541`); для порівняння результатів між збірками. |
| `processing_run_id` / `generation_id` | ADR-0005. |
| `payload.versions.{normalization,rules,ruleset_id,model,prompt,catalog_policy}` | Версії, що визначили результат; зберігаються в stage_results/extraction (P05/P06/P08). |
| `aggregate_revision` | Монотонна ревізія агрегату; client/projection ігнорує старіші. |
| `fencing_token` | Монотонний токен lease на job (LLM); результат зі старим токеном відкидається (ADR-0004 W8). |

### Payload vs `payload_ref` (proposal)

Envelope містить `payload` **або** `payload_ref` (`uri`, sha256 `checksum`, `size_bytes`, `content_type`).
Поріг — proposal: payload > 256 KiB або будь-які бінарні вкладення → durable blob storage з checksum,
окремим retention і ACL; в `ingress.received` вкладення завжди через `attachments[]` refs. P04 підтверджує
поріг і storage (локальний volume vs object storage).

## Альтернативи

- Ревізія всередині key (як зараз) — не дає порахувати «пости vs редакції» без парсингу рядка і робить
  `:end` схожим на редакцію; відкинуто.
- `event_id` як детермінований hash(content) — колізії при однаковому контенті в різних run; UUIDv7 + outbox простіше.
- Semver з PATCH — зайве для payload-схем; PATCH нічого не змінює для consumer.

## Наслідки

- P04 міграція: додати `source_message_key`, `source_revision` (backfill з `source_message_id`), нова unique
  `(source_id, source_message_key, source_revision)`, hash → non-unique index; `IngestResult` повертає id при повторі.
- Consumers мають робити compat-рішення до десеріалізації payload; невідомий `event_type` → quarantine.
- Analytics рахує «пости» за `(source_id, key)`, «редакції» за `revision`, «фази» alerts — як окремі повідомлення.

## Відкрите

| Питання | Задача |
|---|---|
| Unique hash migration, backfill, `IngestResult` при повторі, hash-derived revision для нових джерел | P04 |
| Payload/attachment storage і поріг | P04 |
| Fencing token у `processing.attempts` (колонка є з P03, значення 0), lease takeover | P06 |
| Заміна bridge-правила `causation_id = event_id` на справжній `ingress.received` causation | P04 |
| Формат `aggregate_id` (`track:5501`) vs окремі поля — узгодити з read-side | P09/P11 |
