# P06 — LLM worker, аудит запитів і extraction finalizer

Task: [P06 / issue #8](https://github.com/sql-monk/Puluj-g/issues/8).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p06_review`): approve after fixes → B1–B6 виправлено,
N1–N8, N10–N13, Q1–Q2 враховано (N9 — наступною міграцією, Q3 — статус ADR лишено `proposed`) → повторний прогін зелений. Rollout не виконувався.
Owner: Claude Code (Opus 5), агент `p06`. Reviewer: план — `p06_review` (approve after fixes; B1 lease wait замість throw, B2 breaker → terminal,
B3 job-level MaxAttempts, B4 `final:false` не публікується — внесено до старту, `P06-plan.md`); результат — `p06_review` (таблиця нижче).
Base commit: `991b95d`; проміжний стан закомічено owner'ом як `a9c7ec4` (merge `dfb81f6`) під час review; правки за review, evidence, handoff і
manifest — наступний коміт (`P06-build-manifest.json` перелічує файли цього коміту).

## Результат для споживача

- **`llm-worker`** (`LlmWorkerHandler`, topology v5 `active`): `llm.requested` → виклик моделі через `ILlmCompletion` (`AnthropicCompletion` —
  SDK, structured output, мапінг помилок у `LlmCompletionException{Code, Retryable, StatusCode}`) поза транзакцією; `llm.completed{facts | no_facts |
  needs_review}` або **лише** `llm.failed{final:true}` (`attempts` ≥ 1). Порядок: перевірки (job `succeeded` → `noop`; `MaxAttempts`; `deadline_at`;
  версія/хеш нормалізації) → rate-limit permit і breaker **у межах deadline** → lease → виклик → audit → fencing-перевірка.
- **Lease/fencing (W8)**: job-рядки `processing.attempts` (`subscription_id = llm-worker:job`, `job_key = llm:{request_id}`, `event_id = request_id`,
  `fencing_token` 1..n, `lease_until`), takeover під `pg_advisory_xact_lock(hashtext(job_key))` після `lease_until` (старий рядок → `interrupted`),
  partial unique `(job_key, fencing_token)`. Живий чужий lease — очікування (bounded), не throw; holder, що встиг `succeeded`, — `noop`.
  Пізній результат: одразу після виклику job `superseded` + audit `late`; якщо новий holder ще `running` — `DeliveryDeferredException`
  (consumer: attempt `superseded`, requeue без inbox-запису), інакше `noop`. Та сама перевірка ще раз у result-tx під тим самим lock.
- **Audit**: кожен виклик — рядок `llm_requests` autocommit до result-tx (`request_id`, `run_id`, `fencing_token`, `attempt_id`, `provider_request_id`,
  usage/cost, тексти); `outcome` `answered → applied | late`, коди помилок, `invalid_response` (відповідь, яку mapper не приймає → terminal без
  повторного виклику). `answered` без `applied/late` = результат втрачено з crash'ем holder'а.
- **Terminal без виклику** (`deadline_exceeded`, `normalization_drift`, `attempts_exhausted`, `budget_unavailable` — лише коли пауза breaker'а
  виходить за deadline) теж бере власний job-рядок `failed`, тож подія несе актуальний токен і finalizer її не відкидає.
- **`finalizer`** (`FinalizerHandler`, `active`): state machine на (raw, run): `parse.completed{facts|no_facts|unsupported|failed}` → одразу;
  `needs_llm` → стадія `finalize` `awaiting_llm`; `llm.completed`/`llm.failed{final}` → terminal (`completed | no_facts | needs_review | failed`).
  Рівно один immutable `processing.extractions` (unique (raw, run), `ON CONFLICT DO NOTHING`), `processing.observations` лише для `completed`,
  `observations.recorded` лише для `completed` з фактами, `message.analysis.completed` для кожного outcome (`timings` — лише наявні значення,
  `llm_completed_at`; `versions` = normalization/rules зі стадії `awaiting_llm` + `model/prompt`). Terminal ніколи не понижується; пізній/повторний
  вхід → `noop`. `stored_at` — з архіву `messaging.events` (індекс raw/run), не скан outbox.
- **Shadow-режим**: worker і finalizer не пишуть `targets`/`air_alerts`/`processing_status` (F01 assert); legacy loop без змін до P09/P14/P16.
- Міграція `AddExtractions` (`processing.extractions`, `processing.observations`, колонки `llm_requests`, partial unique attempts); ролі `llm-worker`,
  `finalizer` (Compose `messaging`); `Llm:MaxAttempts` (3), `Llm:LeaseSeconds` (90), prefetch llm-worker 2; `DeliveryDeferredException` у Messaging.
- Контракти: topology v5 (`finalizer`, `llm-worker` → `active`), README «Runtime (P06)», `llm.completed.usage` за схемою (`cache_read_tokens`).

Docs: ADR-0004 («Реалізація (P06)», W8 → F04/F04b/F09/F10, open: quarantine safety net), ADR-0005 (finalizer, `analyzed`, fallback-семантика),
ADR-0006 (extractions/observations, llm_requests, job-рядки, індекс N9 → наступна міграція), ADR-0002 (v5), `docs/adr/README.md`, README контрактів,
`fork-deployment.md`, plan §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (Testcontainers 4.15.0).
Усі під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p06-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests --filter FinalizerTests` run1 → run2 → run3 | 1 → 1 → 0 | 26/4 → 5/3 → 8/0 (текст для LLM-гілки, `versions.model`, breaker reset) |
| `dotnet test tests/Puluj.Messaging.Tests` run4 (до review) | 0 | 61/0/1 |
| `dotnet test … --filter FinalizerTests` run5 → run6 (після review-правок) | 1 → 0 | 10/1 (F04b/F10, зависання `StopAsync`) → **11/0** |
| `dotnet test tests/Puluj.Messaging.Tests` run7 (фінальний) | 0 | **64/0/1** (skip — P04-C06 W1c) |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (двічі) | 0 | **60/0/0** |
| `dotnet test tests/Puluj.Processing.Tests` (двічі) | 0 | 105/0/0 |
| `dotnet test tests/Puluj.Integration.Tests` (двічі) | 0 | 32/0/1 |
| `dotnet test tests/Puluj.Api.Tests` / `Puluj.Admin.Tests` / `Puluj.Analytics.Tests` (до review; правки їх не зачіпають) | 0 | 30, 56, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |
| `docker compose … --profile broker config` | 0 | roles `relay,archive,raw-writer,normalizer,parser,llm-worker,finalizer` |

## Evidence

[`P06-finalizer-evidence.md`](P06-finalizer-evidence.md) / `messaging-crash-evidence.json` (P06-F01…F10): rules/no_facts/unsupported → analysis,
LLM-шлях з повним audit, W8 (імітований і реальний takeover з deferral), retry → final, дублі + 2 репліки finalizer, non-retryable/refusal,
порядок черг, W4-crash після audit, terminal без виклику за схемою.

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | Terminal `llm.failed` без job публікував token 1 → finalizer «late» → `awaiting_llm` назавжди | `TerminalWithJobAsync`: власний job-рядок `failed` (актуальний токен) для deadline/drift/exhausted/budget | F10 |
| B2 | `attempts` міг бути 0 (схема `minimum 1`) | attempts = job-рядки `failed` (≥ 1), `Math.Max(1, …)` | F10 (schema valid) |
| B3 | `timings` з `null` у полях `dateTime` | `TimingsJson`: лише наявні ключі; `stored_at` з архіву | F01–F08 схема |
| B4 | Мапінг відповіді до audit — виняток губив оплачений виклик | audit `answered` одразу; мапінг у try/catch → audit `invalid_response` + job `failed` + terminal | код; F03 |
| B5 | Superseded holder писав `noop` inbox для того самого `event_id` — результат нового holder'а губився | fencing одразу після виклику; holder `running` → `DeliveryDeferredException` (attempt `superseded`, requeue без inbox); в Apply — те саме під advisory lock | F04b |
| B6 | Takeover через `lease_until`/crash не тестувалися | F04b (2 репліки, реальний takeover, deferral), F09 (crash після audit, очікування lease) | run6/run7 |
| N1/Q1 | Permit і breaker після lease; breaker → terminal одразу | permit і очікування паузи breaker'а **до** lease, у межах deadline; після lease — re-check deadline | F07 (4xx → pause 2 с → refusal-тест) |
| N2 | `MaxTokenAsync` у result-tx без lock | `pg_advisory_xact_lock` перед перевіркою | код |
| N3 | `Fail()` + `Trip()` подвійний лічильник | `Trip` для статусних помилок, `Fail` для інших | код |
| N4 | Cancel/takeover лишали `running` | cancel → `interrupted` (best effort); takeover → старий `running` → `interrupted` | F04b, F09 |
| N5 | `usage` ключі ≠ схема | `cache_read_tokens`, без cache-creation у події | F03/F04b schema |
| N6 | `versions` LLM-шляху без normalization/rules; без `llm_completed_at` | merge зі стадії `awaiting_llm`; `llm_completed_at = occurred_at` | F03 |
| N7 | `stored_at` скан outbox без індексу | `messaging.events` за (raw, run) | код |
| N8 | `DateTimeOffset.UtcNow` | `clock` | код |
| N9 | надлишковий індекс attempts | залишено (міграція вже закомічена owner'ом); зафіксовано в ADR-0006 → наступна міграція | docs |
| N10 | `llm_requests.worker` varchar(64) vs довгий `Producer` | усічення до 64 у audit | код |
| N11 | RateLimiter без dispose | `IDisposable` | код |
| N12 | docs: §17 наперед, назви trx, коментар v5, F04-опис | виправлено | docs |
| N13 | `budget_unavailable` у переліку кодів провайдера | коментар уточнено | код |
| Q2 | observations для `needs_review` | рядки observations лише для `completed` | F07 |
| Q3 | статус ADR-0005 | повернуто `proposed` (реалізовано; accept — owner) | docs |

Reviewer підтвердив: shadow mode, ідемпотентність (inbox + job + unique extraction + guard), транзакції (lease коротка tx, провайдер поза tx, audit
autocommit, подія+receipt+job в одній tx, ACK після commit), міграція ↔ конфігурація, fixture reset, контракти v5/asyncapi/README.

## Відомі обмеження / невиконані перевірки

- Реальний `AnthropicCompletion` без інтеграційного тесту (ключ/мережа); mapping помилок — за кодом.
- Quarantine `llm.requested`/`llm.completed` (5 delivery attempts інфраструктурних помилок) без terminal `llm.failed` → finalizer `awaiting_llm`;
  safety net (reconciliation/admin) — P13/P16.
- Crash holder'а після відповіді: другий оплачений виклик після lease (F09); reuse `answered`-audit — можливе покращення.
- Rate limit/breaker — на репліку (N реплік × ліміт).
- Project card — токен без scope; статус у issue/§17.

## Rollout / rollback / input ownership

Default deploy без змін (ролі лише у сервісі `messaging` профілю `broker`; `Llm__Enabled=false` за замовчуванням → worker не викликає модель,
`llm.requested` завершуються `llm.failed{no_api_key}` лише якщо `Llm__Enabled=true` без ключа). Увімкнення — профіль `broker` + ключ.
Міграція `AddExtractions` — `Down` симетричний (дані extractions/observations втрачаються; legacy `targets` незалежні). Topology v5 зареєстрована —
повернення потребує bump, не даунгрейду. Ownership: `src/Puluj.Processing/Stages/{LlmWorkerHandler,FinalizerHandler}` (P06), `Llm/ILlmCompletion`,
`AnthropicCompletion` (P06), `DeliveryDeferredException` у Messaging (P06).

## Чекбокси issue #8

- [x] Lease/fencing, audit кожного виклику, budget (rate limit + breaker у межах deadline); terminal завжди з актуальним токеном
- [x] Canonical extraction (один на run), observations, `observations.recorded` + `message.analysis.completed` за схемами; shadow (без domain writes)
- [x] Crash/late-result/fencing тести: F04, F04b, F09, F10 + F05–F08; усі suites зелені на реальних PostGIS + RabbitMQ
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (v5), конфігурація (ролі, Compose, `Llm:*`), міграція, документація (ADR-0002/0004/0005/0006, README, fork-deployment, plan) оновлені

## Наступний task

**P08** (issue #9) — DB-driven rule versions/resolver, preview/shadow/corpus, нові kinds §8.3; далі **P09** (issue #10) — track/alert writers з
`observations.recorded`, locks/revisions, watchdog commands, SQL trigger ownership §7. P07 закрито раніше.
