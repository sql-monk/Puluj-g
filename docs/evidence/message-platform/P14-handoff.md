# P14 — run/generation orchestration, isolated lanes, replay checkpoints, delta catchup/promote/rollback

Task: [P14 / issue #14](https://github.com/sql-monk/Puluj-g/issues/14).
Status: **done** — реалізація, тести (Messaging R01–R06, контракти v9, Playwright A05), документація; незалежне review результату (`p14_review`):
request changes (B1, B2) + N1–N9, Q1–Q8 → виправлено/задокументовано → повторні прогони зелені. Rollout не виконувався (міграція `AddReplayRuns`
additive; topology v9; нова роль `replay` у дефолтних ролях `messaging` — простоює без replay run'а).
Owner: Claude Code (Opus 5), агент `p14`. Reviewer: план — `p14_review` (approve after fixes; B1–B3, N1–N13, Q1–Q6 — внесено, таблиця в
`P14-plan.md`); результат — `p14_review` (таблиця в `P14-replay-evidence.md`).
Base commit: `6e183a5` (P13); результуючий commit — цей handoff комітиться разом із кодом (`P14-build-manifest.json`; паралельна робота public UI /
public catalogue (`f6bb518`, `web/`, `Puluj.Api`, `Puluj.Contracts/Dtos.cs`) до коміту не входить).

## Результат для споживача

- **Replay run як job** (`RunService`, [ADR-0005 «Orchestration»](../../adr/ADR-0005-runs-completion.md)): scope (джерела, вікно), власна неактивна
  generation, checkpoint `{published, total, lastRawMessageId, ingestCeiling, done, error, failures}` (keyset за PK — порядок ingestion), один відкритий
  replay run; state machine `created → running ↔ paused → verified → promoted → rolled_back`, `cancelled` (з created/running/paused/verified/failed),
  `failed → running`; кожен перехід — CAS + `control_audit run:*`.
- **Publisher** (`ReplayPublisher`, роль `replay`): `raw.stored{lane replay, is_new:false, processing_run_id}` через outbox в одній tx з checkpoint
  (resumable, повтор → stage `noop`), `FOR UPDATE SKIP LOCKED`, backpressure `Replay:MaxInFlight`, `PrefetchByLane {replay: 2}`, `failed` після
  `MaxBatchFailures` поспіль.
- **Shadow без production effects**: topology v9 (`incident-worker` + replay lane; track/alert/projection — ні); `IncidentWriterHandler.ApplyShadowAsync`
  пише лише incidents/links/revisions у generation run'а (без `targets`, NOTIFY, LLM); `(raw, generation)` ідемпотентність; `noop run_cancelled`.
- **Delta catchup** (до promote): ingestion-дельта — `ingestCeiling`/`to` → watermark; пізній raw зі старим `published_at` теж потрапляє.
- **Verify / promote / rollback**: гейти (pending/quarantined/unconfirmed з моменту run'а, `unanalyzed`), звіт (`incidents_in_generation`,
  `active_incidents_in_window`, `active_incidents_outside_scope`, `active_incidents_missing_in_generation`); promote — одна tx під exclusive advisory lock
  `generation:active` (live writers тримають shared → запис ніколи не потрапляє в щойно деактивовану generation), 409 при partial scope без `force`;
  rollback — попередня generation (або live), audit `incidentsWrittenSincePromote`. Нічого не видаляється.
- **Supersede live run** за `pipeline_version` (лише run, старший за старт процесу; новіший — приймається).
- **Admin**: `GET/POST /api/admin/ops/runs…` (400/404/409), панель «Replay» (`ReplayPanel`): runs, створення, команди з actor/reason, підтвердження
  називає generation і лічильники verify, явний `force`; `/api/admin/incidents` і `/review` — лише active generation (`generation=all`).

Docs: ADR-0005 (accepted; «Orchestration (P14)», межі/відкладення), ADR-0009/0010/0011/0012 рядки, `docs/adr/README.md`, `contracts/messaging/README.md`
(Runtime P14, v9), `docs/README.md` (endpoints), `fork-deployment.md` (P14 секція: міграція, ролі, опції, порядок деплою, процедура), plan §16.1/§17,
`deploy/docker-compose.yml` + `scripts/deploy.ps1` (роль `replay` у дефолті).

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management`, Node 22 / Playwright 1.58.2 / Chromium 145.
.NET — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p14-*.trx`. Повна таблиця — [`P14-replay-evidence.md`](P14-replay-evidence.md).

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests` run1 → run2 (після review) | 0 → 0 | 94/0/1 → **94/0/1** |
| `dotnet test tests/Puluj.Messaging.Tests --filter ReplayTests\|TopologyRegistryTests` run6 | 0 | **16/0** |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v9) | 0 | **61/0** |
| `dotnet test tests/Puluj.Integration.Tests` | 1 | 42/1/1 — падіння `PipelineTests` через закомічений паралельною задачею `PublicCatalogQueriesTests` (`f6bb518`), що лишає `target_tracks`; тест наодинці 1/0; поза P14 |
| `dotnet test` Processing / Api / Analytics / Admin (run1 → run2) | 0 | 134, 35, 33, 92 → **92/0** |
| `cd web; npx playwright test` (усі) → `A05` run3 | 0; 0 | **20/0**; 1/0 |
| `cd web; npx vitest run src/admin`; `tsc` app + e2e | 0 | 44/0; — |
| `dotnet build Puluj.sln` | 0 | 0 warnings |

## Review результату → виправлення → повторна перевірка

Таблиця B1/B2/N1–N9/Q1–Q8 — у [`P14-replay-evidence.md`](P14-replay-evidence.md). Ключове: роль `replay` у `AllRoles` (інакше `messaging` не стартує),
catchup лише до promote (post-promote shadow-запис не досяг би карти), cancel з verified/failed, verify у вікні run'а, явний `force`, локальний час у UI,
NOTIFY-лічильник після тиші.

## Відомі обмеження / невиконані перевірки

- Tracks/alerts без `generation_id`: replay їх не будує й не ізолює (P16); partial merge/replacement scope — після даних (promote всієї generation з
  `force` — відхилення від §11.5, зафіксовано); history run `completed`, legacy reset cutover, projection checkpoint — P15/P16.
- «Live не зупиняється» — послідовно, не під одночасним навантаженням; publisher failure path (`failed`) без тесту; кілька реплік publisher'а — одна у fixture;
  HTTP-рівень `RunEndpoints` — Playwright над mock.
- Integration-набір червоний через тест паралельної задачі (див. вище) — не через P14.
- Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy: міграція `AddReplayRuns` (2 індекси), topology v9 (реєструється поруч із v8; v8 incident-worker не читає replay-чергу — verify червоний,
без хибних ефектів), роль `replay` у `messaging`; rollback — `Down` (drop індексів), прибрати `replay` з ролей; відкриті replay runs — cancel. Ownership:
`Puluj.Infrastructure/Processing/RunService.cs`, `Puluj.Messaging/ReplayPublisher.cs`, `ProcessingRuns` supersede, `IncidentStateWriter.EnsureGenerationAsync`,
`IncidentWriterHandler.ApplyShadowAsync`, `RunEndpoints`, `web/src/admin/ReplayPanel.tsx`, `web/src/api/adminRuns.ts`, `web/e2e/A05-replay.e2e.ts`.

## Чекбокси issue #14

- [x] Live не зупиняється (R01/R06), shadow без production effects (R01: targets/NOTIFY/LLM/projection/track/alert незмінні), atomic switch (R01/R06) і відрепетируваний rollback (R01)
- [x] Тести на актуальній збірці (PostGIS + RabbitMQ Testcontainers, Playwright); результати й пропуски зафіксовані
- [x] Незалежне code review — request changes → B1/B2 і N1–N9 виправлено → повторна перевірка зелена
- [x] Контракти (topology v9, `producer_roles.replay`, RunDto), міграція/rollback, конфігурація (`Replay:*`, `PrefetchByLane`, роль `replay`), документація (ADR-0005, README, fork-deployment, plan) оновлені

## Наступний task

P15 (issue #17) — аналітика життєвого циклу повідомлень замість copy-аналітики (§10).
