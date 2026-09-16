# P10 — incident worker, evidence, консервативне злиття, ревізії, admin-команди: evidence

Джерела: `Puluj.Messaging.Tests/Integration/IncidentWriterTests.cs` (I01–I09; Testcontainers PostgreSQL 17 + PostGIS, RabbitMQ 4 quorum queues; підсумки —
[`messaging-crash-evidence.json`](messaging-crash-evidence.json) `P10-I01…I09`), `Puluj.Processing.Tests/Incidents/IncidentPolicyTests.cs` (12, pure policy),
`Puluj.Admin.Tests/IncidentEndpointsTests.cs` (Guard: 404/409/400, outbox off), `Puluj.Messaging.Contracts.Tests` (topology v7, fixture `incident.changed`).
TRX — `test-results/p10-*.trx`. Kinds поза v1-правилами (`fire.reported`, `impact.confirmed`, `damage.reported`, `infrastructure.outage`) подаються синтетичними
`observations.recorded` через `OutboxWriter` (raw-рядок вставляється напряму — інакше міст `IngestAsync` публікує `raw.stored/history` і pipeline парсить текст
удруге), так само як їх публікує finalizer; `impact.explosion.reported` у I01/I09 — реальним текстом через увесь pipeline.

## Gate issue #12

| Gate | Тест | Підсумок |
|---|---|---|
| Provenance: факт → рядок → incident → ревізія → подія | I01: «Вибухи у Харкові» → `targets{observation_id}` 1 (пише incident-worker, track-worker `noop`), `incidents` 1 (`reported`, revision 1, `generation_id` = live, location `city`), link `canonical` з `legacy_target_id`, `observations.legacy_target_id` заповнено, `incident_revisions` 1 (`created`, actor `incident-worker@instance`) зі snapshot, `processing.generations` 1 active (live), `incident.changed{created}` валідний за envelope + schema (`aggregate_id incident:1`, `event_kind_code`, `location.kind city`), `archive` отримав подію (routable); повторна доставка → inbox duplicate, 1 incident | ✅ |
| **Concurrent create/dedup** | I02: 2 репліки incident-worker на спільному `Barrier(2)` у `BeforeCommit` (обидва пройшли Prepare, входять у tx одночасно — тест перевіряє, що бар'єр зустрів обох), два джерела про вибухи в Харкові за 3 хв → **1 incident**, links `canonical` + `supports`, `source_count 2`, state `reported` (без auto-confirm), `incident.changed` `created, updated`, revisions 1, 2 — kind lock серіалізує кандидатів | ✅ |
| **Conservative merge** | I03: інша область (Суми) → окремий; те саме місто через 200 хв (вікно 120) → окремий; звіт по області (третє джерело) через 10 хв після міського → **containment** (`GapTo 0`) → `supports`, `source_count 2`, точна локація (місто, `accuracy_km < 30`) збережена, не розширена; **ambiguity детерміновано**: два Харківські incidents за 121 хв (поза вікном один від одного), третє джерело на +60 → score 0.7 vs 0.695 (margin 0.1) → окремий incident 3 `relation ambiguous`, `decision_reason.ambiguous = [1, 2]`, `considered 2` | ✅ |
| **Echo не підвищує state** | I04: 3 пости одного джерела → 2 links `echo`, `source_count 1`, revision 3, `reported`; `impact.confirmed` з того самого джерела → далі `reported` (echo); з іншого джерела → `confirms` → **`confirmed`**, `source_count 2`, остання `incident.changed{state confirmed}`, усі події валідні | ✅ |
| **Out-of-order closure** | I05: admin `resolve` (effective T+30) → `resolved` revision 2; факт з `effective_at T+20` (до закриття) → приєднано (`source_count 2`) **без reopen**; факт `T+90` → новий incident; текстовий відбій тривоги/«загроза минула» (через увесь pipeline, alert-worker/track-worker) → incidents не змінено (incident 2 revision 1, incident 1 `resolved` revision 3 = created/resolved/late supports) | ✅ |
| **Replay idempotency** | I06: та сама `observations.recorded` з новим `event_id` → `noop already_written` (рядки і links є, без revision); інший набір observations того самого raw → `noop already_written`; `incidents` 1 (revision 1), links 1, `targets` 1 | ✅ |
| **As-of history** | I07: 2 факти + admin `confirm` → revisions 1..3 (`created, updated, updated`), snapshot revision 2 = `reported`, `source_count 2`, 2 observations; revision 3 = `confirmed`, actor `ops`; `incident.changed` revisions 1..3, state події revision 2 == snapshot; `recorded_at` — годинник, `effective_at` — evidence | ✅ |
| **Admin commands, immutable evidence** | I08: merge 1→2 (той самий kind): `merged` + `updated`, source `retracted{merged, merged_into 2}`, 3 links у target, перенесений — `moved{merged_from 1}`, `source_count 2`; повторний merge retracted → `IncidentConflictException` (409); split → `split` + `updated`, новий incident 3 з `canonical{split_from 2}`; retract → resolve retracted → 409; suppress/unsuppress → `suppressed`/`updated{suppressed false}`; невідомий id → `IncidentNotFoundException` (404); без actor/reason → `ArgumentException` (400); **md5 `targets` і `processing.observations` до/після — рівні**; усі ревізії `ops` мають reason; **факт після merge** («знову пошкодження у Миколаєві», третє джерело): merged source 1 і retracted split 3 — не кандидати, Херсон 2 — considered 1, за slack → новий incident 4 `canonical`, links 1/3 незмінні; перенесений link несе `merged_from 1`, `original_relation canonical`, `split_from 2`; `incident.changed` `merged{merged_into 2}`, `split`, `suppressed`, `retracted` — валідні | ✅ |
| **Змішаний raw (B2 плану)** | I09: «Шахеди на Харківщині. Вибухи у Харкові.» → одна `observations.recorded` з фактами `target` + `incident`, `expected_branches` = обидва writers; `targets{observation_id}` 2, `observations.legacy_target_id` 2, incident 1 (`canonical` з `legacy_target_id`), трек 1 (`track_targets` 1), deliveries track-worker/incident-worker `completed`, `track.changed` 1 + `incident.changed` 1 — guard `already_written` бачить повний набір з обох боків | ✅ |
| Pure policy | `IncidentPolicyTests`: нові/canonical, supports у вікні, симетричне вікно (±120 exclusive), threshold (0.45 < 0.55), інша область не кандидат / сусід → `near_candidates`, containment (gap 0), без geometry — лише той самий place, echo/supports/confirms + `NextState`, crossover kinds не зливається (і зворотно), resolved — лише late evidence, retracted — ніколи, incident без canonical source не підтверджується своїм каналом (B4), ambiguity margin, `KindPolicy.From` seed/дефолти | ✅ 12/12 |
| Admin Guard | `IncidentEndpointsTests`: `IncidentNotFoundException` → 404, `IncidentConflictException` → 409, `ArgumentException` → 400; outbox off → 409 **до** виконання команди | ✅ 2/2 |
| Topology v7 | Contracts 61/0: `incident-worker` active (lanes live/history), `archive` required для `incident.changed`, bindings; unit `ExpectedSubscriptions` з `expected_branches` incident → `incident-worker` | ✅ |

## Запуски

| Файл | Результат | Примітка |
|---|---|---|
| `p10-incidents-run1.trx` | 1 / 7 failed | тестовий helper шукав міста через `FindAdmin` (індекс лише район/громада/область) → пошук за `Candidates` + `Place.Name` |
| `p10-incidents-run2.trx` | 4 / 4 failed | I03 — звіт по області з того ж джерела, що міський (echo, а не supports) → третє джерело; I05 — closure порівнювалась із `updated_at` (годинник), а не з `effective_at` ревізії → `ClosuresAsync` за `incident_revisions`; I07 — link додавався у `Observations` двічі (EF fixup + явний Add) → 3 observations у snapshot; I08 — після `Remove` fixup спорожнив `source.Observations` до перенесення → links читаються до Remove |
| `p10-incidents-run3.trx` | 7 / 1 failed | I05: очікуване число extractions рахувалось після publish (pipeline встигав) → лічильник до publish |
| `p10-incidents-run4.trx` → `-run5` | 8 / 0 → **8 / 0** | run4 — 29 с; у повному прогоні I05 падав (лічильник extractions): синтетичні raw через `IngestAsync` міст публікував `raw.stored/history`, і pipeline парсив їх удруге → raw-рядки вставляються напряму (без outbox); діагностика waits у повідомленні assert |
| `p10-policy-run1.trx` | 11/1 → **12 / 0** | арифметика вікна в тесті (−100 хв → score 0.5 < threshold) → −30 хв |
| `p10-Puluj.Messaging.Contracts.Tests.trx` | 61 / 0 | v7 |
| `p10-Puluj.Processing.Tests.trx` → `-run2` | 133/1 → **134 / 0** | `EventKindCatalogTests`: `policyVersion` 2 + `dedupPolicy` лише в incident kinds |
| `p10-incidents-run6.trx` → `-run7` | 8/1 → **9 / 0** | після review: I08 — hash evidence брався до нового факту i08-d → блок після hash-перевірки; +I09 |
| `p10-Puluj.Messaging.Tests.trx` → `-run2` → `-run3` → `-run4` | 81/1/1 → 81/1/1 → 82/0/1 → **83 / 0 / 1** | повний проєкт (I05 у suite — див. вище; run4 — після review-правок, +I09; W06 parity тепер з incident-worker; skip — P04-C06 W1c) |
| `p10-Puluj.Integration.Tests.trx` → `-run3` → `-run4` | 36/1/1 → 37/0/1 → **37 / 0 / 1** | `P07EventKindTests`: `eventKindPolicyVersion` 2; legacy pipeline без змін |
| `p10-Puluj.Processing.Tests-run3.trx`, `p10-Puluj.Admin.Tests-run3.trx`, `p10-Puluj.Messaging.Contracts.Tests-run4.trx` | 134 / 0, 64 / 0, 61 / 0 | після review-правок (policy retracted/B4 unit, Guard, fixture `incident.changed`) |
| Api / Analytics | 30, 33 | регресія |

## Що не покрито (свідомо)

- Replay lane для incident-worker (v7 lanes `live, history`) і promote generation — P14; live generation — стала `UUIDv5(generation:live)`.
- Admin endpoints перевірені через `IncidentStateWriter` (той самий код, що й у Admin) і unit-тестом `Guard` (mapping, outbox off), не HTTP-рівнем із БД; `AdminIndexes.EnsureFreshAsync` перед командою (B2) — без окремого тесту.
- NOTIFY `TargetCreated` після commit (`AfterCommit`) не перевіряється тестом (PgNotify listener поза fixture).
- Полігони admin-одиниць: у seed gazetteer-lite лише центроїди/радіуси → containment у тестах — через радіуси (`GapTo` без `Boundary`); на production-gazetteer з полігонами gap точний.
- Benchmark kind-lock (serial ceiling `impact.explosion.reported`) не знімався — дані для finer partitions збирати після cutover.
