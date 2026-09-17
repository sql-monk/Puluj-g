# ADR-0006 — Логічна модель даних `messaging.*`, `processing.*`, `analytics.message_*`

Статус: **accepted** для `messaging.*`/`processing.*` (P03, міграція `20260915150214_AddMessagingSchema`, entities
`Puluj.Domain.Entities.{Messaging,Processing}`, конфігурації `Persistence/Configurations/{Messaging,Processing}Configuration.cs`);
`analytics.message_*` — proposed до P15. Запропоновано P01. Рівень цього документа — логічний; фізичні відхилення P03 — у розділі
«Фізична модель (P03)». Індекси обрані за access paths коду, не за `EXPLAIN` на production-shaped даних (це лишається для P16). Вимоги: plan §5.2, §10, §12 (analytics lifecycle schema у хвилі 1), §15.2.

## Принципи

- Inbox/outbox належать БД, де етап змінює свій результат. Analytics живе у тій самій PostgreSQL
  (схема `analytics`, `AnalyticsDbContext`), тому має власні `inbox`, без distributed transaction.
- Додаткові схеми `messaging`, `processing` — additive; існуючі таблиці не перейменовуються у P03.
- Broker ACK і DB commit не атомарні; receipts збираються асинхронно, без глобального бар'єра на ACK.
- Append-only там, де це evidence (`events`, `attempts`, `deliveries` receipts); rebuildable там, де проєкція.

## ERD

```mermaid
erDiagram
    raw_messages ||--o{ processing_stage_results : "raw_message_id"
    processing_runs ||--o{ processing_stage_results : "run_id"
    processing_stage_results ||--o{ processing_attempts : "stage_result_id"
    messaging_outbox ||--o| messaging_events : "event_id (archive)"
    messaging_events ||--o{ messaging_event_links : "input/output"
    messaging_events ||--o{ processing_deliveries : "event_id"
    messaging_subscriptions ||--o{ processing_deliveries : "subscription_id, topology_version"
    messaging_subscriptions ||--o{ messaging_inbox : "subscription_id"
    processing_generations ||--o{ processing_runs : "generation_id"
    raw_messages ||--o{ analytics_message_lifecycle : "raw_message_id, run_id"
```

## Таблиці

### `messaging` (owner P03)

| Таблиця | Ключі / access-path hints (proposal, індекси остаточно — P03 за EXPLAIN) | Призначення / колонки (proposal) | Retention |
|---|---|---|---|
| `messaging.outbox` | PK `event_id`; index `(confirmed_at NULL, next_attempt_at)` | Подія для гарантованої публікації: envelope (jsonb), routing_key, lane, `created_at`, `lease_owner`, `lease_until`, `attempts`, `next_attempt_at`, `last_error`, `confirmed_at` | Cleanup **лише** після archive receipt для `replay_source` подій (§15.2); інакше — після confirm + grace |
| `messaging.inbox` | unique `(subscription_id, event_id)` | Consumer dedup; `received_at`, `completed_at`, `outcome`; запис у транзакції результату | За підпискою: ≥ max redelivery window (proposal 30 днів) |
| `messaging.events` | PK `event_id`; index `(correlation_id)`, `(raw_message_id, run_id)`, `(event_type, published_at)` | Архів envelope + `payload`/`payload_ref` (append-only); джерело replay/backfill нових підписок | За policy (ADR-0007); ніколи не коротше за raw retention для `replay_source` |
| `messaging.event_links` | unique `(output_event_id, input_event_id)` | Causal links для batch результатів (багато входів → один вихід) | = events |
| `messaging.subscriptions` | PK `(subscription_id, topology_version)` | Версіонований registry (копія `topology.json` при deploy): bindings, lanes, required, queue_policy, status (`planned/active/paused/retired`), `activated_at`, `waiver` | Назавжди (малий) |
| `messaging.topology_versions` | PK `topology_version` | Хеш `topology.json`, `applied_at`, `applied_by` | Назавжди |

### `processing` (owner P03; stage_results/attempts — P05/P06)

