# P10 — incident worker, evidence, консервативне злиття, ревізії, admin-команди

Task: [P10 / issue #12](https://github.com/sql-monk/Puluj-g/issues/12).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p10_review`): approve after fixes → B1 (schema `suppressed` вкладений у `policy_version`), B2 (Admin без індексів → числовий `event_kind_code` у подіях), B3 (merged/retracted лишались кандидатами), B4 (`CanonicalSourceId null` дозволяв своєму каналу підтвердити), N1–N11, Q1–Q6 виправлено/задокументовано → повторний прогін зелений. Rollout не виконувався
(default deploy без змін — роль `incident-worker` вимкнена, як і інші writers).
Owner: Claude Code (Opus 5), агент `p10`. Reviewer: план — `p10_review` (approve after fixes; B1 confirms-map/lock set, B2 перехід fact-writer за
`expected_branches`, B3 NOTIFY `TargetCreated`, N1–N10, Q1–Q5 — внесено до старту, `P10-plan.md`); результат — `p10_review` (таблиця нижче).
Base commit: `99a9f33`; проміжний commit `0304e08` (схема, worker, policy, admin, тести — до зелених прогонів); результуючий commit — цей handoff
комітиться разом із рештою (`P10-build-manifest.json` перелічує файли обох кроків).

## Результат для споживача

- **Схема** (міграція `AddIncidents`, ADR-0010): `incidents` (state `reported|confirmed|resolved|retracted`, `suppressed`, `event_at` = min по evidence,
  найточніша локація, `source_count`, `independent_source_count` NULL, `revision`, `last_event_id`, `generation_id`), `incident_observations` (unique
  `observation_id`; `relation canonical|supports|echo|confirms|ambiguous|moved`, `score`, `decision_reason`, `policy_version`), `incident_revisions`
  (snapshot на кожну ревізію — as-of history зі snapshot, actor/reason для команд).
- **Owner `incident-worker`** (`IncidentWriterHandler` → `IncidentStateWriter`): fact writer incident-observations (рядок `targets`, links,
  NOTIFY `TargetCreated`) і єдиний writer агрегату. Locks: `Store` shared → `incident:kind:{id}` exclusive для kind факту і kinds, які він підтверджує
  (sorted); кандидати під lock → race двох реплік дає один incident (I02). Finalizer: `incident → incident-worker`; track-worker пропускає incident-факти
  лише коли подія називає цю гілку (старі v6-події дописує сам).
- **Policy `incident-1`** (`IncidentPolicy`, pure, `dedup_policy {windowMinutes, slackKm, confirms[]}` у каталозі — seed `policyVersion 2`): симетричне
  вікно від `event_at`, `SpatialAnchor.GapTo ≤ slack` (containment район/область — gap 0), без geometry — лише той самий place, score `0.6·time +
  0.4·space ≥ 0.55`, ambiguity margin 0.1 → окремий incident `ambiguous` (review), `near_candidates` для ревʼю; закриті — лише late evidence
  (`effective_at ≤` effective-dated closure з ревізії). Echo не змінює state/`source_count`; `confirms` (kind із `confirms`, інше джерело) → `confirmed`;
  `resolved`/`retracted` — лише команди; відбої/кінець тривоги incidents не чіпають.
- **Ревізії/події**: `incident.changed{created|updated|resolved|retracted|suppressed|merged|split}` (+`suppressed`, `location`, `policy_version`,
  `generation_id`), `aggregate_id incident:{id}`, `partition_key incident:kind:{code}`; routable через `archive` (v7). Redelivery/інший run → skip без
  revision.
- **Admin `/api/admin/incidents`**: list/get (+links, revisions зі snapshot), `resolve|retract|confirm|suppress|unsuppress|merge|split` — через той
  самий `IncidentStateWriter` (власна tx, ті самі locks, actor/reason обов'язкові, envelope як у watchdog-команд); raw evidence/`targets`/`observations`
  незмінні (I08 hash); 400/404/409; outbox off → 409.
- **Generation**: `run.generation_id` або live `UUIDv5(generation:live)` (`processing.generations` on demand) — до P14.
- Ролі: `incident-worker` у `StageRoles.DomainWriters`, `WorkerOptions`, `deploy.ps1 -DomainWriters` (cutover тепер 4 ролі), Compose коментар.

Docs: ADR-0010 (новий), ADR-0002 (v7), ADR-0005 (domain_completed/generation), ADR-0006 (таблиці), ADR-0009 (owner incident, cutover 4 ролі),
`docs/adr/README.md`, README контрактів («Runtime (P10)»), `completion-manifest.json` (коментар), `docs/README.md` (admin API incidents),
`fork-deployment.md`, plan §16.1 (команда I01–I08) / §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (Testcontainers 4.15.0).
Усі під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p10-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests --filter IncidentWriterTests` run1 → run5; run6 → run7 (після review, +I09) | 1 → 0; 1 → 0 | 1/7 → 4/4 → 7/1 → 8/0 → 8/0; 8/1 → **9/0** |
| `dotnet test tests/Puluj.Processing.Tests --filter IncidentPolicyTests` | 0 | **12/0** |
| `dotnet test tests/Puluj.Messaging.Tests` run1 → run2 → run3 → run4 (після review) | 1 → 1 → 0 → 0 | 81/1/1 → 81/1/1 → 82/0/1 → **83/0/1** (I05 у suite: міст `raw.stored/history` для синтетичних raw → прямий insert; skip — P04-C06 W1c) |
| `dotnet test tests/Puluj.Processing.Tests` → run2 → run3 | 1 → 0 → 0 | 133/1 → 134/0 → **134/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` → run3 → run4 | 1 → 0 → 0 | 36/1/1 → 37/0/1 → **37/0/1** (`eventKindPolicyVersion` 2) |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v7) → run4 (fixture) | 0 | 61/0/0 → **61/0/0** |
| `dotnet test tests/Puluj.Admin.Tests` (+2 Guard) / `Api` / `Analytics` | 0 | **64**, 30, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |

## Evidence

[`P10-incidents-evidence.md`](P10-incidents-evidence.md), `messaging-crash-evidence.json` (P10-I01…I08).

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | `suppressed` у схемі `incident.changed` опинився всередині `policy_version` (валідатори мовчки ігнорують) | властивість перенесено на верхній рівень `properties`; fixture `incident.changed` оновлено за реальною подією (`suppressed`, `policy_version incident-1/p2`, revision 1, `partition_key`) | Contracts 61/0; I01/I04/I08 `Valid` |
| B2 | Admin ніколи не вантажив `IndexProvider` → `KindCode` повертав числовий id → `event_kind_code = "5"` (порушення `common.schema`) | `Guard` викликає `AdminIndexes.EnsureFreshAsync` перед командою; `KindCode` кидає `IncidentConflictException` замість числового fallback | build; `IncidentEndpointsTests` (Guard), ADR-0010 «Межі» |
| B3 | Merged source (і retracted) лишались кандидатами: факт після merge міг приєднатись до мертвого incident'а | policy: `retracted` — ніколи не кандидат; writer додатково прибирає `merged_into_incident_id`; I08 — факт після merge → новий incident 4 | I08, unit `Closed_incidents…` |
| B4 | `CanonicalSourceId` з link `canonical` → null для ambiguous/split incidents → свій канал міг `confirms` | canonical source за `CanonicalObservationId`; `Relation`: джерело, яке incident уже чув, → `echo` завжди | unit `Echo_supports_and_confirms` (+2 кейси) |
| N1 | Тавтологічна перевірка змін подій у I02 | `Assert.Equal(["created","updated"], …)` | I02 |
| N2 | Ambiguity у I03 не відтворювалась (вакуумний `\|\|`) | детермінований сценарій: A `t0`, B `t0+121`, третє джерело `t0+60` → 0.7 vs 0.695 → `ambiguous [1,2]` | I03 |
| N3 | Placeholders у evidence; I05 падав у повному suite | root cause — міст `raw.stored/history` з `IngestAsync` для синтетичних raw → прямий insert; таблиці заповнено реальними числами, run5–run7 перелічено | Messaging run3/run4 |
| N4 | `policy_version` події ≠ links; fixture застарілий | `PolicyVersion` спільний (`incident-1/p{catalog}`); fixture оновлено | Contracts |
| N5 | Обіцяний тест змішаного raw відсутній | I09: target + incident в одній події → обидва writers, 1 incident, 1 трек | I09 |
| N6 | `updated_at` отримував evidence time | `updated_at` = clock; `effective_at ?? now` → у ревізію; Q6: `event_at ≤ effective_at ≤ now` | I05, I07 |
| N7 | Merge без перевірки generation, губив relation, не перераховував confidence/location | 409 для різних generations; `original_relation` у reason; `AbsorbEvidence` (найточніша локація, max confidence) | I08 |
| N8 | Split: локація з першого рядка, `confirmed` без `confirms` | найточніший з перенесених рядків, max confidence; source `confirmed → reported`, якщо `confirms` пішов | I08, ADR |
| N9 | Невідомий kind → retry-шлях; `CodeOf` лінійний | refresh індексу, далі `PermanentDeliveryException unknown_kind` | код |
| N10, N11 | Застарілий коментар harness; слабка перевірка cancellations | оновлено; I05 перевіряє incident 1 `resolved` revision 3 | I05 |
| Q1–Q6 | Третій writer через тригери; retracted late evidence; асиметричний crossover; `is_active` race; hashtext; валідація `effective_at` | ADR-0010 «Межі»/п.4/п.7; `effective_at` валідується | docs |

Reviewer підтвердив: lock hierarchy без циклів між родинами locks, кандидати під lock, I02 — справжня race, ідемпотентність (unique + guards + inbox),
symmetric window/threshold/margin/containment, revisions/snapshot/payload за схемою, migration Up/Down, mapping статусів, topology v7/asyncapi/manifest, docs.

## Відомі обмеження / невиконані перевірки

- Replay lane incident-worker і promote generation — P14; projection/`NOTIFY IncidentChanged` для карти — P11 (мапа показує incident-факти як `targets`).
- Межа кандидатів = kind (crossover вибух/пожежа не зливається); `independent_source_count` NULL; benchmark kind-lock не знімався.
- Admin endpoints перевірені через `IncidentStateWriter`, не HTTP; 409 при вимкненому outbox без тесту; NOTIFY після commit без тесту.
- Seed gazetteer-lite без полігонів — containment у тестах через радіуси. Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy без змін поведінки (міграція additive, `incident-worker` вимкнений). Cutover — ADR-0009/0010: зупинити `processing`, увімкнути
`track-worker,alert-worker,watchdog,incident-worker` (`deploy.ps1 -Broker -DomainWriters`); rollback — зворотно (incidents лишаються, legacy їх не знає).
Ownership: `Puluj.Processing/Incidents/*` (P10), `IncidentEndpoints`, topology v7, `event-kinds.json` `dedupPolicy`.

## Чекбокси issue #12

- [x] Conservative merge/split і race tests (I02, I03, I08), immutable evidence (I08), as-of history (I07), echo не підвищує state (I04)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (v7), міграція/rollback, конфігурація (ролі, deploy.ps1, Compose), документація (ADR-0010, 0002/0005/0006/0009, README, fork-deployment, plan) оновлені

## Наступний task

P11 (issue #11) — read-side/API/catalog/incident DTO, cursor/windows, SignalR bridge (§8.6).
