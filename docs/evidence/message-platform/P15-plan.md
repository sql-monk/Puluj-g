# P15 — analytics lifecycle projections, source/parse/cost/quality dashboards, backfill; заміна copy UI (§10) — план

Task: [P15 / issue #17](https://github.com/sql-monk/Puluj-g/issues/17). Залежності: схема — P01 (ADR-0005/0006), виконання — P06/P10/P14 — done.
Base commit: `37402e1` (P14). Gate: counts reconciliation; late results; no-text/failed видимі; старі невідомі timings позначені unavailable.

## Стан коду на старті (звірено)

- Є: `Puluj.Analytics` (окремий сервіс `Puluj.Analytics.Worker`, схема `analytics` з власною `__EFMigrationsHistory`: `state`, `runs`, `messages`
  (fingerprints), `copies`, `track_firsts`; `AnalysisRunner` — cursor-скан `raw_messages` за raw id, `CopyDetector`, `AnalyticsReportService`
  (`/api/admin/analytics/status|report|recent|pairs|reset`), панель «Хто кого копіює» + «Стан сервісу» (`web/src/admin/analytics/*`)).
  Топологія v9: підписка `message-analytics` **planned** (bindings `raw.stored`, `message.analysis.completed`, `track.changed`, `alert.changed`,
  `incident.changed`; lanes live/history/replay; `idempotency: upsert(raw_message_id, run_id) + reconciliation`) — черги не оголошені, deliveries не
  очікуються. `message.analysis.completed` несе `raw_message_id, outcome, method, fact_count, expected_branches, versions, timings {received_at, stored_at,
  normalized_at, parsed_at, llm_completed_at, finalized_at}, error, llm_request_ids`; `llm_requests` — tokens/cost/latency/model per raw; receipts
  `processing.deliveries` (+`lane`, P13); runs/generations (P14); `event_kinds` каталог; incidents з precision (`LocationKind`), `incident_revisions`
  (actor `operator` для review outcomes); `control_audit`.
- Немає: lifecycle-проєкції per raw (ingested → stored → analyzed → facts → domain → visible), consumer `message-analytics`, backfill з durable evidence,
  reconciliation лічильників, дашбордів джерел/конвеєра/розборів/якості/вартості/результатів/історії, сторінки «Аналітика повідомлень».
- Паралельно інший агент працює у `web/` (public UI) і `Puluj.Api` — P15 торкається лише `web/src/admin/*`, `web/src/api/adminAnalytics*.ts`, `web/e2e/A06*`.

## Рішення

### D1. Проєкція `analytics.message_lifecycle` і consumer `message-analytics` (topology v10)

- Таблиця (міграція `AddMessageLifecycle` у `AnalyticsDbContext`): PK `(raw_message_id, run_id)`; root-поля: `source_id, lane, published_at, received_at,
  has_text, text_length, has_payload, is_edit (source_revision <> '0'), stored_at`; аналіз: `analyzed_at, analysis_outcome (completed|no_facts|unsupported|
  needs_review|failed), method (rules|llm), fact_count, versions jsonb, timings jsonb {normalized_at, parsed_at, llm_completed_at, finalized_at},
  timings_available bool, error text`; домен: `domain_completed_at, expected_branches text[], incident_ids bigint[], track_ids bigint[], alert_ids bigint[]`;
  вартість: `llm_calls, llm_input_tokens, llm_output_tokens, llm_cost_usd, llm_latency_ms` (з `llm_requests` за raw+run при analysis.completed);
  `updated_at`, `source_of_truth text (event|backfill)`. Індекси: `(received_at)` BRIN, `(source_id, received_at)`, `(analysis_outcome)`, partial pending.
  Root-лічильники — `count(DISTINCT raw_message_id)`; результати — per `(raw, run)` (ADR-0005). `is_edit`/повторний `raw.stored{is_new:false}` не збільшує
  root count (PK).
- `MessageAnalyticsHandler` (`Puluj.Processing/Analytics/`, subscription `message-analytics`, усі lanes; replay lane пише рядок з run replay — без
  live-ефектів): `raw.stored` → upsert root; `message.analysis.completed` → outcome/method/fact_count/versions/timings/error + llm cost за
  `llm_request_ids`; `track.changed`/`alert.changed`/`incident.changed` → append id до масиву, `domain_completed_at = max(occurred_at)` коли всі
  `expected_branches` мають terminal receipt (перевірка через `processing.deliveries` по raw подіям) — ідемпотентно (`ON CONFLICT DO UPDATE`, масиви через
  `array_append` з guard). Receipt `completed`; невідомий raw (backfill ще не пройшов) → upsert root з `source_of_truth = event`. Таблиця відсутня (Analytics
  Worker ще не мігрував) → transient (requeue).
- Topology v10: `message-analytics.status = active` (queue policy required); `raw.stored`/`analysis.completed`/`*.changed` тепер очікують
  `message-analytics` (ADR-0002). Worker: роль `message-analytics` (у дефолтних ролях `messaging`). Тести Messaging: consumer стартує у fixture постійно
  (проєкція без side-effects), TRUNCATE `analytics.message_lifecycle`; асерти exact-лічильників deliveries оновлюються.

### D2. Backfill і reconciliation (`Puluj.Analytics/Lifecycle/`)

- `LifecycleBackfill.RunAsync(fromRawId, batch)` (Analytics Worker, після copy-аналізу; та `POST /api/admin/analytics/lifecycle/backfill`): з durable
  evidence — `raw_messages` (root), `processing.stage_results`/`extractions`/`observations` (outcome/method/fact_count/versions/timings за `started_at/
  finished_at` стадій), `processing.deliveries` ⋈ `messaging.events` (domain completion), `incident_observations`/`targets→track_targets`/`air_alerts`
  (id), `llm_requests` (cost); raw без stage_results (legacy до P05) → `analysis_outcome` з `processing_status` (Processed→`legacy`, Failed→`failed`,
  Skipped→`unsupported`), `timings_available = false` (**unavailable, не вигадуємо**), `run_id = legacy` (UUIDv5 `run:legacy`). Checkpoint у
  `analytics.state (lifecycle_backfill_cursor)`, resumable, ідемпотентний upsert (event-рядки не перезаписуються backfill'ом: `source_of_truth = event`
  зберігає поля, backfill лише доповнює NULL).
- `LifecycleReconciliation` (щоденний/за запитом): (1) counts — `raw_messages` у вікні vs `count(DISTINCT raw_message_id)`; (2) пізні результати —
  рядки з `analyzed_at IS NULL`, для яких є `processing.extractions` → дописати; `domain_completed_at IS NULL` при всіх terminal receipts → дописати;
  звіт `{window, raw, roots, missing, lateFilled, unavailableTimings}` у `analytics.state` + endpoint.

### D3. Звіти (`LifecycleReportService`, `GET /api/admin/analytics/lifecycle?hours=24|168|720`)

Секції з відомими знаменниками (`received` (roots), `analyzed`, `facts`, `deliveries` окремо): **Джерела** (пости/edits/no-text/payload per source,
довжина p50, затримка збору p50/p95 = received − published, live vs history, перерви = max gap); **Конвеєр** (funnel ingested → stored → analyzed → with facts →
domain_completed → visible (incidents active generation); p50/p95 переходів; stuck: analyzed NULL старше SLO — окремо `unavailable` для legacy); **Розбори**
(rules vs llm, outcomes, multi-fact, unlocated (facts без location у `observations.payload`), версії rules/model); **Якість** (review outcomes з
`incident_revisions` actor `operator`: merge/split/resolve/retract/confirm; rules-vs-llm розбіжність там, де є обидва; precision/recall — `unavailable`
до розміченої вибірки (P16)); **Вартість** (tokens/cost/latency p50/p95 per model, per source, retries, cache hit); **Результати** (event kinds, precision
розподіл incidents, targets/tracks/alerts/incidents, provenance coverage = incidents з canonical observation); **Історія змін** (per `pipeline_version`/
run outcomes; replay diff = incidents per generation у вікні vs active); **Схожість** — посилання на існуючий copy-звіт. UTC у сховищі, Kyiv у UI.

### D4. UI

Сторінка «Аналітика повідомлень» (`web/src/admin/MessageAnalyticsPanel.tsx`, `api/adminLifecycle.ts`): секції з таблицями/барами (`Bars`, `Stat`), no-text і
failed видимі окремо, `unavailable` словом; кнопка backfill/reconcile з actor/reason; навігація: «Аналітика повідомлень» — перший пункт групи «Аналітика»,
«Хто кого копіює» лишається (legacy analytics видаляється окремим етапом — план §10). Playwright A06.

### D5. Тести

| # | Сценарій | Доказ |
|---|---|---|
| L01 | Messaging.Tests: pipeline (ingress → … → incident-worker) → рядок lifecycle: root після `raw.stored`, outcome/method/fact_count/timings після analysis, incident id + `domain_completed_at` після `incident.changed`; повторний `raw.stored{is_new:false}` → той самий root (count 1); no-text/structured raw → рядок з `has_text=false`, outcome `unsupported|no_facts`; failed parse → `failed` + error видимі; replay run → окремий рядок (raw, run) без зміни live-рядка; llm cost з `llm_requests` | late results, no-text/failed |
| L02 | Analytics.Tests (Testcontainers PostGIS): backfill з evidence: raw з stage_results → timings; legacy raw без stage_results → `timings_available=false`, outcome з processing_status; event-рядок не перезаписується; повторний backfill ідемпотентний; reconciliation: roots == raw у вікні, late fill для analyzed NULL | backfill/reconciliation |
| L03 | Analytics.Tests: `LifecycleReportService` над синтетичними рядками — знаменники окремі, funnel монотонний, `unavailable` для legacy timings, cost per model, no-text у джерелах | звіти |
| L04 | Contracts: v10 — `message-analytics` active; expected set `raw.stored` = normalizer, message-analytics, archive | контракти |
| A06 | Playwright: сторінка з mock — секції, no-text/failed видимі, «unavailable» словом, backfill з actor/reason | UI |

### D6. Документація

ADR-0013-message-analytics (проєкція, знаменники, backfill/unavailable, reconciliation, cutover legacy copy-аналітики — видалення окремим етапом),
ADR-0005 (analytics_caught_up stage), README контрактів (v10), `docs/README.md`, `fork-deployment.md` (роль, міграція analytics, backfill процедура),
plan §16.1/§17, evidence `P15-analytics-evidence.md`, handoff, manifest.

## Порядок

1. Міграція + handler + topology v10 (L01, L04). 2. Backfill/reconciliation (L02). 3. Report + endpoints (L03). 4. UI + A06. 5. Docs, review, handoff,
commit (явний перелік), push.

## Ризики / межі

- Precision/recall — потребують розміченої вибірки (P16); показуємо «unavailable», не підміняємо кількістю фактів.
- Legacy analytics API/таблиці не видаляються (план §10: окремий етап після перевірки).
- Analytics.Worker мігрує схему `analytics`; consumer у `messaging` вимагає таблиці — порядок деплою документується (Worker analytics першим).

## Незалежне review плану — `p15_review` (approve after fixes)

| # | Finding | Рішення |
|---|---|---|
| B1 | `count(DISTINCT raw_message_id)` рахує редакції як roots (edit = новий raw id) | Колонки `source_message_key`, `source_revision`; знаменники окремо: `raw rows`, `posts = count(DISTINCT (source_id, source_message_key))`, `edits`; reconciliation звіряє обидва |
| B2 | «Таблиця відсутня → requeue» = quarantine після 5 спроб; pending analytics deliveries пінять watermark watchdog, блокують replay verify | DDL `analytics.message_lifecycle` **у міграціях `PulujDbContext`** (роль `migrate` завжди раніше за consumers; `AnalyticsDbContext` мапить таблицю з `ExcludeFromMigrations`) — deploy-ordering зникає; `DomainWatchdog.WatermarkAsync` виключає `message-analytics`; replay verify чекає analytics (консистентно з `analytics_caught_up`, UI показує, що саме блокує) |
| B3 | Domain completion не тригериться для `noop`-гілок/out-of-order; «щоденний» reconciliation | Reconciliation sweep у кожному циклі Analytics.Worker (`Analytics:Interval`, 1 хв) для рядків з `analyzed_at < now − 2 хв`: `domain_completed_at` з `processing.deliveries` (усі expected terminal), `analyzed_at` з `extractions`; «late» = дописано reconciliation'ом (`late_filled` у звіті); порожній `expected_branches` → `domain_completed_at = analyzed_at` |
| B4 | Постійний consumer + TRUNCATE = гонка; ~90 асертів | Fixture: consumer зупиняється **до** TRUNCATE і стартує після purge (`ResetAsync`); змінені асерти перелічені (Crash C01 + 4 `completed`-лічильники, Gate G01); `PurgeQueuesAsync` включає `message-analytics` |
| N1 | Ownership DDL vs raw SQL writer | DDL — Infrastructure (`MessageLifecycleConfiguration`), writer — handler у Processing, reader/backfill — Analytics через mapped entity; drift ловлять L01 (Messaging.Tests мігрує main context) і L02/L03 (Analytics.Tests мігрують обидва contexts) |
| N2 | `POST /analytics/reset` стирає весь `state` | Reset чистить лише copy-ключі; backfill/reconcile — `control_audit` з actor/reason |
| N3 | Out-of-order upsert в обидва боки | Root upsert не чіпає analysis-полів; analysis update не чіпає root; L01 сценарій «analysis раніше за raw.stored» |
| N4 | LLM cost за `llm_request_ids` неповний | Cost = усі рядки `llm_requests` за `(raw, run)` (retries/takeover/late), cache tokens окремо (`llm_cache_tokens`); legacy backfill — за raw |
| N5 | Події без `raw_message_id` | `noop no_raw` (expiry/admin), задокументовано в ADR-0013 |
| N6 | Backfill completion при відсутніх receipts | `completion_available = false` (як `timings_available`); stuck-лічильник виключає unavailable |
| N7 | Вартість звіту 720h | `statement_timeout`, snapshot-кеш (`Analytics:ReportCacheSeconds`), індекс `(received_at, source_id, analysis_outcome)`; відхилення від daily-агрегатів ADR-0006 зафіксовано в ADR-0013 (тригер переходу — латентність > 2 с на 720h) |
| N8 | Kyiv-час bucket | hour для 24h, Kyiv-day для 168/720 (`AT TIME ZONE 'Europe/Kyiv'`); L03 перевіряє межу доби |
| N9 | `visible`/replay diff | `visible` = join `incidents.generation_id = active` при запиті; рядок зберігає `generation_id` останньої зміни |
| N10 | Роль/compose/deploy.ps1 атомарно | `WorkerOptions.AllRoles/BrokerRoles`, compose, deploy.ps1, `AddPulujMessageAnalytics` — в одному коміті; fork-deployment: migrate → роль → backfill → reconcile → навігація |
| N11 | ADR-0003: `RawMessageReader` `:e{ts}` → `source_message_key/revision` | Відкладено (P16) — copy-аналітика не в scope P15; зафіксовано в ADR-0013 |
| N12 | EXPLAIN 720h | Evidence: `EXPLAIN` звітних запитів над синтетичними даними L03 (не production-shaped — задокументовано як межа) |
| N13 | Перелік файлів | `web/src/admin/*`, `web/src/api/adminLifecycle.ts`, `web/e2e/A06*`; commit явним списком |
| N14 | Status Analytics.Worker | `/api/admin/analytics/status` + backfill cursor / last reconcile / missing |
| Q1 | Пункти інших ADR, призначені P15 | Таблиця «Відкладено з P15» в ADR-0013 (P16/після даних); `unknown.unclassified` — у секції «Розбори», якщо є в outcomes |
| Q2 | Owner для similarity reuse / видалення legacy | P16 (legacy retirement); similarity — після даних |
| Q3 | Bindings `*.changed` для analytics | Лишаємо (near-real-time domain ids для «Історії змін»); noop-receipts на expiry/admin — прийнято, ADR-0013 |
| Q4 | Synthetic run/lane `legacy` | ADR-0013; UI показує «legacy» словом |
| Q5 | Replay verify vs analytics | Чекати (див. B2) |
| Q6 | Rules-vs-LLM розбіжність | «unavailable до shadow-порівняння» (P16), не порожній нуль |
