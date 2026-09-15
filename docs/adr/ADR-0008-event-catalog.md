# ADR-0008 — DB-каталог видів подій, legacy mapping і сумісна міграція

Статус: **proposed → implemented локально** (P07, 2026-09-15; приймання після незалежного review). Вимоги: plan §8.1–8.2, §16.2 п.3, 5, 6, 7.
Machine-readable: `data/taxonomy/event-kinds.json`; контракт кодів — `contracts/messaging/schemas/common.schema.json` (`eventKindCode`, `observationCategory`).

## Контекст

Вид факту сьогодні — `Target.EventType` (enum: `Unknown, TargetObserved, AirRaidAlert, AlertCancelled, TargetCancelled,
ExplosionReport, AirDefenseActivity`), зашитий у код і в SQL-функції (`puluj_on_target_insert`, `puluj_link_target`
фільтрують `event_type = 1`). Нові види (пожежа, наслідки, цивільні повідомлення) потребують словника з стабільними
кодами, map policy і версією без правки enum і без масового перейменування API.

## Рішення

1. **Таблиця `event_kinds`** (§8.2): `event_kind_id`, unique `code`, `name_uk`, `category` (**text**: `target|alert|incident|info` —
   рядок контракту `observationCategory`, не число), `default_severity`, `state_model`, `requires_location_for_map`,
   `render_mode`, `map_color`, `map_icon`, `map_lifetime` (interval, лише presentation), `creates_incident`, `enabled`,
   `map_visible`, `sort_order`, `dedup_policy`/`presentation`/`metadata` jsonb, `policy_version`.
2. **Seed** `data/taxonomy/event-kinds.json` (`policyVersion`, `kinds[]`): 15 кодів §8.2 + `unknown.unclassified` (явний outcome,
   `info`, не показується на мапі). `casualties.reported` **не** seed-иться (окреме розширення з правилами щодо персональних
   даних). `EventKindSeeder` (Order 30): додає відсутні коди; для існуючих оновлює presentation/policy лише коли
   `policyVersion` файлу більший за `policy_version` рядка; `code` не змінює, рядки не видаляє, admin-owned `enabled`
   зберігає. `Validate` перевіряє pattern коду, категорію, унікальність і паритет із legacy map.
3. **Legacy mapping** (`Puluj.Domain.EventKindLegacyMap`, одна таблиця для C#, seed `metadata.legacyEventType`,
   SQL backfill): `Unknown→unknown.unclassified`, `TargetObserved→target.observed`, `AirRaidAlert→alert.air_raid.started`,
   `AlertCancelled→alert.air_raid.ended`, `TargetCancelled→target.cancelled`, `ExplosionReport→impact.explosion.reported`,
   `AirDefenseActivity→air_defence.activity`. Kind без enum-члена (`fire.reported`, …) → legacy колонка отримує
   `EventType.Unknown` (`LegacyOrFallback`) — задокументований fallback, без вигаданого значення.
4. **Міграція `AddEventKinds`** — additive: таблиця, `targets.event_kind_id int NULL`, FK `RESTRICT`, індекс
   `(event_kind_id, observed_at DESC)`. Без NOT NULL і без зміни `event_type`. `Down` знімає FK/індекс/колонку/таблицю.
5. **Writer**: `RawMessageProcessor` бере один snapshot `IIndexes.EventKinds` на повідомлення і стемпить `EventKindId`
   на кожен target (текстовий і structured шлях) **поряд** із legacy `EventType`, у тій самій транзакції під Store;
   `ParserMetadata.eventKindPolicyVersion` цитує policy version snapshot-а. Порожній каталог (seed ще не виконано) →
   `NULL` + одноразовий warning на процес, не здогадка; `IndexProvider` перечитує каталог кожні 15 s, поки він порожній;
   backfill дозаповнить.
