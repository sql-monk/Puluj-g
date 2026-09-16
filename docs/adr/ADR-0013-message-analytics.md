# ADR-0013 — Аналітика життєвого циклу повідомлень (lifecycle projection)

Статус: accepted (P15). Стосується плану §10, §5.2 (`analytics.message_*`), ADR-0002 (v10: `message-analytics` active), ADR-0003 (identity post/revision),
ADR-0005 (workflow stages, roots per raw, results per (raw, run)), ADR-0006 (data model), ADR-0012 (audit контролів).

## Контекст

«Хто кого копіює» (`Puluj.Analytics.Worker`, cursor-скан `raw_messages` за raw id) відповідає лише на питання схожості. §10 вимагає сторінку про весь
життєвий цикл: джерела й надходження, проходження конвеєра з часом і втратами на переходах, розбори, якість, вартість, результати, історія змін —
з відомими знаменниками, з видимими no-text/failed, з пізніми результатами через події + reconciliation (не скан за raw id), і без вигаданих timings
для повідомлень, оброблених до появи стадій.

## Рішення

1. **Проєкція `analytics.message_lifecycle`** — один рядок на `(raw_message_id, run_id)`. Root-поля (`source_id`, `source_message_key`, `source_revision`,
   `lane`, `published_at`, `received_at`, `stored_at`, `has_text`, `text_length`, `has_payload`, `is_edit`); аналіз (`analyzed_at`, `analysis_outcome`
   `completed|no_facts|unsupported|needs_review|failed|legacy`, `method rules|llm|legacy`, `fact_count`, `unlocated_facts`, `versions`, `timings`,
   `timings_available`, `error`); домен (`expected_branches`, `branches_done`, `domain_completed_at`, `completion_available`, `incident_ids`, `track_ids`,
   `alert_ids`, `generation_id`); вартість (`llm_calls`, `llm_input_tokens`, `llm_cache_tokens`, `llm_output_tokens`, `llm_cost_usd`, `llm_latency_ms`);
   `source_of_truth event|backfill|reconciliation`. **DDL — у міграціях `PulujDbContext`** (роль `migrate` завжди раніше за consumers; deploy-ordering
   Analytics.Worker ↔ consumer зникає), `AnalyticsDbContext` мапить таблицю з `ExcludeFromMigrations`. Два writers (consumer, backfill/reconciliation), один
   owner DDL; drift ловлять тести обох контекстів.
2. **Знаменники.** Root = raw-рядок; **пост** = `(source_id, source_message_key)`; **редакція** = інша `source_revision` того самого поста (ADR-0003). Звіт
   показує raw / пости / редакції окремо; результати рахуються per `(raw, run)`; deliveries — у ops snapshot (P13), не тут. Повторний `raw.stored{is_new:false}`
   і redelivery не збільшують root count (PK).
3. **Consumer `message-analytics`** (topology **v10**, active; `MessageAnalyticsHandler`, роль `message-analytics` у `messaging`): `raw.stored` → upsert root;
   `message.analysis.completed` → outcome/method/facts/versions/timings/error, `expected_branches` — лише ті гілки, що обслуговують lane (replay без
   track/alert), cost = **усі** рядки `llm_requests` за (raw, run) (retries/takeover/late — оплачені), cache tokens окремо; `track/alert/incident.changed` →
   id + `branches_done`, `domain_completed_at` коли `expected_branches ⊆ branches_done`; порожні expected → complete при аналізі. Усі записи —
   `ON CONFLICT DO UPDATE` без перезапису чужих полів (порядок подій довільний). Події без `raw_message_id` (expiry/admin) → `noop no_raw`. Replay lane →
   рядок replay run'а (окрема історія, не funnel). Analytics-lag не є доменною стадією: `DomainWatchdog.WatermarkAsync` виключає `message-analytics`
   (pending analytics deliveries не зупиняють expiry); replay verify (P14) чекає analytics — консистентно з `analytics_caught_up` (ADR-0005).
4. **Backfill** (`LifecycleBackfill`, цикл Analytics.Worker + `POST /api/admin/analytics/lifecycle/backfill`, audit `analytics:backfill`): за raw id з
   курсором `analytics.state lifecycle_backfill_cursor`, батчами в одній tx; з `raw_messages`, `processing.extractions/observations/stage_results`,
   `messaging.events ⋈ processing.deliveries`, `incident_observations`, `track_targets`, `air_alerts`, `llm_requests`. Raw без extraction → synthetic run
   `legacy` (`UUIDv5(run:legacy)`), lane `legacy`, outcome з `processing_status` (`Processed → legacy`, `Failed → failed`, `Skipped → unsupported`),
   `timings_available = false`; без archived events → `completion_available = false`. **Unknown записується як unknown**: у legacy-рядку `stored_at = NULL`,
   `analyzed_at = processed_at` (старий цикл ставив його на будь-якому термінальному статусі), нічого не вигадується. **Legacy = без жодного сліду
   платформи** (немає ні extraction, ні `messaging.events` для raw) **і старший за `LegacyGrace` (10 хв)** — platform-raw, який consumer ще не спроєктував
   (лаг, пауза), лишається подіям, а не стає «legacy». Stage-рядок без `observations.recorded` (0 фактів) при наявних archived events run'а →
   `domain_completed_at = analyzed_at` (нічого не очікувалось). Event-рядки не перезаписуються (backfill лише доповнює NULL/порожнє).
   `POST /analytics/reset` copy-аналітики не чіпає `lifecycle_*` ключів.
