# P13 — ops metrics/health, message explorer, scale/pause/drain/retry/DLQ UI

Task: [P13 / issue #16](https://github.com/sql-monk/Puluj-g/issues/16).
Status: **done** — реалізація, тести (Messaging O01–O04, Admin O05, Integration O07, Playwright A03/A04), документація; незалежне review результату
(`p13_review`): approve after fixes (B1, N1–N10, Q1–Q9) → виправлено/задокументовано → повторні прогони зелені. Rollout не виконувався (міграція
`AddMessagingControls` additive; нові admin endpoints; консюмери за замовчуванням поводяться як раніше — усі lanes `active`).
Owner: Claude Code (Opus 5), агент `p13`. Reviewer: план — `p13_review` (approve after fixes; B1–B4, N1–N16, Q1–Q5 — внесено до старту, таблиця в
`P13-plan.md`); результат — `p13_review` (таблиця в `P13-ops-evidence.md`).
Base commit: `3b35e3b` (P12); результуючий commit — цей handoff комітиться разом із кодом (`P13-build-manifest.json`; паралельна робота public UI у `web/`
до коміту не входить).

## Результат для споживача

- **Snapshot шини з БД** (`GET /api/admin/ops/messaging`, [ADR-0012](../../adr/ADR-0012-ops-controls.md)): підписка × lane — pending / in-flight / retry /
  quarantined / найстаріша очікувана / event-time lag / wait і processing p50-p95-p99 / завершено за годину / живі консюмери; roots (повідомлення, не jobs);
  broker (зі статусів воркерів + опційно management API, `source: db|management`); outbox (unconfirmed, вік, retries, unroutable, confirm-латентність);
  inbox; останній звіт reconciliation; воркери з `Consumers[]` та `stale`/`stuck`; backfill read-only; **alarms** з чистих правил (`AlarmRules`, `Ops:Slo`):
  `backlog_growing`, `oldest_age_slo`, `required_consumer_missing`, `dlq`, `inflight_stuck`, `stale_heartbeat_with_jobs`, `llm_paused`,
  `broker_disconnected`, `broker_blocked`, `outbox_stuck`, `outbox_unroutable`, `reconciliation_mismatch`, `roots_need_attention`, `lane_paused` (info).
- **Runtime-контролі з точним scope**: `messaging.subscription_lanes` + `messaging.control_audit`; `POST /ops/messaging/lanes/{sub}/{lane}`
  (pause/resume/drain — консюмер `basic.cancel`/`basic.consume` per lane, drain → `paused` сам), `POST /quarantine/{id}/retry|waive`, `POST /scale`;
  actor + reason обовʼязкові, кожна команда — рядок аудиту; `WorkerStatusDto.Consumers[]`/`Broker`, `Runtime:Reconciliation:Report`.
- **Message explorer** (`GET /api/admin/messages`, `/{rawId}/lifecycle`): одна картка — події → deliveries з квитанціями і attempts (помилки повністю) →
  extractions/observations → похідні → quarantine → `waiting/completed/failed` + `completion`.
- **Admin UI**: панелі «Черги» (`QueuesPanel`) і «Повідомлення» (`MessagesPanel`); severity/стани словом, дії disabled без actor/reason, підтвердження
  називає lane і підписку; текст — лише текст, href — `http(s)`.

Docs: ADR-0012 (+таблиця «Відкладено з §9»), `docs/adr/README.md`, `docs/README.md` (endpoints), `contracts/messaging/README.md` (Runtime P13),
`fork-deployment.md` (міграція/rollback, `Ops:Slo`, `ManagementUrl`, `ControlPoll`, `ScalableServices`, rolling deploy), plan §16.1/§17,
`deploy/docker-compose.yml` (`RABBITMQ_MANAGEMENT_URL` opt-in для admin).

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management`, Node 22 / Playwright 1.58.2 / Chromium 145.
.NET — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p13-*.trx`. Повна таблиця — [`P13-ops-evidence.md`](P13-ops-evidence.md).

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests` run1 → run2 (після review) | 0 → 0 | 90/0/1 → **90/0/1** |
| `dotnet test tests/Puluj.Messaging.Tests --filter OpsControlTests` run3–run5 | 0 | **4/0** ×3 |
| `dotnet test tests/Puluj.Admin.Tests` run1 → run2 | 0 | 91/0 → **92/0** (+11) |
| `dotnet test tests/Puluj.Integration.Tests` (повний; O07 окремо run2) | 0; 0 | **42/0/1**; 1/0 |
| `dotnet test` Contracts / Processing / Api / Analytics | 0 | 61, 134, 34, 33 |
| `cd web; npx playwright test` (усі) → `A03 --project desktop` run4 | 0; 0 | **19/0**; 2/0 |
| `cd web; npx vitest run src/admin`; `tsc` app + e2e | 0 | 44/0; — |
| `dotnet build Puluj.sln` | 0 | 0 warnings |

## Review результату → виправлення → повторна перевірка

Таблиця B1/N1–N10/Q1–Q9 — у [`P13-ops-evidence.md`](P13-ops-evidence.md) («Review результату»). Ключове: waive карантинної доставки тепер закриває
receipt (`waived`), `inflight_stuck` — лише за віком running attempt, індекси attempts, `Docker:ScalableServices` — єдиний allow-list, ліміти actor/reason
→ 400, guard закритого каналу lane'а.

## Відомі обмеження / невиконані перевірки

- Management API брокера і `docker compose --scale` не запускались у тестах (потребують стек); HTTP-рівень endpoints (401/400/409) — Playwright над mock,
  Admin.Tests без WebApplicationFactory (P16).
- Drain на кількох репліках: кожна знає свій in-flight; prefetched-but-undispatched у буфері клієнта не рахуються (ADR-0012 п.4).
- Deliveries до міграції без `lane` — pending у першому lane підписки; event-time lag недоступний.
- Відкладено з §9 (ADR-0012 таблиця): DB pool wait, readiness, LLM budget, quorum, `since` alarms — P16; backfill/replay-контролі — P14; silent source — P14/P15.
- Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy: міграція `AddMessagingControls` (2 таблиці, 2 nullable колонки, 5 індексів), нові admin endpoints, `WorkerStatusDto` additive; rollback —
`Down` (drop), старий worker з новою БД працює, новий worker зі старою БД — warning + усі lanes active. Ownership: `Puluj.Infrastructure/Messaging/Ops/*`,
`SubscriptionAdmin.SetLaneStateAsync`/`AuditAsync`, `SubscriptionConsumer` control loop, `MessagingOpsEndpoints`, `web/src/admin/{QueuesPanel,MessagesPanel}`,
`web/src/api/adminOps.ts`, `web/e2e/A03-queues.e2e.ts`.

## Чекбокси issue #16

- [x] Видимий offline/stuck worker (O03, A03), коректні counters/scope (O01–O03), RBAC/audit (actor/reason + `control_audit`), одна картка lifecycle (O04, A04)
- [x] Тести на актуальній збірці (PostGIS + RabbitMQ Testcontainers, Playwright); результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (додаткові DTO, endpoints), міграція/rollback, конфігурація (`Ops:Slo`, `ManagementUrl`, `ControlPoll`), документація (ADR-0012, README, plan) оновлені

## Наступний task

P14 (issue #14) — версіонований replay, isolated lanes, promote і rollback.
