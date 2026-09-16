# P15 — lifecycle projection, backfill/reconciliation, «Аналітика повідомлень»: evidence

Task: [P15 / issue #17](https://github.com/sql-monk/Puluj-g/issues/17). Base `37402e1`. План і рішення review: [`P15-plan.md`](P15-plan.md); ADR:
[ADR-0013](../../adr/ADR-0013-message-analytics.md). Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, Testcontainers `postgis/postgis:17-3.5`
(+ `rabbitmq:4.3-management` у Messaging.Tests), Node 22 / Playwright 1.58.2 / Chromium 145. TRX — `test-results/p15-*.trx`; JSON —
`messaging-crash-evidence.json` (`P15-L01`). Незалежне review результату (`p15_review`, read-only): **request changes** (B1, B2) + N1–N7, Q1–Q7 →
виправлено (таблиця нижче) → повторні прогони зелені.

## Що доведено (gate issue #17)

| Gate | Доказ |
|---|---|
| Counts reconciliation (відомі знаменники) | L02: `raw 5 / posts 4 / edits 1` з `raw_messages` vs проєкція `5 / 4`, `missing 0`; видалений рядок → `missing 1` і дописано з evidence → знову 0. L03: funnel `raw 5, posts 4` (редакція — не root), джерело `raw 5, posts 4, edits 1, noText 1`; частки у UI названі «% з N» (A06) |
| Late results (події + reconciliation, не скан) | L01: `message.analysis.completed{failed}` опублікований пізніше (новий event id) потрапляє в той самий рядок (outcome `failed`, error повністю); розбір, що випередив `raw.stored` (raw d), відкриває рядок (`stored_at NULL`, `analyzed_at`), root потім лише дописує `stored_at`; L02: рядок без analysis при наявному extraction дописано reconciliation'ом (`late_analyses 1`, `source_of_truth reconciliation`); L04: pending receipt → `late_completions 0 / pending_domain 1`; receipt `noop` пізніше → `late_completions 1`, `domain_completed_at = completed_at` receipt'а, `source_of_truth reconciliation`; порожній expected set без completion → закрито без receipts (`analyzed_at`); рядок без archived events лишається unknown |
| No-text / failed видимі | L01: structured raw без тексту → рядок `has_text=false` з outcome (`unsupported|no_facts`), `fact_count 0`, `domain_completed_at` (нічого не очікувалось); failed — outcome `failed` + error; L03: `outcomes {completed, unsupported, legacy, pending}`; A06: «ПОМИЛКА 18», «без тексту» у джерелах, «ще без розбору» |
| Старі невідомі timings — unavailable | L02: legacy raw (без extraction, без events, старший за `LegacyGrace`) → run `legacy`, lane `legacy`, `stored_at NULL`, `analyzed_at = processed_at`, `timings_available=false`, `timings NULL`, `completion_available=false`; L03: `UnavailableTimings 3`, `UnavailableCompletion 5` (без archived events completion невідомий, не «не завершено»); L04: stage-рядок без events → `completion_available=false`, reconciliation його не «закриває»; UI: «timings unavailable для N повідомлень», «unavailable: N з M», `pipeline unavailable` для legacy run |
| Root рахується один раз | L01: повторний `raw.stored` (новий event id) → той самий рядок; replay run → окремий рядок `(raw, run)`, live-рядок незмінний; `count(DISTINCT raw)` = 4 (a, b, c, d) |
| Вартість з `llm_requests` (не з події) | L04: два рядки `llm_requests` (200 + 429) → `llm_calls 2`, input 1200 / cache 800 / output 150, `cost 0.0123`, latency 1700; `noop` гілка — terminal (`domain_completed_at` при `completed + noop`) |
| Shadow/replay без live-ефектів у проєкції | L01: replay-рядок `expected_branches = [incident-worker]` (track-worker не обслуговує replay lane), `track_ids` порожній; report scope виключає lane `replay` |

## Прогони (команда → exit → результат)

| # | Команда | Exit | Passed / Failed / Skipped |
|---|---|---|---|
| 1 | `dotnet test Messaging.Tests --filter LifecycleTests\|TopologyRegistryTests` run1 (Analytics-міграція окремим контекстом) | 1 | build error (`GetDbConnection` using) + unit: `message-analytics` тепер очікується («Retired/planned» assert) |
| 2 | run2 | 1 | 12/1 — L01: `reason = 'root'` receipts = 3 (по одному на raw), assert виправлено |
| 3 | run3 | 1 | 0/1 — L01 replay row: `expected_branches` містив `track-worker` → handler фільтрує гілки за lane (як receipts) |
| 4 | run4 `--filter LifecycleTests` | 0 | **1/0** (10 с) |
| 5 | `dotnet test Analytics.Tests --filter LifecycleTests` run1 | 1 | 0/2 — очікування `UnavailableCompletion`: без archived events completion невідомий (4/5, не 2) — тести уточнено |
| 6 | run2 | 0 | **2/0** (15 с; L02, L03) |
| 7 | `cd web; npx playwright test A06 --project desktop` | 0 | **1/0** |
| 8 | `cd web; npx playwright test` (усі) | 0 | **21/0** (desktop 17 + mobile 4; 1,4 хв) |
| 9 | `cd web; npx vitest run src/admin`; `tsc` app + e2e | 0 | **44/0**; — |
| 10 | `dotnet build Puluj.sln` | 0 | 0 warnings |
| 11 | `dotnet test Messaging.Tests` (повний, consumer `message-analytics` увімкнено у fixture) run1 | 1 | 84/11/1 — Crash/Gate: точні лічильники `inbox`/`attempts`/`quarantine`/overdue тепер включають `message-analytics` (topology v10) → фільтри `subscription_id` у C01/C08/C09/C10/G01/G02/G03/G05 |
| 12 | Integration / Contracts / Processing / Api / Analytics / Admin run1 | 1 / 0 / 0 / 0 / 0 / 0 | 42/1/1 (`PipelineTests` через `PublicCatalogQueriesTests` паралельної задачі, `f6bb518`; наодинці зелений) / 61 / 134 / 35 / 35 / 92 |
| 13 | після review (B1/B2/N1–N7/Q2/Q5/Q6): `dotnet test Analytics.Tests --filter LifecycleTests` run3 → run4 → run5 | 1 → 1 → 0 | L03 `Analyzed 3≠4`: helper не ставив `processed_at` для skipped (старий цикл ставить) → виправлено; L04 `t0` мікросекунди vs timestamptz → цілі секунди; **3/0** (L02, L03, L04; 32 с) |
| 14 | `dotnet test Messaging.Tests --filter CrashTests\|GateTests\|LifecycleTests` run1 → run2 | 1 → 0 | 12/4 (ще 4 точні лічильники без фільтра: G01 `succeeded`, C01/C09 `inbox completed`, C10 `attempts`) → **16/0** (49 с; L01 з analysis-first секцією) |
| R1 | `dotnet test tests/Puluj.Messaging.Tests` (повний) run2 | 0 | **95/0/1** (6 хв 9 с; `p15-messaging-run2.trx`) |
| R2 | `dotnet test tests/Puluj.Integration.Tests` run2 | 1 | 43/1/1 — `PipelineTests.Raw_messages_become_targets_tracks_and_revisions` через `PublicCatalogQueriesTests` паралельної задачі (`f6bb518`, лишає `target_tracks`); наодинці зелений; поза P15 |
| R3 | `dotnet test tests/Puluj.Messaging.Contracts.Tests` run2 | 0 | **61/0** |
| R4 | `dotnet test tests/Puluj.Processing.Tests` run2 | 0 | **134/0** |
| R5 | `dotnet test tests/Puluj.Api.Tests` run2 | 0 | **39/0** |
| R6 | `dotnet test tests/Puluj.Analytics.Tests` run2 | 0 | **36/0** (L02–L04 включно) |
| R7 | `dotnet test tests/Puluj.Admin.Tests` run2 | 0 | **92/0** |
| R8 | `cd web; npx playwright test --project=desktop e2e/A06-lifecycle.e2e.ts`; `npx playwright test` (усі) | 0; 0 | **1/0** (9,5 с); **21/0** (desktop 17 + mobile 4; 2,3 хв) |
| R9 | `dotnet build Puluj.sln` (після review) | 0 | 0 warnings |

## L01 (P15-L01, Messaging.Tests, реальний pipeline)

`l01-a` «Шахеди на Харківщині. Вибухи у Харкові.» → root (`stored_at`), analysis `completed/rules/2 facts`, `timings_available` з `parsed_at`,
`expected = done = [incident-worker, track-worker]`, `domain_completed_at`, 1 incident id, 1 track id, `source_of_truth event`; receipts
`message-analytics`: 3 `root`, ≥ 2 `domain`. `l01-b` (payload без тексту) → `has_text=false`, outcome є, 0 фактів, completed (порожні expected).
`l01-c` → `no_facts`; пізній synthetic `analysis.completed{failed, error boom}` → `failed` з error. Повторний `raw.stored` → без нового рядка. `l01-d`
(raw вставлено SQL, без події): synthetic `analysis.completed` **до** `raw.stored` → рядок `stored_at NULL / analyzed_at / no_facts`; synthetic `raw.stored`
потім → `stored_at` заповнено, analysis-поля збережено, рядок один. Replay run (scope = raw a) → 2-й рядок (raw, replay run): `completed`,
`expected [incident-worker]`, incident є, tracks немає; live-рядок незмінний; roots = 4.

## L02/L03 (Analytics.Tests, Testcontainers PostGIS, синтетичні evidence)

L02: 5 raw (пост + редакція зі stage rows, legacy processed, structured skipped, event-рядок) → backfill 5 рядків: stage-рядки з timings/unlocated/versions,
legacy → `legacy` run/lane/outcome, `timings_available=false`; event-рядок збережено (`llm`, 3 факти, cost) попри extraction `rules`; повторний повний прохід —
без змін. Reconciliation: raw/posts/edits 5/4/1 vs 5/4, missing 0, late analysis 1 (`reconciliation`), unavailable timings 2 / completion 4; видалення рядка →
missing 1 → дописано → 0. L03: звіт 168 год (`day`, Kyiv-доба): raw 5 / posts 4 / analyzed 4 / withFacts 2 / unavailable 3 / 5 / stuckAnalysis 1 (редакція
без розбору), джерело (raw 5, posts 4, edits 1, noText 1, facts 3, collect delay p50 30 с), outcomes `{completed 2, unsupported 1, legacy 1, pending 1}`,
multi-fact 1, unlocated 1, rules `v3` 2, precision/recall `unavailable…`, cost 0; timeline: два raw по різні боки київської опівночі → різні доби;
24 год → `hour`; 100 → 400; history: run з `pipeline 1.0-test` (completed 2), legacy run kind `legacy`.

L04 (після review): p1 — `llm` розбір з двома `llm_requests` (200: 1200/800/150 токенів, $0.0123, 1400 мс; 429: 300 мс) і `observations.recorded` з receipts
`incident-worker completed` + `track-worker noop` → `llm_calls 2`, сума токенів/вартості/latency, `expected [incident-worker, track-worker]`,
`domain_completed_at` (noop — terminal); p2 — receipt `incident-worker` pending → `domain_completed_at NULL`, `completion_available true`; p3 — `no_facts`
+ archived `raw.stored` → `expected {}`, `domain_completed_at = analyzed_at`; p4 — stage rows без events → `completion_available false`. Reconciliation:
`late_completions 0 / pending_domain 1 / unavailable_completion 1` → receipt стає `noop` → `late_completions 1`, `domain_completed_at = completed_at`,
`source_of_truth reconciliation`; p3 із занульованим completion → закрито без receipts; p4 лишається unknown; повторний прохід → 0.

## Review результату (`p15_review`) → виправлення

| # | Sev | Знахідка | Що зроблено |
|---|---|---|---|
| B1 | blocking | Reconciliation ніколи не закривала рядки з порожнім `expected_branches` (NULL LEFT JOIN рахувався як pending); backfill не ставив `domain_completed_at` для no-facts рядків | `HAVING count(*) FILTER (WHERE d.event_id IS NOT NULL AND d.outcome IS NULL) = 0 AND count(d.event_id) >= cardinality(expected)`; backfill: `COALESCE(receipts, CASE WHEN немає observations.recorded AND є events run'а THEN x.created_at END)`; L04 |
| B2 | blocking | Backfill робив `legacy` з in-flight platform raw (без grace, без перевірки `messaging.events`); reconciliation `missing` без `< cutoff` і refill усього 48h-вікна однією tx щохвилини | Legacy = без extraction **і** без `messaging.events` **і** `received_at < now() − LegacyGrace (10 хв)`; `missing` лише `< cutoff`; refill за id (`BackfillIdsAsync`, ≤ 2000) — L02 (`missing 1 → 0`), L03 (edit без розбору → лишається подіям: `Analyzed 4`) |
| N1 | should | Коментарі handler'а про «власні міграції analytics» і «requeued, never quarantined» | Коментарі виправлено: DDL у `PulujDbContext`, 42P01 → `InvalidOperationException` → quarantine після ліміту (правильний алярм для деплою без `migrate`) |
| N2 | should | Два `HasIndex(ReceivedAt)` злилися в один BRIN partial | `ix_message_lifecycle_received_brin` (BRIN) + `ix_message_lifecycle_pending` btree partial `(received_at, run_id)`; міграція `AddMessageLifecycle` перегенерована (grants збережено) |
| N3 | should | `failures = outcome NOT IN (…)` рахував успішні `applied/answered` як failures | Позитивний список: `429, api_error, error, invalid_response, timeout` |
| N4 | should | Оператор = `actor NOT LIKE '%@%'` (email оператора випадав) | `NOT SIMILAR TO '(%-worker\|watchdog\|replay\|projection\|finalizer\|parser\|normalizer\|system)@%'`; правило в ADR-0013 |
| N5 | should | L03: київська опівніч як UTC−3 (flake після 25.10.2026) | `TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")` + `GetUtcOffset` |
| N6 | should | Два `<summary>` над `AddPulujMessageAnalytics`, `AddPulujProjection` без doc | Повернуто на місця |
| N7 | should | Немає тестів: розбір до `raw.stored`, обчислена вартість з `llm_requests`, late completions/noop | L01 (raw d), L04 |
| Q1 | q | `domain_completed_at` при випередженні domain-подій = `occurred_at` розбору; гілка done з першої `*.changed` | Задокументовано в ADR-0013 (analyzed→domain p50 ≈ 0 для таких рядків; `last_domain_event_at` не зберігається) |
| Q2 | q | Legacy-рядки з вигаданими `stored_at = received_at`, `analyzed_at = coalesce(processed_at, received_at)` | `stored_at NULL`, `analyzed_at = processed_at`; stage-рядок: `stored_at` = `raw.stored.occurred_at` з `messaging.events`, fallback `min(stage started_at)`; ADR-0013 §4 |
| Q3 | q | Expected set із файлових lanes, DB-override (paused/retired) не враховано | Задокументовано як edge case (P16) |
| Q4 | q | Вартість за `llm_requests.occurred_at` (усі lanes) vs funnel без replay | Задокументовано в ADR-0013 §6 (інший знаменник) |
| Q5 | q | `SourceOfTruth` без константи `reconciliation` | `MessageLifecycle.SourceReconciliation`, використано в reconciliation |
| Q6 | q | Паралельний backfill (worker loop + POST) на одному курсорі | `pg_try_advisory_lock(hashtext('lifecycle_backfill'))` у `RunAsync`: другий прохід пропускається (`Processed 0`, лог) |
| Q7 | q | Повний Messaging-набір без trx | `p15-messaging-run1/run2.trx` + решта наборів (R1–R9) |

## A06 (Playwright, mock)

Funnel: `1 200` raw, «постів 1 100», «% з 1 150 розібраних», «unavailable: 250 з»; transitions «timings unavailable для 250 повідомлень»; джерело без тексту
500; outcomes «ПОМИЛКА 18», «ще без розбору»; quality «unavailable: потрібна розмічена вибірка»; статус звірки «лічильники збігаються», «raw 2 300 (пости 2 100,
редакції 200) vs проєкція 2 300», «15 дописано», backfill «наздогнав»; history `unavailable` (legacy pipeline) і `legacy 240`; кнопки disabled без
actor/reason; reconcile → POST `/lifecycle/reconcile?hours=48 {actor, reason}`; посилання на copy-аналітику.

## Не покрито / межі

- Backfill над production-shaped обсягом (корельовані підзапити per raw у батчі 2000) — не міряно; `EXPLAIN` 720h-звіту — не знято (синтетичні дані малі).
- Late completions через receipts — L04 над синтетичними `messaging.events`/`processing.deliveries`, не над живим consumer'ом (L01 доводить event-шлях).
- Advisory lock backfill'у (Q6) — без тесту на конкуренцію (два процеси); поведінка «другий пропускається» перевірена лише кодом.
- Quality: precision/recall і rules-vs-LLM — `unavailable` за дизайном (P16).
- HTTP-рівень нових endpoints — Playwright над mock; Admin.Tests без WebApplicationFactory (P16).
- Legacy copy-analytics не видаляється (план §10: окремий етап, P16).
- Project card — токен без scope.
