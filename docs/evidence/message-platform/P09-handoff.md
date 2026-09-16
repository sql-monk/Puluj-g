# P09 — track/alert writers, locks/revisions, watchdog-команди, SQL trigger ownership

Task: [P09 / issue #10](https://github.com/sql-monk/Puluj-g/issues/10).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p09_review`): approve after fixes → B1 (change-detection
тривог), B2 (NOTIFY `TargetCreated`), N1–N11, Q1–Q3 виправлено/задокументовано → повторний прогін зелений. Rollout не виконувався (default deploy
без змін — ролі writers вимкнені).
Owner: Claude Code (Opus 5), агент `p09`. Reviewer: план — `p09_review` (approve after fixes; B1 archive для routability, B2 expected set за
manifest, B3 outbox unique для команд, B4 ownership incident/info, B5 backlog-aware watermark, B6 replay lane, B7 серіалізація guard'ів — внесено до
старту, `P09-plan.md`); результат — `p09_review` (таблиця нижче).
Base commit: `4849172` (P08); результуючий commit — цей handoff комітиться разом із кодом (`P09-build-manifest.json` перелічує файли).

## Результат для споживача

- **Два владники агрегатів** (ADR-0009): `track-worker` — fact writer усіх не-alert observations (`targets`, один рядок на `observation_id`) і владник
  треків; `alert-worker` — fact writer alert-фактів і владник інтервалів `air_alerts`. Обидва — subscriptions `observations.recorded` (lanes live/history)
  і своїх expiry-команд; parity з legacy by construction (переюз `CorrelationSink`/`TextAlertSink`/`AlertsInUaHandler` через `ConsumerDbContext` над
  консюмерським tx, `TargetMaterializer` з `attributes` факту) — W06: 0 розбіжностей на впорядкованому input.
- **Lock hierarchy** (§7): `Store` shared → `track` shared|exclusive → `track:cat:{id}` (sorted); alerts `alert:region:{id}` (sorted). Кандидати
  читаються під lock → candidate-create race закрита (W02: бар'єр на 2 репліки → 1 трек). Дедлоки `40P01/40001` → requeue без лічильника.
- **Revisions і події**: `target_tracks.revision`/`air_alerts.revision`, `last_event_id`/`last_correlation_id`; одна `track.changed`/`alert.changed`
  на агрегат на delivery (`created | updated | cancelled | expired`; `started | ended | updated | expired`, change за pre/post-image), `category` = код
  таксономії, `occurred_at` = effective time. Routable через `archive` (v6) до появи `projection`; NOTIFY (`TargetCreated`, `TrackUpserted/Closed`,
  `AlertChanged`) з writers після commit — стрічка мапи працює після cutover.
- **Watchdog як команди** (`DomainWatchdog`, роль `watchdog`): `*.expiry.requested{expected_revision, expire_at, watermark}`, UUIDv5 за (aggregate,
  revision, хвилина) + memo після commit; owner: `stale_revision`/`not_active`/`still_fresh` → noop; watermark за unconfirmed outbox і message-scoped
  deliveries без receipt (history backlog не закриває треки).
- **Cutover без подвійного writer**: worker забороняє `processing` + писачів в одному процесі; writers `noop legacy_owned`/`already_written`; legacy
  `writers_owned` (під Store); процедура і rollback — ADR-0009/`fork-deployment.md`; Compose за замовчуванням без нових ролей.
- **Expected set за manifest** (`OutboxWriter` + `TopologyRegistry.ManifestSubscriptions`): deliveries writers видимі completion/reconciliation;
  finalizer маршрутизує incident/info на track-worker до P10.
- Міграція `AddAggregateRevisions` (`targets.observation_id` + partial unique CONCURRENTLY з попереднім DROP, revisions/last_event_id); метрики
  `puluj.writer.stage/outcomes`; `processing.observations.legacy_target_id` заповнюється.

Docs: ADR-0009 (новий), ADR-0002 (v6, by_manifest), ADR-0004 («Реалізація (P09)»), ADR-0006, `docs/adr/README.md` (+0008, +0009), README контрактів
(«Runtime (P09)»), `fork-deployment.md` (cutover), Compose коментар, plan §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (Testcontainers 4.15.0).
Усі під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p09-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests --filter DomainWriterTests` run1 → run2 → run3 (після review) | 1 → 0 → 0 | 5/3 → 8/0 → **8/0** |
| `dotnet test tests/Puluj.Messaging.Tests` (до і після review-правок) | 0 | **74/0/1** (skip — P04-C06 W1c) |
| `dotnet test tests/Puluj.Processing.Tests` (+2 materializer) | 0 | **122/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` (двічі) | 0 | 37/0/1 |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v6) | 0 | 61/0/0 |
| `dotnet test tests/Puluj.Admin.Tests` / `Api` / `Analytics` | 0 | 62, 30, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |
| `docker compose … --profile broker config` | 0 | ролі `messaging` без writers (cutover — вручну) |

