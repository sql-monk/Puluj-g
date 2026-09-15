# P05 — незалежні normalizer і rules/structured parser workers

Task: [P05 / issue #6](https://github.com/sql-monk/Puluj-g/issues/6).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p05_review`): approve after fixes → B1 (handoff/§17 наперед),
N1 (unit-тести mapper/adapter), N2 (parity з `parser_metadata`), N3 (title у `location.text`), N5 (failed-стадія переписується), N6 (event id у outputs)
виправлено; N4/N7/N8/N9 задокументовано → повторний прогін зелений. Rollout не виконувався (default deploy без змін).
Owner: Claude Code (Opus 5), агент `p05`. Reviewer: план — `p05_review` (approve after fixes; B1 paused finalizer/llm-worker, B2 attempt_id, B3 повний mapping,
B4 evidence за registry — внесено до старту, `P05-plan.md`); результат — `p05_review` (таблиця нижче).
Base commit: `4e86f9f`; результуючий commit — цей handoff комітиться разом із кодом (`P05-build-manifest.json` перелічує файли).

## Результат для споживача

- **Стадії як підписки** (topology v4): `normalizer` (`raw.stored` → `message.normalized`: text/structured/empty, `norm-1`, hash, мова) і `parser`
  (`message.normalized` → `parse.completed`; для звіту без збігу правил на live/свіжому пості — `parse.completed{needs_llm}` **і** команда `llm.requested`).
  Обидва — `IDeliveryHandler` на consumer base P03: робота в `PrepareAsync` поза транзакцією, `ApplyAsync` лише `processing.stage_results` + outbox
  (короткі tx). `finalizer`/`llm-worker` — `paused` (черги й backlog існують до P06, deliveries очікуються, reconciliation показує overdue).
- **Persisted stage status**: `processing.stage_results` (`normalize`, `parse`) з `stage_version`, `outcome`, `outputs` (attempt_id uuid, event ids,
  facts_count, fallback_reason, duration), `versions` {normalization, rules, ruleset_id, catalog_policy}; повтор того самого raw/run/stage → `noop` без другої
  події (S06); `failed` (normalization_drift) переписується наступною коректною доставкою; `processing.attempts.stage_result_id` заповнюється consumer'ом.
- **Чистий structured result**: `AlertsInUaStructuredAdapter` (те саме розпізнавання, що legacy handler, через `AlertsInUaHandler.ParsePayload/ResolvePlace`)
  повертає in-memory `Target` → факт `alert.air_raid.started|ended` без читання/запису `air_alerts`; нерозв'язане місце → `location {unknown, text: title}`.
- **`FactMapper`**: `Target` → контрактний факт (`event_kind_code` через `EventKindLegacyMap`, category з каталогу, location/confidence/evidence за
  `$defs`, `attributes` — усі поля legacy `Target`), детермінований, canonical для порівнянь; parity з legacy `RawMessageProcessor` field-by-field — 0 розбіжностей (S08).
- **Shadow-режим**: стадії не пишуть `targets`/`air_alerts`/`processing_status` (S01/S02 assert), legacy `ProcessingLoop` без змін; cutover — P14/P16.
- Worker ролі `normalizer`, `parser`; `AddPulujParsing` (спільне для legacy і стадій), `AddPulujStages`; Messaging DI — handler-based реєстрація
  (`AddSubscriptionConsumer<T>`), DLQ consumer покриває всі handler'и процесу; Compose `messaging` roles `+normalizer,parser`.
- Контракти: `topology.json` v4, `$defs/evidence` +`segment_index`, `rules[]` (additive), 6 нових fixtures, README «Runtime (P05)»; asyncapi перегенеровано.

Docs: ADR-0005 (fallback = обидві події, stage_results, `ProcessingStatus` лишається legacy), ADR-0002 (v4, paused), ADR-0003 (versions, drift),
ADR-0006 (writers stage_results, attempts.stage_result_id), `docs/adr/README.md`, README контрактів, `fork-deployment.md`, plan §4/§17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5` (seed gazetteer/taxonomy/kinds), `rabbitmq:4.3-management` (4.3.5).
Усі під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p05-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Messaging.Tests` Stage+Unit run1 → run2 | 1 → 0 | 25/3/0 (хибні очікування тестів) → 28/0/0 |
| `dotnet test tests/Puluj.Messaging.Tests` весь проєкт run3 → run4 → run5 (після review-правок) | 1 → 0 → 0 | 46/5/1 (P03-очікування «лише archive») → 51/0/1 → **53/0/1** (skip — P04-C06 W1c) |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v4 + 6 fixtures; повторно) | 0 | **60/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` (повторно) | 0 | **32/0/1** (skip — P00 baseline) |
| `dotnet test tests/Puluj.Processing.Tests` (повторно) | 0 | 105/0/0 |
| `dotnet test tests/Puluj.Analytics.Tests` (insert без identity-колонок → виправлено тест; повторно) | 1 → 0 | 31/2 → **33/0/0** |
| `dotnet test tests/Puluj.Api.Tests` / `Puluj.Admin.Tests` | 0 | 30/0/0; 56/0/0 |
| `dotnet build Puluj.sln` (фінальний) | 0 | 0 warnings |
| `docker compose … --profile broker config` | 0 | roles `relay,archive,raw-writer,normalizer,parser` |

## Evidence

[`P05-stage-evidence.md`](P05-stage-evidence.md) / `messaging-crash-evidence.json` (P05-S01…S08): без domain writes, structured без `air_alerts`,
no-text/no-facts/multi-fact, ідемпотентність стадії, fallback-рішення (needs_llm / stale / lane), parity з legacy. Load не вимірювався (P16).

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | §17/handoff заявляли done/закомічено до завершення review | handoff написано після правок; §17 — фактичний стан на момент коміту | docs |
| N1 | Обіцяні unit-тести mapper/adapter відсутні | `StageMapperTests` (детермінізм, 27 полів, canonical sort, adapter з невідомим місцем без запису) | unit 22/22 |
| N2 | S08 виключав `parser_metadata` без підстави | включено (обидві сторони стемплять `EventKindIndex.Stamp`) | run5 S08 0 розбіжностей |
| N3 | plan N5: `location.text` без place не реалізовано | fallback на `parser_metadata.title` | unit |
| N5 | `failed{normalization_drift}` блокував raw у run | `ON CONFLICT … DO UPDATE WHERE outcome = 'failed'` | run5 |
| N6 | normalizer outputs без id виданої події | `message_normalized_event_id` (pre-assigned) | run5 |
| N4, N7, N8, N9 | version mismatch → quarantine `attempts_exhausted`; sibling-події; span семантика; hedged/is_launch лише rules | README контрактів + тут | docs |
| N10 | `Llm:Enabled` без ключа → команда без виконавця | не дефект P05; llm-worker (P06) перевіряє ключ | — |

Reviewer підтвердив §16.2: п.1 (payloads валідуються проти schema у S01/S02/S03/S04/S05/S07; fixtures 60/60), п.2 (Prepare поза tx, Apply — лише stage_results+outbox,
ACK після commit), п.5 (жодного запису домену у `Stages/`; legacy handler без змін поведінки), п.7 (versions, causal chain, attempt_id ↔ stage_result_id),
п.12 (evidence = json; оновлення P03-очікувань обґрунтовані v4).

## Відомі обмеження / невиконані перевірки

- Shadow-режим: подвійний CPU на parse (legacy + стадії); `ProcessingStatus` далі пише legacy loop; workflow stage `analyzed` — з finalizer'ом (P06).
- `Llm:Enabled=true` без API key → `llm.requested` без виконавця до P06 (у deploy `Llm__Enabled=false` за замовчуванням).
- Rolling deploy з новою `normalization_version` і однією реплікою parser'а → quarantine `attempts_exhausted` (admin retry після оновлення); окремий reason — P13.
- History/replay run-ізоляція стадій не тестована end-to-end (той самий raw у двох lanes → два run — за ADR-0005). Backlog paused-черг finalizer/llm-worker
  росте до P06 (reconciliation overdue — очікувано).
- Project card — токен без scope; статус у issue/§17.

## Rollout / rollback / input ownership

Default deploy без змін (ролі normalizer/parser лише у сервісі `messaging` профілю `broker`). Увімкнення — профіль `broker`; вимкнення стадій — прибрати
ролі (черги лишаються). Rollback коду — без міграцій (P05 не додає схем; `topology_version` 4 зареєстрована — повернення до 3 потребує bump до 5 з
відповідними статусами, не даунгрейду). Ownership: `src/Puluj.Processing/Stages` (P05), Messaging DI handler-registration (P05), контракти v4.

## Чекбокси issue #6

- [x] Немає alert writes у parser (S02: `air_alerts` 0; adapter без `db.AirAlerts`); no-text/no-facts/multi-fact fixtures (contracts + S03/S04/S05); короткі DB transactions (Prepare/Apply)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (v4, evidence additive, fixtures), конфігурація (ролі, Compose), документація (ADR-0002/0003/0005/0006, README, plan) оновлені; міграцій немає

## Наступний task

**P06** — LLM worker (`llm.requested` → `llm.completed/failed`, lease/fencing у `processing.attempts`, request audit, budget) і finalizer/fact writer
(`parse.completed` + `llm.*` → канонічний extraction, `targets` з фактів через `attributes`, `observations.recorded`, `message.analysis.completed`);
активація finalizer/llm-worker → topology v5. Паралельно P13 (message explorer над stage_results/deliveries).