5. **Reconciliation** (`LifecycleReconciliation`, кожен цикл Analytics.Worker, `Analytics:LifecycleReconcileWindow` 48 год, grace 2 хв; `POST …/reconcile`,
   audit): (a) пізні розбори — `analyzed_at IS NULL` при наявному extraction; (b) пізні completion — усі expected гілки мають terminal receipt
   (`completed|noop|waived`) — закриває `noop`-гілки без події і загублені події; порожній expected set без completion закривається без receipts
   (NULL LEFT JOIN — не «pending»); (c) лічильники raw/пости/редакції vs проєкція, лише старші за grace; відсутні roots (≤ 2000 за прохід) дописуються з
   evidence **за id**, не переписуванням вікна. Звіт (`lifecycle_reconciliation_report`) показує `late_filled`, pending, unavailable. «Late» = дописано
   reconciliation'ом (`source_of_truth = reconciliation`).
6. **Звіт** (`LifecycleReportService`, `GET /api/admin/analytics/lifecycle?hours=24|168|720`): funnel raw → posts → stored → analyzed → with facts →
   domain_completed → visible (incidents active generation — обчислюється при запиті, не зберігається), p50/p95 переходів лише для `timings_available`,
   stuck окремо від unavailable; timeline bucket: година для 24 год, **Kyiv-доба** для 168/720 (`AT TIME ZONE 'Europe/Kyiv'`); джерела (raw/пости/редакції/
   no-text/payload/факти/довжина/затримка збору/live vs history/макс. перерва); розбори (outcomes, методи, multi-fact, unlocated, версії правил/моделей);
   якість (review outcomes з `incident_revisions` оператора — оператор = актор, що не збігається з системними шаблонами `*-worker@…`, `watchdog@…`,
   `replay|projection|finalizer|parser|normalizer|system@…`; precision/recall та rules-vs-LLM — **«unavailable»** до розміченої вибірки/shadow-порівняння,
   P16); вартість (per model/source за `llm_requests.occurred_at` у вікні — **усі lanes, включно з replay**, тобто інший знаменник, ніж funnel; latency;
   failures = `429|api_error|error|invalid_response|timeout`; late; cache share); результати (event kinds, точність локації incidents, provenance coverage);
   історія (per run/pipeline_version, incidents per generation vs active). Read-only tx, `statement_timeout 20s`, кеш `Analytics:ReportCacheSeconds` (30),
   індекс `(received_at, source_id, analysis_outcome)`. Відхилення від ADR-0006 (`source_daily`/`parse_daily`): агрегати рахуються з рядків; перехід на
   daily-таблиці — коли 720-годинний звіт перевищує 2 с.
7. **UI.** Сторінка «Аналітика повідомлень» — перша в групі «Аналітика»; «Хто кого копіює» лишається як допоміжний розділ (legacy analytics видаляється
   окремим етапом після перевірки — план §10, P16). Знаменник названо поруч із кожною часткою; «unavailable», «legacy», «ПОМИЛКА» — словами.

Уточнення з review результату: `domain_completed_at`, коли domain-події випередили розбір, = `occurred_at` розбору, а гілка вважається done з
**першої** `*.changed` (analyzed→domain p50 для таких рядків ≈ 0 — ціна «результат видно одразу», `last_domain_event_at` не зберігається); expected set
береться з файлових lanes topology — DB-override статусу підписки (paused/retired, ADR-0002) не враховується: retired гілка не дасть completion, поки її не
приберуть із topology (edge case, P16).

## Наслідки

- Активація `message-analytics` розширює expected set `raw.stored`/`message.analysis.completed`/`*.changed` (ADR-0002); noop-receipts на expiry/admin
  події прийнято (near-real-time domain ids для історії змін).
- Тести Messaging: consumer projection зупиняється перед TRUNCATE і стартує після purge; exact-лічильники deliveries виключають `message-analytics`.
- Без Analytics.Worker проєкція наповнюється лише подіями (без backfill/reconciliation) — статус сторінки показує «звіту ще немає».

## Відкладено з P15 (owner)

| Пункт | Owner |
|---|---|
| Precision/recall на розміченій вибірці; rules-vs-LLM shadow-порівняння | P16 |
| Видалення legacy copy-analytics API/таблиць; similarity module reuse | P16 (legacy retirement) / після даних |
| ADR-0003: `RawMessageReader` `:e{ts}` → `source_message_key/revision` | P16 |
| Daily-агрегати (`source_daily`, `parse_daily`, `cost_daily`, ADR-0006) | коли 720h звіт > 2 с |
| Projection checkpoint/delta (ADR-0011), stats/links у projection consumer (ADR-0009), backfill forecast / silent-source alarm (ADR-0012) | P16 |
| `unknown.unclassified` як явний outcome (ADR-0008) | P16 |