6. **Backfill** `EventKindBackfill` (ISeeder, Order 90; та `scripts/backfill-event-kinds.sql`): лише `event_kind_id IS NULL`,
   батчі по `target_id` (5 000), кожен батч — окрема транзакція під `pg_advisory_xact_lock(Store)` (правило P00 для
   writers derived rows), ідемпотентний, rerunnable; звіт `total/nullBefore/updated/batches/nullAfter/unresolvedByEventType/perKind/coverage`.
   Значення enum без mapping лишаються `NULL` і рапортуються, не вгадуються; діапазон `target_id` для батчів береться
   лише з рядків, які мапа може розв'язати, тому «вічний» unresolved рядок не розширює скан на кожен старт.
   `scripts/backfill-event-kinds.sql` виконує **один** батч за виклик — оператор повторює до `updated 0`.
   Індекс `(event_kind_id, observed_at DESC)` створюється всередині EF-транзакції міграції — свідомий tradeoff для
   поточного обсягу `targets`; concurrent build поза транзакцією — якщо production-shaped rehearsal покаже потребу (§8.2).
7. **Compatibility adapter**: `TargetDto.EventType` лишається; додано `TargetDto.EventKindCode` (trailing optional) і
   `EventKindDto` + `GET /api/event-kinds` (лише `enabled`). Web/analytics не змінюються.

## SQL-тригери й другий writer scope

`trg_targets_insert_kinematics` — `AFTER INSERT`; `trg_targets_duplicate` — `AFTER UPDATE OF duplicate_of_target_id`.
Backfill-UPDATE колонки `event_kind_id` не запускає жодного з них; функції й далі читають `event_type = 1`, тому legacy
enum лишається обов'язковим полем запису до кінця compatibility window. Backfill не є другим власником derived
state: він пише лише `event_kind_id` і бере Store, як інші maintenance writers (`fix-text-alert-ends.sql`).

## Альтернативи

- Розширювати enum `EventType` — не дає map policy/presentation у БД, потребує міграцій коду на кожен вид; відкинуто.
- Category як int enum — розходиться з контрактом (`observationCategory` рядки) і ускладнює SQL/analytics; відкинуто.
- Backfill усередині EF-міграції — довга транзакція на великій таблиці, без батчів і без Store; відкинуто (§8.2).
- NOT NULL одразу — ламає старі writers/партії; посилення constraints — окремий крок після 100% coverage.

## Наслідки

- Кожен новий вид = рядок у seed (+ `policyVersion`), без коду; правила розпізнавання для нього — P08.
- `event_kind_id` NULL допустимий у compatibility window; readers мають це терпіти (DTO `eventKindCode: null`).
- Рядок `unknown.unclassified` дозволяє рахувати «нерозпізнано» як явний outcome у analytics (P15).
- Rollback: зупинити writers нової збірки → `Down` міграції (рядки каталогу зникають разом із таблицею) → стара збірка.
  Повторний rollout = міграція → seed → backfill (порядок у `DatabaseInitializer` це гарантує).

## Виміряно (Testcontainers PostGIS, `P07-backfill-report.json`)

50 000 synthetic targets: backfill 10 батчів по 5 000 за ~1.7 s, coverage 1.0, 0 unresolved. `ANALYZE` після backfill,
потім `EXPLAIN (ANALYZE, BUFFERS)`: map-запит `WHERE event_kind_id = ? ORDER BY observed_at DESC LIMIT 100` — Index Scan
`ix_targets_event_kind_id_observed_at` (est. 20 102 rows, 100 повернуто, 7 shared hits, 0.07 ms); `count(*) WHERE
event_kind_id IS NULL` — Index Only Scan того самого індексу (50 000 heap fetches без visibility map, 3.7 ms).
Це не production-вимір; індекс лишається кандидатом до перевірки на production-shaped даних (P11/P16).

## Правила розпізнавання (P08, §8.3)

- **Модель.** `event_kind_rulesets` (`version` PK, `state` ∈ `draft | shadow | published | superseded`, `is_active` — partial unique, один shadow —
  partial unique, `parent_version`, actor/reason, `published_at/by`), `event_kind_rules` (`rule_code` стабільний між версіями, `event_kind_id` FK RESTRICT,
  `language` `uk|ru|en|*`, `source_scope {sources[]}`, `positive_patterns`/`negative_patterns` — масиви `{type: stems, stems[], window}`,
  `priority`, `extraction_hints {header_if_target_without_level}`, `confidence_modifier` (зберігається, **не застосовується**), `rule_version`,
  `enabled`, actor/reason), `event_kind_ruleset_audit` (`created | rules_replaced | validated | shadow_started | shadow_stopped | published | rolled_back`),
  `event_kind_rule_shadow` (лише розбіжності; без FK на raw). Рядки не-draft версій ніколи не оновлюються — будь-яка зміна = новий draft.