| Таблиця | Ключі / access-path hints (proposal) | Призначення / колонки | Retention |
|---|---|---|---|
| `processing.runs` | PK `run_id`; index `(lane, state)` | ADR-0005: `lane`, `kind (live/history/replay)`, `state`, `generation_id`, `supersedes_run_id`, `replays_run_id`, versions jsonb, scope jsonb (sources, interval, stages), `checkpoint` jsonb, `created_by`, timestamps | Назавжди |
| `processing.generations` | PK `generation_id`; one row `is_active` per domain | Набір доменних результатів; `promoted_at`, `rolled_back_at`, `verified_by` | Назавжди |
| `processing.stage_results` | unique `(raw_message_id, run_id, stage, stage_version)` | `outcome`, `outputs` jsonb (event_ids, extraction_result_id), `started_at`, `finished_at`, `worker`, `versions` | Live: = raw; replay non-promoted: policy |
| `processing.attempts` | PK `attempt_id`; index `(stage_result_id)`, `(job_key, fencing_token)` | Кожна спроба: `worker`, `lease_until`, `fencing_token`, `state (running/interrupted/failed/succeeded/superseded)`, `error`, `retry_reason`, `retry_of_attempt_id`, timing | Proposal 90 днів |
| `processing.deliveries` | unique `(event_id, subscription_id)` | Expected/terminal receipts: `topology_version`, `expected_at`, `outcome (completed/noop/quarantined/waived)`, `completed_at`, `reason`, `actor`, `attempt_id` | = events для replay_source; інакше 90 днів |
| `processing.quarantine` | PK `quarantine_id`; index `(subscription_id, event_id)` | Payload/помилка для DLQ receipts, `resolved_at`, `retry_run_id` | До розв'язання + 90 днів |

Legacy `raw_messages.processing_status`, `claimed_by`, `attempts` лишаються compatibility-проєкцією до P05/P14.

### `analytics.message_*` (owner P15; проєктується тут, щоб не втратити lifecycle під час переходу)

| Таблиця | Ключі | Призначення |
|---|---|---|
| `analytics.message_lifecycle` | unique `(raw_message_id, run_id)` | Одна стрічка проходження: `source_id`, `source_message_key`, `source_revision`, `lane`, `source_published_at`, `received_at`, `stored_at`, `normalized_at`, `parsed_at`, `llm_completed_at`, `analyzed_at`, `domain_completed_at`, `visible_at`, `outcome`, `method`, `fact_count`, `expected_branches`, `needs_attention`, `versions`; **upsert часткового запису** при out-of-order (ADR-0004 W14b), `unavailable` (NULL + flag) для історичних timings, яких не збирали |
| `analytics.message_stage_timings` | unique `(raw_message_id, run_id, stage)` | wait/processing тривалості за стадією для p50/p95/p99 |
| `analytics.message_receipts` | unique `(event_id, subscription_id)` | Проєкція deliveries для «хто чекає/завершив/помилився» |
| `analytics.source_daily` | unique `(source_id, day, lane)` | Пости/редакції/forwards, live vs history, затримка збору, перерви |
| `analytics.parse_daily` | unique `(day, source_id, method, outcome, rules_version, model)` | Rules vs LLM, no_facts/unknown/unsupported/multi-fact/unlocated |
| `analytics.cost_daily` | unique `(day, model, stage)` | Tokens/cost/cache/retries/latency |
| `analytics.similarity_*` | (існуючі copies/fingerprints) | Допоміжний розділ; legacy таблиці видаляються окремим етапом |

Правила: root message count рахується один раз на `raw_message_id` (не на run/редакцію); знаменники
(отримано / розібрано / фактів / deliveries) — окремі колонки; сховище — UTC, UI — Europe/Kyiv.

## Ідемпотентність за таблицею

| Writer | Ключ ідемпотентності |
|---|---|
| raw-writer | unique `(source_id, source_message_key, source_revision)` → існуючий id, `is_new:false` |
| stage consumers | `messaging.inbox (subscription_id, event_id)` + `stage_results` unique |
| finalizer | `stage_results (raw, run, 'extraction', version)` — рівно один канонічний extraction на run |
| domain workers | inbox + `targets.observation_id` unique + lock hierarchy ADR-0009 (Store shared → track → category / alert region); `aggregate_revision` = `target_tracks.revision`/`air_alerts.revision` (P09) |
| archive | `messaging.events` PK `event_id` |
| analytics | upsert `(raw_message_id, run_id)`; агрегати — recompute з lifecycle rows, не інкремент |

