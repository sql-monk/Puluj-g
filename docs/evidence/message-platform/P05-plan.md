# P05 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/6. Початок: 2026-09-15. Base: `4e86f9f` (master, P04 закомічено, дерево чисте).
Залежності P03/P04 — done (consumer base `SubscriptionConsumer`/`IDeliveryHandler`, outbox/inbox/receipts, raw-writer + `raw.stored{is_new}`, topology v3).
Виконавець: Claude Code (Opus 5); незалежний reviewer: субагент `p05_review` (план → результат). Результат комітиться.

## Аналіз задачі

Хвиля 4, «розділення extraction»: normalizer і rules/structured parser стають незалежними підписками шини (`raw.stored` → `message.normalized`
→ `parse.completed` | `llm.requested`), результат кожного етапу — persisted `processing.stage_results`, structured adapter повертає **чистий**
результат розпізнавання без запису `air_alerts`. Критерії issue: немає alert writes у parser; fixtures no-text/no-facts/multi-fact; короткі DB
транзакції. Gate хвилі 4 (§12): «No-facts/failed також видимі; LLM не затримує raw чи незалежні rules jobs».

Що є (перевірено на base):

- Legacy: `RawMessageProcessor.ProcessAsync` — одна транзакція під row lock: `AlertsInUaHandler.HandleAsync` (читає/пише `air_alerts` + Target)
  або `Normalizer.Normalize` → `IParser.ParseAsync` (`LlmParser` = rules + LLM fallback усередині) → `TargetBuilder.Build` → `targets` + sinks під
  Store lock; `ProcessingStatus` = сумарний стан. `ProcessingLoop` прокидається NOTIFY `RawMessageStored` (raw-writer P04 шле його для live/is_new).
- Contracts: `message.normalized` (raw_message_id, normalization_version, text_kind text|structured|empty, structured_kind, language, normalized_text,
  normalized_text_hash), `parse.completed` (attempt_id, outcome facts|no_facts|unsupported|needs_llm|needs_review|failed, method, versions, facts[]
  з event_kind_code/category/effective_at/location/confidence/evidence/attributes, fallback_reason, error, duration_ms), `llm.requested` (command:
  request_id, model, prompt_version, fencing_token, deadline_at, budget, input{normalized_text_hash, normalization_version}, rules_context).
  Topology: `normalizer` binds raw.stored, emits message.normalized; `parser` binds message.normalized, emits parse.completed + llm.requested
  (обидва `planned`); `finalizer`/`llm-worker` — planned (P06).
- P03/P04 інфраструктура: `SubscriptionConsumer` (робота в `PrepareAsync` поза tx, `ApplyAsync` у короткій tx з inbox/receipt/outbox), `processing.stage_results`
  (таблиця без writers), `EventKindIndex` (`EventKindLegacyMap` EventType → code, `EventKind.Category`), `IndexProvider` (taxonomy/gazetteer/kinds).

Що P05 **не** робить: LLM worker, finalizer/fact writer, `observations.recorded`/`message.analysis.completed` (P06); alert worker — власник `air_alerts`
(P09); cutover legacy `ProcessingLoop` і mapping `ProcessingStatus` ⇔ workflow stages (лишається за legacy loop до P14/P16 — стадії P05 працюють
**додатково**, shadow); DB-driven rule versions/preview (P08); UI стадій (P13).

## Рішення, прийняті планом

