# P09 — track/alert writers, locks/revisions, watchdog-команди: evidence

Джерела: `Puluj.Messaging.Tests/Integration/DomainWriterTests.cs` (W01–W08; Testcontainers PostgreSQL 17 + PostGIS, RabbitMQ 4 quorum queues; підсумки —
[`messaging-crash-evidence.json`](messaging-crash-evidence.json) `P09-W01…W08`), `Puluj.Processing.Tests/Writers/TargetMaterializerTests.cs`,
`Puluj.Messaging.Contracts.Tests` (topology v6). TRX — `test-results/p09-*.trx`.

## Gate issue #10

| Gate | Тест | Підсумок |
|---|---|---|
| Ідемпотентний fact writer + revision + подія | W01: `targets{observation_id}` 1, `target_tracks{revision 1, last_event_id}`, `observations.legacy_target_id` заповнено, `track.changed{created}` валідний за envelope/schema, `aggregate_id = track:1`, `aggregate_revision 1`; delivery `track-worker` completed (expected за `by_manifest`), `alert-worker` `noop`; `archive` отримав `track.changed` (routable); повторна доставка тієї самої події → `Duplicates` (inbox fast path), 1 трек | ✅ |
| **Candidate-create race** | W02: 2 репліки track-worker (prefetch 1), спільний `Barrier(2)` у `BeforeCommit` (обидва пройшли Prepare і одночасно входять у tx; тест перевіряє, що бар'єр справді зустрів обох); два спостереження одного об'єкта з різних джерел **через 5 хв** (поза вікном дублікатів, у вікні кандидатів) → **1 трек, 2 targets** (candidate-шлях, не duplicate), `track.changed` `created, updated`, revisions 1, 2 | ✅ |
| **Cross-region / time-window** | W03: інший регіон → окремий трек; те саме місце через 240 хв (вікно 120) → новий трек; 3 `created`, `category = UAV` (код таксономії); `target.cancelled` без категорії («загроза минула», exclusive track scope) → обидва треки Сумщини `cancelled{target_cancelled}`, Харківщина не зачеплена | ✅ |
| **Alert start/end/cancel** | W04 (впорядковано): текстова тривога → `air_alerts` + `alert.changed{started}`; текстовий відбій → `ended` (revision 2) **і** трек регіону `cancelled{reason alert_cancelled}` (revision 2); структуровані `end` перед `start` (history-порядок) → один інтервал зі start/end raw, `started` → `updated`; повторний structured start з новим рівнем (review B1) → `updated`, revision 2, `scope.level red`; 7 fact rows | ✅ |
| **Expiry race** | W05: команда з `expected_revision 0` при revision 1 → `noop stale_revision`; дубль команди → inbox duplicate; backlog (unconfirmed `ingress.received` 48 год тому) → watermark за ним, `SweepAsync` → 0 команд; без backlog → 1 команда (валідна за схемою, `expected_revision 1`), повторний sweep → 0 (memo); через relay → трек `Closed/timeout`, revision 2, `track.changed{expired}` | ✅ |
| Parity з legacy | W06: 7 повідомлень (треки, вибухи, тривога/відбій) через legacy `RawMessageProcessor` і через writers **по одному** (впорядкований input, §7): канонічні `targets` (усі колонки + `parser_metadata`), `target_tracks`/links, `air_alerts` — **0 розбіжностей** | ✅ |
| Подвійний writer | W07: legacy пише, поки writer тримається у `BeforeCommit` → writer `noop legacy_owned`, 1 legacy row, 1 трек; writers перші → legacy `ProcessAsync` = 0, raw `Processed`, рядки не змінені | ✅ |
| **Hot-row benchmark** | W08: 60 спостережень × 2 варіанти через 2 репліки: одна категорія — 63.9 ms/спостереження (writers tail 770 ms), дві категорії — 70.7 ms (tail 1000 ms); розподіл 30/30; дедлоків 0. Висновок: ціна на цих обсягах — pipeline (normalize/parse/finalize), а не lock; категорійна межа не дала виграшу, бо всі повідомлення одного джерела ділять рядок `source_daily_stats (source, day)` (тригер) — реальна межа до P11/P15 | ✅ виміряно |
| Reverse parity матеріалізації | `TargetMaterializerTests`: fact → `Target` → fact (без ParsedFact) канонічно рівні для 7 фактів корпусу; координати з точністю контракту (6 знаків) | ✅ |
| Topology v6 | Contracts 61/0: `track-worker`/`alert-worker` active (lanes live/history), `archive` required для `track.changed`/`alert.changed`; unit: `ExpectedSubscriptions` з `expected_branches` | ✅ |

## Запуски

| Файл | Результат | Примітка |
|---|---|---|
| `p09-writers-run1.trx` | 5 / 3 failed | W04/W06: порядок доставки платформи ≠ порядок id legacy (кореляція/дублікати order-dependent) → `RunOrderedAsync`; W05: watermark враховував `raw_messages.Pending` (raw-writer лишає Pending назавжди після cutover) → прибрано з watermark |
| `p09-writers-run2.trx` | **8 / 0** | після правок |
| `p09-writers-run3.trx` | 6/2 → 7/1 → **8 / 0** | після review-правок: W04 change-detection через pre/post-image (structured `Added` губився після першого `SaveChanges`); W03 `target.cancelled` — обидва треки регіону (очікування) |
| `p09-Puluj.Messaging.Tests.trx` → `-run2` | 74/0/1 → **74 / 0 / 1** | повний проєкт до/після review-правок (skip — P04-C06 W1c) |
| `p09-Puluj.Processing.Tests.trx` | **122 / 0** | +2 materializer |
| `p09-Puluj.Integration.Tests.trx` → `-run2` | 37 / 0 / 1 | legacy pipeline без змін поведінки (guard `writers_owned` неактивний без `observation_id`) |
| `p09-Puluj.Messaging.Contracts.Tests.trx` | 61 / 0 | v6 |
| Api / Admin / Analytics | 30, 62, 33 | регресія |

## Що не покрито (свідомо)

- Replay lane для writers (v6 lanes `live, history`) — run-ізоляція агрегатів P14.
- Cap/ROLLBACK шляхів дедлоків не відтворено (0 дедлоків у W08); `40P01` обробляється кодом consumer'а.
- Benchmark — на Testcontainers/ноутбуці, без production-shaped даних і без `pg_stat` метрик pool wait; метрики `puluj.writer.*` не знімалися з OTel.
- NOTIFY після commit (`AfterCommit`) не перевіряється тестом (PgNotify listener поза fixture).
- Structured-alert parity з legacy (`AlertsInUaHandler` переюзано) — у W04, не у W06 (N10: різні `parser_metadata`).