## Rollback / міграція (для P03/P15)

- Усі об'єкти additive у нових схемах; rollback = не читати їх (legacy шлях лишається).
- `messaging.subscriptions` заповнюється з `topology.json` при deploy; розбіжність hash → alarm.
- Індекси — за `EXPLAIN (ANALYZE, BUFFERS)` на production-shaped копії; concurrent builds поза EF transaction.

## Відкрите

| Питання | Задача |
|---|---|
| ~~Типи колонок, індекси~~ — done P03; партиціювання `events`/`deliveries` за часом і `EXPLAIN` на production-shaped даних | P16 |
| Analytics DDL, backfill з legacy `raw_messages`/`processing_errors`, позначення `unavailable` | P15 |
| Розмір `payload` в `events` vs `payload_ref` до blob | P04 |

## Фізична модель (P03)

Відхилення від логічної таблиці вище, усі additive:

- `messaging.outbox`: surrogate PK `outbox_id` (identity) замість `event_id`, бо admin retry (ADR-0004 W6b) додає **redelivery-рядок**
  того самого `event_id` з `target_queue` (publish через default exchange у чергу підписки). Unique `(event_id) WHERE target_queue IS NULL`;
  partial index `(next_attempt_at) WHERE confirmed_at IS NULL` для relay; `(confirmed_at) WHERE confirmed_at IS NOT NULL` для cleanup;
  колонка `replay_source` (копія з registry) — cleanup чекає archive receipt лише для таких рядків.
- `messaging.inbox`: PK `(subscription_id, event_id)`, `outcome` ∈ `processing | completed | noop | quarantined` (`processing` видно лише
  всередині транзакції consumer); brin `(completed_at)` для retention.
- `messaging.events`: PK `event_id`; `envelope` — **тіло як отримано** (додаткові поля новішого MINOR зберігаються); індекси
  `(correlation_id)`, `(raw_message_id, processing_run_id)`, `(event_type, published_at)`, brin `(published_at)`.
- `messaging.subscriptions`: PK `(subscription_id, topology_version)`; `status` — джерело істини після першого insert (pause/waiver/activate
  через `SubscriptionAdmin`), `bindings`/`lanes` jsonb — копія файлу; `waiver` jsonb — останній audit-запис.
- `messaging.topology_versions`: PK `topology_version`, `hash` (SHA-256 канонічного JSON) — інший файл під тією самою версією → помилка старту.
- `processing.runs`: partial unique `(lane) WHERE state = 'running' AND kind IN ('live','history')` — один відкритий run на lane (P14 знімає для replay).
- `processing.generations`: partial unique `(is_active) WHERE is_active`; рядки з'являться в P14.
- `processing.attempts`: `job_key = "{subscription_id}:{event_id}"`, `fencing_token` = 0 до P06, `retry_of_attempt_id`/`retry_reason='admin_retry'`
  після retry з quarantine, `state` ∈ `running | succeeded | failed | interrupted | superseded`; індекси `(subscription_id, event_id)`, `(job_key, fencing_token)`;
  з P05 `stage_result_id` заповнює consumer, коли handler повертає `DeliveryResult.StageResultId`.
- `processing.stage_results` (writers з P05: `normalize` — `NormalizerHandler`, `parse` — `ParserHandler`): `stage_version` = `Normalizer.Version` /
  `RuleParser.Version` / `AlertsInUaStructuredAdapter.Version` / `empty`; `outputs` jsonb (text_kind, hash, outcome, facts_count, attempt_id (uuid спроби
  парсингу з `parse.completed`), event ids), `versions` jsonb; insert `ON CONFLICT DO NOTHING` — повтор того самого raw/run/stage → `noop` без другої події.
  `ReprocessService.ResetAsync` (legacy) стадій не чіпає; replay (P14) — інший run. Стадія `finalize` (P06) має проміжний outcome `awaiting_llm`, який
  оновлюється до terminal (guard: лише з `awaiting_llm`/`failed`, terminal не понижується).