| # | Рішення | Мотив / межі |
|---|---|---|
| D1 | Два handler'и `IDeliveryHandler` у **`src/Puluj.Processing/Stages/`** (`NormalizerHandler`, `ParserHandler`); `Puluj.Processing` отримує ProjectReference на `Puluj.Messaging` (Messaging → Infrastructure; циклу немає). Реєстрація через `Puluj.Messaging.DependencyInjection.AddSubscriptionConsumer<THandler>(services, instanceName)`; `DlqConsumer` бере перелік підписок з зареєстрованих `IDeliveryHandler`, не з константного списку | Handler'ам потрібні `Normalizer`, `RuleParser`, `TargetBuilder`, `IIndexes` (Processing); Worker ролі `normalizer`, `parser` (`BrokerRoles`) |
| D2 | **Normalizer**: `PrepareAsync` — власне з'єднання, читає `raw_messages` (text/payload/source), визначає `text_kind`: `structured` (payload `kind` = alert.*), `text` (непорожній `raw_text`), `empty`; для text — `Normalizer.Normalize` → `normalized_text`, `language`, SHA-256 hash; версія `Normalizer.Version` (нова константа `norm-1`, дорівнює поточній поведінці). `ApplyAsync` — `processing.stage_results` (stage `normalize`, `stage_version`, outcome `completed`, outputs {text_kind, language, segments, hash, event_id}) `ON CONFLICT (raw, run, stage, version) DO NOTHING`; якщо рядок уже є (повторний `raw.stored{is_new:false}` з іншим event_id) → `noop`, без другого `message.normalized` | Short tx: parsing поза tx; ідемпотентність за stage_results |
| D3 | **Parser**: `PrepareAsync` — читає raw + source; `text` → повторно `Normalize(raw_text)` (детерміновано; hash звіряється з payload, розбіжність → outcome `failed` з error `normalization_drift`, retryable=false) → `RuleParser.Parse` (**лише правила**, без `LlmParser`) → `TargetBuilder.Build` **в пам'яті** (без `db.Targets.Add`) → facts за контрактом (`event_kind_code` через `EventKindLegacyMap`, `category` з `EventKindIndex`/enum fallback, location {kind, place_id, geometry point, accuracy_km, text}, confidence, evidence {segment_index, span, rules[], parser_version}, attributes {target_category/class/model ids, count, direction, alert_level, is_launch}); `structured` → **`AlertsInUaStructuredAdapter.Extract(raw)`** — чистий факт `alert.air_raid.started|ended` (place через gazetteer як у legacy handler, level, alert_type, source_alert_id, started/finished_at) **без читання/запису `air_alerts`**; `empty` → `unsupported`. Outcomes: facts / no_facts / unsupported / needs_llm / failed. `ApplyAsync` — stage_result (stage `parse`, version `RuleParser.Version` або `AlertsInUaStructuredAdapter.Version`, outputs {outcome, facts_count, method, event_ids}) + outbox `parse.completed` (+ `llm.requested` при needs_llm); повтор → noop | Критерій «немає alert writes у parser»; чистий структурований результат (§4); legacy `AlertsInUaHandler` лишається для legacy loop до P09 |
| D4 | **LLM fallback рішення** (без виклику LLM): rules порожні ∧ text ∧ `LlmParser.LooksLikeTargetReport` (зробити internal static) ∧ `Llm:Enabled` ∧ lane `live` ∧ вік ≤ `Llm:MaxMessageAgeHours` → outcome `needs_llm` (`fallback_reason`), публікуються **обидва**: `parse.completed{needs_llm, facts:[]}` і команда `llm.requested` (request_id UUIDv7, model/prompt з `LlmOptions`, `fencing_token: 1`, `deadline_at` = now + Llm timeout×3, budget {max_output_tokens}, input {hash, normalization_version}, rules_context {facts: []}). Attempts/lease LLM — P06 (llm-worker planned → не очікується) | ADR-0005 «fallback required → awaiting_llm»: finalizer (P06) бачить rules-спробу з `parse.completed` і чекає `llm.*`; topology «або» трактуємо як «команда додатково до факту спроби» — зафіксувати в ADR-0005/README |
| D5 | `processing.stage_results.outputs`/`versions` jsonb; `worker` = `{subscription}@{instance}`; `started_at/finished_at` з Prepare/Apply; `duration_ms` у payload | §5.2 persisted stage status |
| D6 | Envelope: `raw_message_id`, `source_*` з вхідного envelope; `causation_id` = вхідний event, той самий `correlation_id`, `processing_run_id` з вхідного; `producer` = `normalizer@{instance}` / `parser@{instance}` | ADR-0003 |
| D7 | `topology.json` v4: `normalizer`, `parser` → `active`; expected set `raw.stored` = normalizer + archive (message-analytics planned), `message.normalized` = parser, `parse.completed` = (finalizer planned → нікого; archive не binds) — це нормально: P06 активує; Compose `messaging` roles `+normalizer,parser` | ADR-0002 |
| D8 | **Shadow-режим**: стадії працюють паралельно з legacy `ProcessingLoop` (той далі пише targets/alerts/ProcessingStatus); жодних спільних записів (stage_results/outbox лише). Cutover per source/lane — P14/P16. `ProcessingStatus` mapping (ADR-0005 «Відкрите» P05) → фіксуємо як «залишається за legacy loop до cutover; workflow stage `analyzed` з'явиться з P06 finalizer» | Без подвійних writers (§16.2 п.5); ризик — подвійний CPU на parse |
| D9 | Contracts: нові fixtures `message.normalized.{structured,empty}.json`, `parse.completed.{no-facts,unsupported,multi-fact,needs-llm}.json`, `llm.requested` без змін; README «Runtime (P05)». Contract tests валідують fixtures автоматично | Критерій «no-text/no-facts/multi-fact fixtures» |
| D10 | Тести: `MessagingFixture` +`AddPulujProcessing` (без старту hosted services) + seeders (taxonomy, gazetteer, kinds) + `IndexProvider.RefreshAsync`; `Integration/StageTests.cs` P05-S01…S08; unit `StageContractTests` (payloads handler'ів проти schema); `Processing.Tests` для `AlertsInUaStructuredAdapter` і mapping facts (без БД) | §16.2 п.1/п.2/п.12 |
| D11 | Commit наприкінці після review approve | Вимога користувача |

## Тестова матриця

| Test | Сценарій | Assert (committed) |
|---|---|---|
| P05-S01 | Текст із ціллю (`Шахеди на Сумщині курсом на Полтавщину`) через ingress → raw-writer → normalizer → parser | stage_results: normalize (`completed`, text_kind text, hash) + parse (`completed`, outcome facts, facts_count ≥ 1); outbox: `message.normalized` (causation = raw.stored id), `parse.completed{facts}` з `event_kind_code=target.observed`, `category=target`, location place; **`targets` 0, `air_alerts` 0** (стадії не пишуть домен); receipts normalizer/parser `completed`; усі tx короткі (без Store lock) |
| P05-S02 | Structured: alerts `31:start` (`alert.started` з `location_oblast`) і `31:end` | `message.normalized{text_kind structured, structured_kind alerts_in_ua.alert.started}`; `parse.completed{facts}` з `alert.air_raid.started` / `…ended`, `category alert`, location place (oblast), attributes {source_alert_id, alert_type, level}; `air_alerts` 0 |
| P05-S03 | No-text: raw без тексту й без структурованого payload | `message.normalized{text_kind empty}` → `parse.completed{unsupported}`; stage_results 2; receipts completed |
| P05-S04 | No-facts: текст без цілей («Доброго ранку, друзі») | `parse.completed{no_facts, facts: []}`; stage parse outcome `no_facts` |
| P05-S05 | Multi-fact: список («Шахеди: Суми, Полтава; Ракета на Київ») | facts ≥ 2, різні місця/kinds; `facts_count` у stage outputs = кількість |
| P05-S06 | Повторний `raw.stored{is_new:false}` (redelivery ingress) після нормалізації | normalizer: receipt `noop`, `message.normalized` лише 1, stage_results normalize 1 |
| P05-S07 | needs_llm: текст-звіт без розпізнаних цілей при `Llm:Enabled=true` | `parse.completed{needs_llm, fallback_reason}` + `llm.requested{request_id, fencing_token 1, input.hash}`; `Llm:Enabled=false` → `no_facts` без команди |
| P05-S08 | Parity: той самий текст через legacy `RawMessageProcessor.ProcessAsync` (targets) і через стадії | кількість фактів і набір `event_kind_code` збігаються з legacy targets (`EventKindLegacyMap.ToCode(EventType)`) |
| Unit | payloads `NormalizerHandler`/`ParserHandler` проти `message.normalized`/`parse.completed`/`llm.requested` schema; `AlertsInUaStructuredAdapter` на fixture payload | valid |
| Crash | inherit P03 (consumer base): W4/W5 не повторюємо; S06 покриває дедуп на stage_results | — |

## Кроки

1. Статус: коментар в issue #6 (in_progress), §17 → `in_progress`.
2. Незалежне review плану (`p05_review`) → blocking правки.
3. Реалізація: Messaging DI refactor (handler-based registration, DlqConsumer з handlers), Processing → Messaging reference, `Normalizer.Version`,
   `AlertsInUaStructuredAdapter`, `FactMapper` (Target → contract fact), `NormalizerHandler`, `ParserHandler`, `LlmParser.LooksLikeTargetReport` internal,
   Worker roles/Compose/appsettings, topology v4 + fixtures + README.
4. Тести (unit + Processing.Tests + integration S01–S08); контрактні; повні suites; build; compose config.
5. Docs (ADR-0005 «Відкрите» P05, ADR-0002 v4, ADR-0006 stage_results writers, README контрактів, plan §16.1/§17), evidence, review результату → правки →
   повторний прогін, handoff, issue comment + close, commit.

## Незалежне review плану — p05_review

Вердикт: **approve after fixes**. Blocking → правки внесено до старту:

| # | Finding | Правка |
|---|---|---|
| B1 | `parse.completed`/`llm.requested` без bound черг (finalizer/llm-worker planned) → relay `mandatory` → нескінченні unroutable retries, outbox росте | D7: у v4 `finalizer` і `llm-worker` → **`paused`** (черги оголошуються, backlog у quorum queue, expected deliveries створюються, reconciliation показує overdue — чесний стан «required consumer відсутній до P06»); `TopologyRegistryTests` оновити |
| B2 | `parse.completed.attempt_id` required uuid; `processing.attempts.attempt_id` — bigint, handler його не бачить | `attempt_id` = UUIDv7 спроби парсингу з `PrepareAsync`, у `stage_results.outputs.attempt_id`; `DeliveryResult.StageResultId` → consumer пише `attempts.stage_result_id` (колонка є); семантика в README |
| B3 | Mapping facts губить поля `Target` (family, model/classification confidence, identification source, count approx, origin/destination, direction deg/kind/conf, segment text, parser metadata) → дрейф legacy vs нові факти невидимий | `FactMapper` серіалізує **усі** поля `Target` (крім id/FK) детерміновано в `attributes` (+`parser_metadata`), shape у README; S08 — parity field-by-field через той самий mapper проти legacy `Target` |
| B4 | `evidence` не за `$defs/evidence` | `rule_id` = join(rules), `rule_version` = parser version, `structured_field` для structured, `span`; optional `segment_index`, `rules[]` додано в `$defs/evidence` (additive) |

Non-blocking прийняті: N1 (`needs_llm` = обидві події; зафіксувати в ADR-0005/README; lease/attempt LLM — P06), N2 (`no_facts` з `fallback_reason`
`llm_skipped_lane|stale|disabled`), N3 (спершу `normalization_version` vs `Normalizer.Version`: mismatch версії → retryable, mismatch хешу тієї самої
версії → не retryable), N4 (`versions` {normalization, rules, ruleset_id "default", catalog_policy}), N5 (adapter: `started_at` для ended, location text
навіть без place; нова версія adapter), N6 (history/live → різні runs → окремі stage_results; `ResetAsync` не чіпає stage_results — у docs), N7
(`Seed:SeedGazetteer=true` у fixture, `IndexProvider.RefreshAsync` явно), N8 (producer `{subscription}@{instance}`).

