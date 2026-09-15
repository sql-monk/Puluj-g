# P07 — DB-каталог event kinds, legacy mapping, backfill, compatibility adapter

Task: [P07 / issue #7](https://github.com/sql-monk/Puluj-g/issues/7).
Status: **done** — локальна реалізація й тести завершені; незалежне review результату (`p07_review`):
**approve after fixes** → усі fixes внесено, перевірки повторено → повторна перевірка reviewer: **approve** (B1–B2 закриті).
Commit/PR/rollout не виконувалися.
Owner: Claude Code (агент `p07`, паралельно з P02 у тому самому робочому дереві). Reviewer: незалежний субагент `p07_review` (координатор), approved.
Base commit: `6822541` + P01 commit `d4ec6be` (HEAD); результат — локальні незакомічені файли (перелік із SHA-256 — `P07-build-manifest.json`).

## Що змінилось для користувача/споживача

- **Схема (additive, міграція `20260915132238_AddEventKinds`)**: таблиця `event_kinds` (§8.2 повністю; `category` —
  text `target|alert|incident|info` = contract `observationCategory`), `targets.event_kind_id int NULL` FK `RESTRICT`,
  індекс `ix_targets_event_kind_id_observed_at (event_kind_id, observed_at DESC)`. `Down` перевірено тестом.
- **Seed** `data/taxonomy/event-kinds.json` (policyVersion 1): 15 кодів §8.2 + `unknown.unclassified`; `casualties.reported`
  не seed-иться. `EventKindSeeder` — insert-missing, presentation refresh лише за більшим `policyVersion`, `code` незмінний,
  admin `enabled` зберігається.
- **Legacy mapping** `Puluj.Domain.EventKindLegacyMap` (одна таблиця для C#/seed/SQL): усі 7 членів `EventType` → код;
  kind без enum → `EventType.Unknown` як задокументований fallback.
- **Writer**: `RawMessageProcessor` стемпить `EventKindId` на кожен target (текстовий і structured шлях) поряд із
  legacy `EventType`, один snapshot каталогу на повідомлення, у тій самій транзакції під Store; `ParserMetadata.eventKindPolicyVersion`
  цитує policy version snapshot-а; порожній каталог → одноразовий warning на процес, `IndexProvider` перечитує кожні 15 s.
- **Backfill** `EventKindBackfill` (ISeeder Order 90 у `DatabaseInitializer`) + `scripts/backfill-event-kinds.sql`:
  батчі 5 000 за `target_id`, кожен — транзакція під `pg_advisory_xact_lock(Store)`, ідемпотентний; звіт coverage/unresolved.
- **API/DTO (additive)**: `TargetDto.eventKindCode` (trailing optional, `null` до backfill), `EventKindDto`,
  `GET /api/event-kinds` (лише `enabled`). Web, analytics, `StatsFolds`, `EventTypeMatcher` — без змін.
- Docs: `docs/adr/ADR-0008-event-catalog.md`, `docs/README.md` (Дані, API, Розширення без коду), §17.

Контракти/міграції/flags/конфігурація: нова міграція (additive); `SeedOptions.SeedEventKinds`/`BackfillEventKinds`
(default `true`); нових пакетів, змін `Puluj.sln`, Compose, `.env` — немає.

## Unresolved mapping report

- Mapping: `Unknown→unknown.unclassified`, `TargetObserved→target.observed`, `AirRaidAlert→alert.air_raid.started`,
  `AlertCancelled→alert.air_raid.ended`, `TargetCancelled→target.cancelled`, `ExplosionReport→impact.explosion.reported`,
  `AirDefenseActivity→air_defence.activity`. Enum-члени без mapping: **немає**. Seed-коди без enum (8): `target.launch`,
  `air_defence.interception.reported`, `impact.confirmed`, `fire.reported`, `damage.reported`, `infrastructure.outage`,
  `civil_defence.notice`, `evacuation.notice` — legacy fallback `Unknown` (задокументовано в ADR-0008).
- Backfill на тестових даних: значення `event_type`, невідомі мапі (тест: 999), лишаються `NULL` і рапортуються
  (`unresolvedByEventType`), не вгадуються. Live/dev БД **не** запускалась; production unresolved rate невідомий до rollout.
- Production-shaped synthetic 50 000 targets (`P07-backfill-report.json`, `ANALYZE` після backfill): 10 батчів, ~1.7 s,
  coverage 1.0, 0 unresolved; map-запит — Index Scan `ix_targets_event_kind_id_observed_at` (est. 20 102, 100 rows, 7 hits);
  `count(*) WHERE event_kind_id IS NULL` — Index Only Scan того самого індексу.

## Тести (команда → exit code → результат → середовище)

SDK 10.0.401, Debug/net10.0, Windows; Docker engine, Testcontainers `postgis/postgis:17-3.5` (одноразова БД,
`PULUJ_TEST_CONNECTION` не задано). Усі команди — під `pwsh -File scripts/with-lock.ps1` (паралельний P02).

| Команда | Exit | Passed / Failed / Skipped | TRX |
|---|---|---|---|
| `dotnet build Puluj.sln` | 0 | 0 warnings | — |
| `dotnet test tests/Puluj.Processing.Tests/...` (+7 нових `EventKindCatalogTests`) | 0 | 105 / 0 / 0 | `test-results/p07-Puluj.Processing.Tests.trx` |
| `dotnet test tests/Puluj.Api.Tests/...` (+2 `EventKindDtoTests`) | 0 | 30 / 0 / 0 | `p07-Puluj.Api.Tests.trx` |
| `dotnet test tests/Puluj.Admin.Tests/...` | 0 | 56 / 0 / 0 | `p07-Puluj.Admin.Tests.trx` |
| `dotnet test tests/Puluj.Analytics.Tests/...` | 0 | 33 / 0 / 0 | `p07-Puluj.Analytics.Tests.trx` |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests/...` (контракти P01, незмінні) | 0 | 54 / 0 / 0 | `p07-Puluj.Messaging.Contracts.Tests.trx` |
| `PULUJ_EVIDENCE_DIRECTORY=… dotnet test tests/Puluj.Integration.Tests/...` (+6 `P07EventKindTests`) | 0 | 32 / 0 / 1 explicit baseline skip | `p07-Puluj.Integration.Tests.trx` |

Разом **310 passed, 0 failed, 1 explicit skip** (`PULUJ_RUN_BASELINE` opt-in); після review fixes Processing/Api/Integration
перезапущено з тими самими числами (TRX перезаписано). Перший прогін P07 integration (`p07-integration-run1.trx`,
лише 6 нових тестів) — 6 passed. Проміжні збірки з помилками компіляції (using,
regex escape, `\n` у літералі, `SqlQueryRaw` з інтерполяцією) виправлені до першого прогону; невдалих прогонів тестів не було.

Integration покриває: seed parity + noop reseed + presentation refresh за policyVersion зі збереженням `enabled`;
backfill усіх legacy значень + unresolved 999 + ідемпотентність (batch 3); FK RESTRICT і unique code; pipeline
(текст і alerts.in.ua) пише `event_kind_id` **і** `event_type`, тригер `puluj_on_target_insert` далі рахує daily stats;
50k backfill + `EXPLAIN (ANALYZE, BUFFERS)`; `Down`/`Up` міграції з повторним seed.

## Review findings → виправлення → повторна перевірка

`p07_review` (координатор, read-only): **approve after fixes**.

| # | Finding | Виправлення | Повторна перевірка |
|---|---|---|---|
| B1 | `ANALYZE targets` до backfill → EXPLAIN на застарілій статистиці (map rows=1, IS NULL Seq Scan) | `ANALYZE` після `RunAsync`, перед `Explain`; коментар у `TargetConfiguration.cs` і «Виміряно» ADR-0008 — за фактичним планом | Integration 32/0/1; `P07-backfill-report.json`: Index Scan est. 20 102/100 rows; IS NULL → Index Only Scan |
| B2 | Base commit: HEAD = `d4ec6be` (P01 закомічено) | plan/handoff/issue: `6822541` + P01 `d4ec6be` | manifest `baseCommit` = `d4ec6be` |
| N1 | `PolicyVersion` не використовувався | `Stamp` пише `ParserMetadata.eventKindPolicyVersion`, існуючі metadata зберігаються | unit (з/без metadata) + integration assert |
| N2 | MIN/MAX по всіх NULL, «вічний» unresolved розширює скан | MIN/MAX лише по `EventKindId == null && mapped.Contains(EventType)`; ранній вихід | Integration backfill/idempotent тести |
| N3 | Порожній каталог: 10-хв refresh; warning на кожне повідомлення | `IndexProvider` delay 15 s поки `EventKinds.IsEmpty`; warning раз на процес (`Interlocked`) | build; поведінка описана в ADR-0008 |
| N5 | `Enum.TryParse` приймає числа; `ArgumentException` з `LegacyEventType` | `Enum.GetNames` порівняння; `InvalidOperationException` з кодом kind | unit: `"2"` і `NoSuchMember` відхиляються |
| N6 | Doc-дрейф (`legacy_event_type`, state models, шляхи артефактів, README про видалення seed) | виправлено в `EventKind.cs`, `P07-plan.md`, `docs/README.md` | — |
| N8 | `EventKindSeeder.EventKindDto` — не DTO | → `EventKindSeedEntry` | build/tests |
| N4/N7 | Індекс у EF-транзакції; SQL-скрипт — один батч | зафіксовано як tradeoff у ADR-0008 | — |

Повторна перевірка reviewer: B1–B2 закриті, N1–N8 на місці — **approve**. Два зауваження після approve:
(1) `Infrastructure.dll` mtime пізніший за Integration TRX через паралельну збірку P02 тих самих джерел (хеш = manifest);
(2) Admin/Analytics/Messaging.Contracts перепрогнано координатором на актуальній збірці після fixes:
56 / 33 / 54 passed, 0 failed (`test-results/p07-Puluj.{Admin,Analytics,Messaging.Contracts}.Tests.trx`).

## Самоперевірка за §16.2 (виконавець)

- п.1: категорії/коди звірені з `common.schema.json` тестом; DTO additive (trailing optional), web не читає нове поле.
- п.3: стемпінг лише для `EventKindId == null`; backfill лише `IS NULL`; replay/retry не дублюють.
- п.5: тригери targets (`AFTER INSERT`, `AFTER UPDATE OF duplicate_of_target_id`) не спрацьовують на backfill; backfill
  бере Store; жодного другого власника derived state.
- п.6: additive міграція; `Down` тестом; rollback реалістичний (зупинити writers → Down → стара збірка).
- п.7: `event_type`, `parser_metadata`, evidence не змінюються; тест перевіряє збережений enum після backfill.
- п.12: усі suites реально виконані на актуальній збірці; TRX додані.

## Відомі обмеження / невиконані перевірки

- Закриття issue і §17 `done` — після підтвердження reviewer координатором.
- `GET /api/event-kinds` не тестується HTTP-рівнем (у `Puluj.Api.Tests` немає WebApplicationFactory); мапер — unit-тестом.
- Якщо процесор стартував з порожнім каталогом (fresh DB до seed) — до наступного refresh `IndexProvider` (15 s поки
  каталог порожній) targets отримують `NULL`; backfill виконується лише при старті або вручну (`scripts/backfill-event-kinds.sql`, один батч за виклик).
- Індекс `(event_kind_id, observed_at DESC)` перевірено на synthetic 50k, не на production-shaped даних.
- `unknown.unclassified`/нові kinds не мають правил розпізнавання — P08; incidents — P10; UI/legend — P11/P12.
- GitHub Project card не змінено (токен `gh` без scope `project`).

## Rollout / rollback / input ownership

Rollout: узгоджена збірка → `migrate` role (міграція → seed `event_kinds` → backfill під session lock) → processors.
Backfill на production: батчі по 5 000 під Store; при великій таблиці — можна виконати `scripts/backfill-event-kinds.sql`
у maintenance window до старту процесорів (ідемпотентно). Rollback: зупинити writers нової збірки → `dotnet ef database
update 20260915085730_AddLlmRequestAudit` (Down: FK, індекс, колонка, таблиця; seed rows зникають) → стара збірка.
Ownership: `event_kinds` — seed + admin (`enabled`); `targets.event_kind_id` — processor (insert) і backfill (NULL only).

## Чекбокси issue #7

- [x] Clean/production-shaped migration, seed parity, unresolved mapping report, contract compatibility
- [x] Необхідні тести виконані на актуальній збірці з реальним PostGIS (RabbitMQ не потрібен для P07)
- [x] Незалежне code review завершене (approve); blocking findings виправлені й підтверджені reviewer
- [x] Контракти, міграція/rollback, конфігурація та документація оновлені в межах задачі

## Наступний крок

Підтвердження reviewer після fixes → координатор: `gh issue close 7`, §17 `done`.
Далі за залежностями: P08 (rules/resolver з БД поверх `event_kinds`), P10 (incidents за `creates_incident`/`state_model`).