- **`processing.extractions`** (P06, замість `stage_results (raw, run, 'extraction', version)` з логічної моделі): `extraction_id` uuid PK, unique
  `(raw_message_id, run_id)` — рівно один канонічний immutable extraction на run (`extraction_version` = 1; повторний розрахунок = новий run), `method`,
  `outcome`, `versions`/`facts`/`error` jsonb, `llm_request_ids uuid[]`, `finalized_by`; **`processing.observations`**: `observation_id` uuid PK, FK extraction
  (cascade), `raw_message_id`, `run_id`, `event_kind_code`, `category`, `effective_at`, `payload` jsonb (факт за `$defs/observation`), `legacy_target_id`
  null у compat window; з P09 writers заповнюють `legacy_target_id` у тій самій tx, що й рядок `targets` (`observation_id`).
- P09 (`AddAggregateRevisions`): `targets.observation_id uuid NULL` (partial unique, CONCURRENTLY; NULL = рядок legacy loop), `target_tracks`/`air_alerts`:
  `revision int`, `last_event_id uuid`, `last_correlation_id uuid` (causation chain для watchdog-команд). Writers `targets` для не-alert фактів — track-worker,
  для alert — alert-worker; для incident — incident-worker (P10); info — track-worker.
- P10 (`AddIncidents`, ADR-0010): `incidents` (state check, `suppressed`, `event_at`/`first_reported_at`/`last_reported_at`, location + `accuracy_km`,
  `source_count`, `independent_source_count` NULL, `canonical_observation_id`, `revision`, `closure_reason`, `merged_into_incident_id`, `last_event_id`,
  `last_correlation_id`, `generation_id`, `run_id`; індекси `(event_kind_id, event_at)`, `(state, last_reported_at)`, `(generation_id)`), `incident_observations`
  (PK `(incident_id, observation_id)`, unique `(observation_id)`, `relation`, `score`, `decision_reason` jsonb, `policy_version`, `legacy_target_id` без FK,
  `source_id`, `effective_at`, `linked_at`), `incident_revisions` (unique `(incident_id, revision)`, `change`, `effective_at`, `recorded_at`,
  `triggering_event_id`, `actor`, `reason`, `snapshot` jsonb). `ReprocessService.ResetAsync` ці таблиці не чіпає (P14).
- `llm_requests` (P06): +`request_id`, `run_id`, `fencing_token`, `attempt_id`, `provider_request_id`; (`AddLlmPayloads`) +`request_payload jsonb`,
  `response_payload jsonb` — дослівні тіла запиту/відповіді (або тіло помилки) в обох шляхах; `outcome` ∈ `answered → applied | late`, коди помилок
  провайдера; рядок пишеться autocommit до result-tx. `processing.attempts`: partial unique `(job_key, fencing_token) WHERE fencing_token > 0`
  (lease takeover), job-рядки llm-worker під `subscription_id = 'llm-worker:job'` (не рахуються consumer'ом; `event_id` = `request_id` команди,
  `state` ∈ `running | succeeded | failed | superseded | interrupted` — останнє ставить takeover після `lease_until` або cancel під час виклику).
  Індекс `ix_processing_attempts_job_key_fencing_token` (неунікальний) дублює partial unique — прибрати наступною міграцією.
- `processing.deliveries`: PK `(event_id, subscription_id)`; expected рядок = `outcome IS NULL`; partial index `(expected_at) WHERE outcome IS NULL`;
  `completed` ніколи не понижується (upsert лише з `NULL`/`quarantined`).
- `processing.quarantine`: partial unique `(subscription_id, event_id) WHERE resolved_at IS NULL`; зберігає **повний envelope + headers** —
  єдине джерело для redelivery після outbox cleanup; `resolution` ∈ `retried | waived`, `retry_outbox_id`.
- Ролі `puluj_reader`/`puluj_admin` отримують на обидві схеми ті самі права, що на `public` (міграція).