## Evidence

[`P09-writers-evidence.md`](P09-writers-evidence.md), `messaging-crash-evidence.json` (P09-W01…W08, benchmark W08).

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | Зміна рівня structured-тривоги / тихе закриття текстової без події та revision | change-detection за pre/post-image інтервалів у tx (не за оголошеннями sinks); W04 + повторний start з новим рівнем → `updated`, revision 2 | W04 |
| B2 | Після cutover зникав NOTIFY `TargetCreated` (стрічка мапи) | обидва writers емітують `TargetCreated` для вставлених рядків у `AfterCommit`; ADR/fork-deployment | код |
| N1 | incident/info не в expected set | finalizer: усі не-alert гілки → `track-worker` до P10 | W06 (вибухи) |
| N2 | Дедлок writers ↔ legacy/reset через FK на raw | коментар і ADR-0009 §7: «захищено» = коректність; `40P01` → requeue | docs |
| N3 | W02 доводив duplicate-шлях; бар'єр без перевірки | 5 хв між спостереженнями (candidate-шлях), `Assert(hooks.Met == 2)` | W02 |
| N4 | Memo watchdog'а отруювався при невдалому sweep | memo після commit | код |
| N5 | Watermark не бачив `ingress.received` | фільтр за message-scoped `event_type` | W05 |
| N6 | `category` як id | код таксономії (`TaxonomyIndex.CategoryCode`), fixture `UAV` | W03 |
| N7 | `closed` не емітується; `created` для legacy-треку | `closed` reserved (docs); `created` = id > max до доставки | W01/W02 |
| N8 | INVALID індекс після перерваного CONCURRENTLY | `DROP INDEX CONCURRENTLY IF EXISTS` перед створенням; fork-deployment | Integration Down/Up |
| N9 | Guard `writers_owned` після structured handler | `WritersOwnAsync` одразу після Store в обох гілках | W07 |
| N10 | `target.cancelled` не покритий | W03: «загроза минула» → треки регіону `cancelled{target_cancelled}` | W03 |
| N11 | Дрібниці (summary, `ExpireAt`, docs метрик, `TargetCreated` metric) | виправлено | build |
| Q1–Q3 | Snapshot на окремому з'єднанні; детекція за kind code; `occurred_at` | pre/post-image у tx; cancellation за legacy `EventType`; `occurred_at` = effective time | код/docs |
| Q4, Q5 | Змішані structured+text факти; різні конфіги owner/watchdog | практично неможливо / `still_fresh` + memo 6 год — задокументовано як межа | docs |

Reviewer підтвердив: lock hierarchy детермінована, кандидати під lock, ідемпотентність (unique + guards + inbox), revisions/envelope-правила, тригери
спрацьовують як у legacy, cutover-guard'и, watchdog UUIDv5/lane/run, fixture reset, docs узгоджені.

## Відомі обмеження / невиконані перевірки

- Replay lane writers — P14; incident owner — P10; projection/NOTIFY-міст і stats/links поза fact path — P11/P15.
- Порядок обробки — частина семантики кореляції (напрямок дубліката, склад треку): відтворюваність = впорядкований input (§7).
- Benchmark — Testcontainers/ноутбук, 2×60 спостережень; без production-shaped даних; категорійна межа не дала виграшу через рядок
  `source_daily_stats (source, day)`.
- «Вічна» expected delivery пінить watermark до reconciliation/waive (P13); `still_fresh` + memo 6 год при різних конфігах owner/watchdog.
- NOTIFY після commit і cap дедлоків без окремих тестів. Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy без змін поведінки (міграція additive, ролі writers вимкнені, legacy loop — writer). Cutover — ADR-0009: зупинити `processing`,
увімкнути `track-worker,alert-worker,watchdog`; rollback — зворотно (guard'и в обох напрямках). Ownership: `Puluj.Processing/Writers/*` (P09),
`CorrelationSink.HandleTargetFactsAsync/CloseTracksForCancellationAsync` (P09 entry points), `ConsumerDbContext`, `OutboxWriter` by_manifest, topology v6.

## Чекбокси issue #10

- [x] Candidate-create race (W02), cross-region/time-window (W03), alert start/end/cancel (W04), expiry race (W05), hot-row benchmark (W08)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (v6, by_manifest), міграція/rollback, конфігурація (ролі, Compose, cutover), документація (ADR-0009, 0002/0004/0006, README, fork-deployment, plan) оновлені

## Наступний task

P10 (issue #12) — incident owner (schema, worker, merge/reject policy, revisions/evidence, admin command contracts §8.4–8.5) — не входить у поточне
доручення (P06–P09). Далі за планом також P11 (read-side/projection) і P13 (explorer).
