# P07 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/7. Початок: 2026-09-15. Base: `6822541` + локальні незакомічені
результати P01 (`contracts/messaging/schemas/common.schema.json`: `eventKindCode`, `observationCategory`).
Виконавець: агент `p07` (Claude Code fork); незалежний reviewer: субагент `p07_review` (план і результат).
**Паралельно виконується P02 у тому самому робочому дереві** — див. «Правила паралельної роботи».

## Аналіз задачі

Хвилі 2–5, залежить лише від контрактів P01: DB-каталог `event_kinds` зі стабільними `code`, seed початкового
словника §8.2, mapping із legacy `EventType`, batch backfill `targets.event_kind_id`, compatibility adapter для
readers (DTO зберігає `eventType`), звіт unresolved mapping. Не входить: правила/resolver з БД (P08),
incidents (P10), API/UI каталогу (P11/P12), rules у `EventTypeMatcher` (лишаються).

Що є в коді: `EventType` enum (`Unknown, TargetObserved, AirRaidAlert, AlertCancelled, TargetCancelled,
ExplosionReport, AirDefenseActivity`) у `Target.EventType` (int), DTO `TargetDto.EventType: string`
(`DtoMapper` → `ToString()`), web читає `eventType` рядком; seeders `ISeeder` (`TaxonomySeeder`, `SourceSeeder`,
`GazetteerSeeder`; «БД володіє рядками, seed лише додає відсутні коди»); `DatabaseInitializer` під advisory lock;
міграції EF у `src/Puluj.Infrastructure/Persistence/Migrations` (`scripts/add-migration.ps1`); P00 SQL inventory —
тригери на `targets` (`trg_targets_insert_kinematics`, `trg_targets_duplicate`) не читають `event_type`?
**Перевірити** у `P00-sql-inventory.json` перед додаванням колонки. Контракти P01: `eventKindCode` pattern
`^[a-z_]+(\.[a-z_]+)+$`, `observationCategory ∈ target|alert|incident|info`; fixtures використовують
`target.observed`, `air_defence.activity`, `alert.air_raid.started`.

## Рішення (proposal, фіксується ADR-0008)

| Питання | Рішення |
|---|---|
| Модель `event_kinds` | §8.2 повністю: `event_kind_id, code (unique), name_uk, category (target/alert/incident/info = contract observationCategory), default_severity, state_model, requires_location_for_map, render_mode, map_color, map_icon, map_lifetime, creates_incident, enabled, map_visible, sort_order, dedup_policy jsonb, presentation jsonb, metadata jsonb, policy_version` |
| Seed | `data/taxonomy/event-kinds.json`: 15 кодів §8.2 + `unknown.unclassified` (явний outcome); `casualties.reported` **не** seed-иться (документовано). `EventKindSeeder : ISeeder`: додає відсутні коди; для існуючих оновлює лише presentation/policy при більшому `policy_version`; ніколи не видаляє і не змінює `code` |
| Legacy mapping | `TargetObserved→target.observed`, `AirRaidAlert→alert.air_raid.started`, `AlertCancelled→alert.air_raid.ended`, `TargetCancelled→target.cancelled`, `ExplosionReport→impact.explosion.reported`, `AirDefenseActivity→air_defence.activity`, `Unknown→unknown.unclassified`. Зворотно: kind без enum → `EventType.Unknown` + `metadata.legacy_fallback=true` (задокументований fallback, без вигаданого значення). Mapping — код (`EventKindLegacyMap`) і seed `metadata.legacy_event_type` |
| Міграція | Additive: `event_kinds` + `targets.event_kind_id int NULL` FK `RESTRICT` + index `(event_kind_id, observed_at DESC)` (candidate; `EXPLAIN` на synthetic dataset у handoff). Без NOT NULL/посилення constraints у P07 |
| Backfill | Окремо від міграції: `EventKindBackfill` (запускається `DatabaseInitializer` після seed, під тим самим advisory lock; та `scripts/backfill-event-kinds.sql`): batched `UPDATE targets SET event_kind_id = … WHERE event_kind_id IS NULL AND target_id BETWEEN …`, ідемпотентний, rerunnable; звіт: total, mapped per enum, unresolved (enum без mapping), null rate → лог + `P07-backfill-report.json` |
| Writer | `TargetBuilder`/structured handler пишуть **і** `EventType`, **і** `EventKindId` через `EventKindResolver` (кеш каталогу, pinned на час обробки одного повідомлення; `policy_version` у `ParserMetadata`) |
| Compatibility adapter | DTO: `eventType` лишається; додається `eventKindCode` (additive). `GET /api/event-kinds` (`EventKindDto`, тільки `enabled`) — additive, без UI змін. Analytics/`StatsFolds` читають `EventType` як раніше |
| Lock discipline (P00) | Запис `event_kind_id` іде в тій самій `targets` INSERT транзакції під Store; backfill — окремий maintenance writer, бере Store (як `fix-text-alert-ends.sql`) |

## Артефакти