- **Семантика збігу** — та сама, що у замороженого `EventTypeMatcher`: стеми в порядку, `StemMatch(exact:false)` (≤ 4 літери суфікса), останній стем
  у вікні `window` токенів від першого; `negative_patterns` — veto правила на сегменті; hint `header_if_target_without_level` — правило пропускається,
  якщо сегмент називає ціль без рівня тривоги (перебір триває). Tie-break детермінований: `priority DESC` → найраніша позиція збігу → `rule_code ASC`.
  Правила з `enabled=false` або з вимкненим kind (`event_kinds.enabled=false` — feature flag нових kinds) не спрацьовують. `language` (`uk|ru|en|*`)
  спирається на евристику `Normalizer.DetectLanguage` (кириличні маркери), тож для правил рекомендовано `*`; `source_scope` у preview/corpus
  діє лише з переданим `sourceCode`.
- **Pinned per job.** `IndexProvider.Rules` — immutable snapshot активної (або `Parsing:RulesetPin`) версії; `RuleParser.Parse(…, ruleset)` бере його
  один раз на повідомлення; `stage_results.versions.ruleset_id = v{n}` (`builtin` до seed), evidence `ruleset_version`, `rule_code`, `rule_code_version`,
  `rule_span` (additive), legacy `parser_metadata.rulesetVersion/ruleCode/eventKindCode`. Publish/rollback поширюються за `Parsing:RulesetPollSeconds` (30 с).
- **Kind без enum** (`fire.reported`, …): факт існує (control-flow парсера — «правило спрацювало», не legacy enum), `Target.EventKindId` за кодом,
  `EventType = Unknown` (з ціллю — `TargetObserved`), `event_kind_code` у `parse.completed` — з правила. Live це стосується лише після publish версії з такими правилами.
- **Bootstrap.** `data/taxonomy/event-rules.json` → v1 (24 фрази матчера, priority = порядок, `language *`) один раз; далі БД володіє правилами.
  Parity gate: корпус + конкурентні сегменти + 10k fuzz (`RulesetParityTests`). `EventTypeMatcher` — frozen.
- **Authoring** (`/api/admin/rulesets`): draft (копія active) → `PUT rules` (rule_version: без змін поведінки — успадковується, інакше +1) → validate →
  preview (`texts[]`, ≤ 200) / corpus (`data/corpus/kinds.json`, P/R/F1 per kind) → shadow (один; розбіжності в `event_kind_rule_shadow`, cap
  `Parsing:ShadowMaxRowsPerHour`, insert під savepoint — live не постраждає) → publish (validator виконується знову; попередній active → superseded)
  → rollback (активує старішу published/superseded; збережені результати не змінюються). Shadow можна зняти (`POST /{v}/shadow/stop` → draft,
  audit `shadow_stopped`) — єдиний вихід, окрім publish. Усі мутації, включно з validate, — actor + reason (400 без них). `rule_version`
  рахується відносно **parent**-версії (lineage), не глобально: evidence цитує пару `ruleset_version` + `rule_code_version`. Pin на draft
  (`RulesetPinAllowDraft`) перечитується лише при зміні номера версії — після `PUT rules` потрібен рестарт репліки.
- **Поза P08:** regex-патерни, `effective_from/to`, RBAC-ролі, UI адмінки, shadow у legacy `RawMessageProcessor`, pin на рівні run (P14).

## Відкрите

| Питання | Задача |
|---|---|
| Incidents (`creates_incident`, `state_model`) | P10 |
| Catalog у API/UI: legend/filters з `render_mode`/`map_color`, посилення constraints (NOT NULL) після coverage | P11/P12 |
| Analytics по `event_kind_id`, `unknown.unclassified` як явний outcome | P15 |
| `casualties.reported` — правила персональних даних | окреме рішення |
