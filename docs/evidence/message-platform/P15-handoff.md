# P15 — lifecycle projection, backfill/reconciliation, «Аналітика повідомлень» замість copy UI

Task: [P15 / issue #17](https://github.com/sql-monk/Puluj-g/issues/17).
Status: **done** — реалізація, тести (Messaging L01, Analytics L02–L04, контракти v10, Playwright A06), документація; незалежне review результату
(`p15_review`): request changes (B1, B2) + N1–N7, Q1–Q7 → виправлено/задокументовано → повторні прогони зелені. Rollout не виконувався (міграція
`AddMessageLifecycle` additive; topology v10 — `message-analytics` active; нова роль у дефолтних ролях `messaging`).
Owner: Claude Code (Opus 5), агент `p15`. Reviewer: план — `p15_review` (B1–B4, N1–N14, Q1–Q6 — внесено, таблиця в `P15-plan.md`); результат —
`p15_review` (таблиця в `P15-analytics-evidence.md`).
Base commit: `37402e1` (P14); результуючий commit — цей handoff комітиться разом із кодом (`P15-build-manifest.json`; паралельна робота public UI /
public catalogue (`web/` public, `Puluj.Api`, `Puluj.Contracts/Dtos.cs`, `Puluj.Admin/wwwroot`) до коміту не входить).

## Результат для споживача

- **Проєкція `analytics.message_lifecycle`** ([ADR-0013](../../adr/ADR-0013-message-analytics.md)): один рядок на `(raw_message_id, run_id)` — root
  (пост/редакція, has_text/payload, `stored_at`), розбір (outcome/method/fact_count/unlocated/versions/timings/error), домен (`expected_branches`,
  `branches_done`, `domain_completed_at`, incident/track/alert ids, `generation_id`), вартість з `llm_requests` (calls/tokens/cache/cost/latency),
  `timings_available`/`completion_available`, `source_of_truth (event|backfill|reconciliation)`. DDL — міграція `AddMessageLifecycle` у `PulujDbContext`
  (schema `analytics`, grants `puluj_admin`/`puluj_reader`); `AnalyticsDbContext` мапить її `ExcludeFromMigrations`.
- **Consumer `message-analytics`** (topology **v10**, active; `MessageAnalyticsHandler`, роль `message-analytics` у дефолтних ролях `messaging`,
  compose/`deploy.ps1`): `raw.stored` → upsert root (ніколи не чіпає analysis-поля); `message.analysis.completed` → analysis + cost + expected set (lane-фільтр
  за topology); `*.changed` → `branches_done`, completion при `expected ⊆ done`; порядок подій довільний (розбір до root — ок); події без
  `raw_message_id` → `noop no_raw`; `DomainWatchdog` watermark цю підписку ігнорує.
- **Backfill** (`LifecycleBackfill`, Analytics.Worker + `POST /api/admin/analytics/lifecycle/backfill?reset=`): за raw id з курсором, батчами, з
  extractions/observations/stage_results/`messaging.events ⋈ deliveries`/incident_observations/track_targets/air_alerts/llm_requests; **legacy** raw (без
  extraction, без events, старший за 10 хв) → run `legacy` з `stored_at NULL`, `analyzed_at = processed_at`, `timings_available=false`,
  `completion_available=false` — unknown як unknown; event-рядки не перезаписуються; advisory lock на прохід.
- **Reconciliation** (`LifecycleReconciliation`, щопроходу Analytics.Worker; `POST …/reconcile?hours=`): пізні розбори, пізні completion за receipts
  (`completed|noop|waived`, порожній expected → закривається), лічильники raw/пости/редакції vs проєкція (за grace), відсутні roots — за id.
- **Звіт** `GET /api/admin/analytics/lifecycle?hours=24|168|720` + `GET /status`: funnel з окремими знаменниками, timeline (година / Kyiv-доба),
  джерела, розбори, якість (precision/recall — `unavailable`), вартість (усі lanes), результати, історія per run, звірка. Панель «Аналітика повідомлень»
  (`MessageAnalyticsPanel`, перша в групі «Аналітика»; «Хто кого копіює» — допоміжний розділ); кнопки backfill/reconcile з actor/reason (audit).

Docs: ADR-0013 (новий, accepted), ADR-0005 (stage row), `docs/adr/README.md`, `contracts/messaging/README.md` (Runtime P15, v10), `docs/README.md`
(endpoints), `fork-deployment.md` (P15 секція), plan §16.1/§17, `deploy/docker-compose.yml` + `scripts/deploy.ps1`.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management`, Node 22 / Playwright 1.58.2 / Chromium 145.
.NET — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p15-*.trx`. Повна таблиця — [`P15-analytics-evidence.md`](P15-analytics-evidence.md).

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests` run1 → run2 (після review) | 1 → 0 | 84/11/1 (точні лічильники Crash/Gate під v10) → **95/0/1** |
| `dotnet test tests/Puluj.Messaging.Tests --filter CrashTests\|GateTests\|LifecycleTests` run2 | 0 | **16/0** |
| `dotnet test tests/Puluj.Analytics.Tests --filter LifecycleTests` run5 (L02–L04) | 0 | **3/0** |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v10) run2 | 0 | **61/0** |
| `dotnet test tests/Puluj.Integration.Tests` run2 | 1 | 43/1/1 — `PipelineTests` через `PublicCatalogQueriesTests` паралельної задачі (`f6bb518`); наодинці зелений; поза P15 |
| `dotnet test` Processing / Api / Analytics / Admin run2 | 0 | **134 / 39 / 36 / 92** — 0 failed |
| `cd web; npx playwright test` (усі) → `A06` | 0; 0 | **21/0**; 1/0 |
| `cd web; npx vitest run src/admin`; `tsc` app + e2e | 0 | 44/0; — |
| `dotnet build Puluj.sln` | 0 | 0 warnings |

## Review результату → виправлення → повторна перевірка

Таблиця B1/B2/N1–N7/Q1–Q7 — у [`P15-analytics-evidence.md`](P15-analytics-evidence.md). Ключове: порожній expected set закривається (NULL LEFT JOIN — не
pending), backfill ставить completion для no-facts рядків при archived events; legacy — лише без сліду платформи і старший за grace, missing roots — за id,
не переписуванням вікна; failures LLM — позитивний список; оператор — не системний актор; київська опівніч у тесті — з `TimeZoneInfo`; L01 (розбір до root)
і L04 (receipts/noop/вартість) додано.

## Відомі обмеження / невиконані перевірки

- Backfill над production-shaped обсягом і `EXPLAIN` 720h-звіту — не міряно (синтетичні дані малі); daily-агрегати — коли звіт > 2 с.
- Precision/recall, rules-vs-LLM — `unavailable` за дизайном (P16); legacy copy-аналітика не видаляється (P16); `:e{ts}` reader — P16.
- Late completions — над синтетичними events/receipts (L04), advisory lock backfill'у — без тесту на конкуренцію; HTTP-рівень endpoints — Playwright над mock.
- Integration-набір червоний через тест паралельної задачі (`PublicCatalogQueriesTests`, `f6bb518`), не через P15.
- Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy: `migrate` (таблиця + індекси + grants) → `messaging` (роль `message-analytics`; expected set v10 включає її — pending до старту видно у
«Чергах», watchdog не чекає) → Analytics.Worker (backfill по курсору, reconciliation) → панель → перевірити звірку. Rollback: `Down` дропає таблицю; підписку
можна поставити на паузу lane'ами (P13). Ownership: `Puluj.Processing/Analytics/MessageAnalyticsHandler.cs`, `Puluj.Analytics/Lifecycle/*`,
`Puluj.Domain/Entities/Analytics/MessageLifecycle.cs`, `MessageLifecycleConfiguration.cs` + міграція, `AnalyticsEndpoints.cs` (lifecycle),
`web/src/admin/MessageAnalyticsPanel.tsx`, `web/src/api/adminLifecycle.ts`, `web/e2e/A06-lifecycle.e2e.ts`, `contracts/messaging/topology.json` (v10).

## Чекбокси issue #17

- [x] Counts reconciliation (L02/L04: raw/пости/редакції vs проєкція, missing → refill); late results (L01 late failed, analysis-first; L04 receipts/noop);
      no-text/failed видимі (L01/L03/A06); старі невідомі timings — `unavailable` (L02/L03/A06)
- [x] Тести на актуальній збірці (PostGIS + RabbitMQ Testcontainers, Playwright); результати й пропуски зафіксовані
- [x] Незалежне code review — request changes → B1/B2 і N1–N7 виправлено → повторна перевірка зелена
- [x] Контракти (topology v10, Lifecycle DTOs), міграція/rollback, конфігурація (`Analytics:Lifecycle*`, роль `message-analytics`), документація
      (ADR-0013, README, fork-deployment, plan) оновлені