| Артефакт | Шлях |
|---|---|
| Domain | `src/Puluj.Domain/Entities/EventKind.cs`, `Target.EventKindId/EventKind`, enum `EventKindCategory` (значення = contract strings) |
| Persistence | `Configurations/EventKindConfiguration.cs`, `TargetConfiguration` (FK/index), міграція `AddEventKinds`, `PulujDbContext.EventKinds` |
| Seed/backfill | `data/taxonomy/event-kinds.json`, `Seeding/EventKindSeeder.cs`, `Ingestion/EventKindBackfill.cs`, `scripts/backfill-event-kinds.sql` |
| Processing | `Puluj.Processing/Catalog/{EventKindResolver,EventKindLegacyMap}.cs`, зміни `TargetBuilder`/`AlertsInUaHandler` (мінімальні), DI |
| API/Contracts | `Puluj.Contracts` `EventKindDto`, `TargetDto.EventKindCode`; `Puluj.Api` endpoint `GET /api/event-kinds`, `DtoMapper` |
| Тести | unit у `tests/Puluj.Processing.Tests` (mapping bijection/fallback, resolver pinning, seed file ↔ contract pattern/category); integration у `tests/Puluj.Integration.Tests` (clean migration → seed parity → legacy targets → backfill counts/coverage/unresolved → rerun idempotent → FK RESTRICT → pipeline пише kind id; production-shaped: synthetic ≥50k targets batch backfill timing + `EXPLAIN`; `Down` міграції); контрактна перевірка seed codes ↔ `common.schema.json` (у Processing.Tests, читає файл) |
| Docs | `docs/adr/ADR-0008-event-catalog.md` (mapping, fallback, seed policy, compatibility window); `docs/README.md` (розділ «Дані»/«Розширення без коду»: event kinds seed); plan §17; `P07-plan.md`, `P07-handoff.md`, `P07-backfill-report.json`, TRX `test-results/p07-*.trx`, `P07-build-manifest.json` |

## Кроки

1. **Статус.** Коментар в issue #7; §17 P07 → `in_progress` (лише свій рядок); Project card за наявності scope `project`.
2. **Незалежне review плану** (`p07_review`, read-only): §8.1–8.2, §16.2 п.3, 5, 6, 7; чи backfill/seed не створюють
   другого writer scope (P00 writer map, тригери targets); чи DTO сумісний; чи не зачіпається P08/P10/P11 scope.
3. **SQL inventory check**: тригери/функції, що читають `targets.event_type` (`P00-sql-inventory.json`) — нова
   колонка не має їх ламати; зафіксувати.
4. Domain + configuration + міграція (через `scripts/add-migration.ps1` під `with-lock`); перевірити `Down`.
5. Seed file + seeder + legacy map + resolver + writer changes + backfill + SQL script.
6. DTO/endpoint additive; `DtoMapper`.
7. Тести (unit + integration на одноразовій Testcontainers PostGIS); production-shaped backfill і `EXPLAIN` для індексу.
8. **Запуск** через `scripts/with-lock.ps1`: `dotnet build Puluj.sln`; Processing/Integration/Api/Admin/Analytics suites
   (src змінено → всі); контрактні тести P01 (незмінні, але прогнати). TRX → `test-results/p07-*.trx`.
9. Docs/ADR-0008/README; звіт backfill; rollback (Down міграції + видалення seed rows лише якщо FK не тримає).
10. **Незалежне review результату**; blocking → виправити → повторити тести.
11. **Handoff §16.4**, коментар в issue, §17 → `done`, Project card за наявності scope. Commit/PR не виконуються.

## Правила паралельної роботи (P07 ∥ P02, одне робоче дерево)

- Ownership P07: `src/`, `data/`, міграції, `scripts/backfill-event-kinds.sql`, `tests/Puluj.Processing.Tests`,
  `tests/Puluj.Integration.Tests`, `tests/Puluj.Api.Tests`, `docs/adr/ADR-0008`, `docs/README.md`,
  `docs/evidence/message-platform/P07-*`. **Не чіпати** `Directory.Packages.props`, `Puluj.sln`, `deploy/`, `.env.example`,
  `contracts/messaging/`, `ADR-0001/0002`, `tests/Puluj.Transport.Spike.Tests` (P02). Нові пакети не додавати.
- `docs/plan-message-platform.md`: лише рядок P07 у §17; targeted replace, re-read перед edit.
- Кожен `dotnet build`/`dotnet test`/`dotnet ef`/`npm` — через `pwsh -File scripts/with-lock.ps1 …`.
- Не робити `git checkout/stash/reset`; не видаляти чужі незакомічені файли; `git status` перед handoff.

## Самоперевірка плану

- Міграція additive, nullable FK RESTRICT, без масового перейменування API — відповідає §8.2.
- Backfill поза EF transaction, батчами, ідемпотентний, зі звітом coverage/unresolved.
- Один writer scope: `event_kind_id` пишеться в тій самій транзакції targets під Store; backfill — maintenance з Store.
- Seed не вмикає `casualties.reported`; `unknown.unclassified` — явний outcome.
- Контрактна сумісність: коди й категорії перевіряються проти `common.schema.json` тестом.
