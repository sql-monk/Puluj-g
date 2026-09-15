# P08 — версіоновані правила подій, resolver, preview/corpus/shadow, нові kinds — план

Task: [P08 / issue #9](https://github.com/sql-monk/Puluj-g/issues/9). Залежності: P05 (стадії parser, `versions.rules/ruleset_id`), P07 (`event_kinds`,
`EventKindLegacyMap`, `EventKindIndex`) — обидві done. Base commit: `ea92aba` (P06).

## Стан коду на старті (звірено)

- Розпізнавання виду події — статичний `EventTypeMatcher.Phrases` (24 фрази «стеми в порядку, вікно 5 токенів» → legacy `EventType`), спеціальні
  правила: `AirRaidAlert` з ціллю без рівня → header, не факт; launch — окремий прапорець `IsLaunch` (стеми `пуск|зліт|злет|запуск|взлет`); fallback
  `TargetObserved`/`Unknown`. `RuleParser` (`rule-0.1`) викликає його на кожен сегмент; `ParsedFact.Rules` містить `event:<стеми>`.
- `ParserHandler` пише `versions {normalization, rules: rule-0.1, ruleset_id: "default", catalog_policy}` у `stage_results` і `parse.completed`;
  `FactMapper.Evidence` — `rule_id` = join rules, `rule_version` = `rule-0.1`, `rules[]`. Legacy `RawMessageProcessor` — той самий `RuleParser`.
- `event_kinds` (P07): 16 кодів, `EventKindIndex.Stamp` ставить `Target.EventKindId` **лише** з legacy enum; kinds без enum → `Unknown`.
- Корпус `data/corpus/cases.json` (35 golden cases, поля `event` = legacy enum) — `CorpusTests` на `RuleParser`. Admin — bearer-token фільтр без
  ролей; `Puluj.Admin` не посилається на `Puluj.Processing`.
- Немає: `event_kind_rules`, версій ruleset, resolver'а з БД, preview/shadow, authoring API.

## Цільова модель (§8.3)

**Ruleset = immutable версія набору правил.** Один `published` набір є `active` для нових jobs; draft — редагується; shadow — порівнюється на живому
трафіку без впливу на результат; rollback = активація старішої published версії (нова audit-подія, не зміна збережених результатів).

### D1. Схема (additive, міграція `AddEventKindRules`)

- `event_kind_rulesets`: `ruleset_id serial PK`, `version int unique`, `state text` (`draft | shadow | published | superseded | retired`),
  `is_active bool` (partial unique `WHERE is_active`), `parent_version int?`, `created_at`, `created_by`, `reason`, `published_at?`, `published_by?`,
  `notes jsonb?`. Інваріанти в `RulesetService`, не тригерами.
- `event_kind_rules`: `rule_id bigserial PK`, `ruleset_id FK cascade`, `rule_code text` (стабільний ключ правила між версіями, напр. `event.alert.ended.vidbiy_tryvoh`),
  `event_kind_id FK RESTRICT`, `language text` (`uk | ru | *`), `source_scope jsonb?` (`{"sources":[codes]}`; null = усі), `positive_patterns jsonb`
  (масив `{type: stems, stems: [...], window: 5}` або `{type: regex, pattern}`), `negative_patterns jsonb`, `priority int`, `extraction_hints jsonb?`
  (`{requires_target: bool?, header_if_target_without_level: bool?, sets_launch: bool?}`), `confidence_modifier numeric(4,3)`, `rule_version int`,
  `effective_from/to timestamptz?`, `enabled bool`, `actor`, `reason`, `created_at`; unique `(ruleset_id, rule_code)`.
- `event_kind_ruleset_audit`: `audit_id`, `version`, `action` (`created | rules_replaced | validated | shadow_started | published | rolled_back | retired`),
  `actor`, `reason`, `at`, `details jsonb`.
- `event_kind_rule_shadow`: `shadow_id`, `raw_message_id`, `run_id`, `live_version`, `shadow_version`, `segment_index`, `live_kind`, `shadow_kind`,
  `live_rule`, `shadow_rule`, `created_at`; індекс `(shadow_version, created_at)`; лише розбіжності.
- `Down` симетричний.

### D2. Seed і parity

- `data/taxonomy/event-rules.json` (`rulesetVersion 1`, `policyVersion`): переносить усі 24 фрази `EventTypeMatcher` як stems-правила з `priority`
  за порядком у матчері (перше — найвищий), `rule_code` детермінований із стемів; hint `header_if_target_without_level` на правилах `alert.air_raid.started`;
  правило `target.launch`? — **ні**: launch лишається прапорцем факту (`sets_launch` hint на окремому правилі без kind не потрібен), parity зберігається.
- `EventKindRuleSeeder` (Order 40, після `EventKindSeeder`): якщо жодного ruleset немає — створює v1 `published` + `is_active` з actor `seed`; якщо є —
  нічого не змінює (БД володіє правилами; seed лише bootstrap). Draft v2 із seed-файлу **не** створюється автоматично — нові kinds додаються через
  authoring API/тести (`data/taxonomy/event-rules-v2-draft.json` як зразок для preview/corpus).
- Parity gate: `EventKindResolver(v1).Resolve == EventTypeMatcher.Match` на корпусі + синтетичному наборі (усі фрази, комбінації з ціллю/рівнем).

### D3. Resolver і pinned ruleset

- `RulesetIndex` (immutable snapshot: version, compiled rules; `Empty` = builtin `EventTypeMatcher`, `ruleset_id = "builtin"`). `IIndexes.Rules`,
  `IndexProvider` вантажить активний ruleset разом з іншими індексами (refresh 10 хв; `Parsing:RulesetPin` = версія для canary override).
- `EventKindResolver.Resolve(segment, hasTarget, hasLevel, ctx)` → `Resolution(kindCode, legacyType, ruleCode, ruleVersion, spans, launch)`:
  фільтр enabled/effective window/language/source scope; negative patterns — veto правила на сегменті; tie-break детермінований: `priority DESC`,
  потім найраніша позиція збігу, потім `rule_code ASC`. Regex: `RegexOptions.NonBacktracking | CultureInvariant`, timeout 100 мс (timeout → правило
  пропущено, `fallback_reason: regex_timeout`, warning).
- `RuleParser` бере `indexes.Rules` один раз на повідомлення (snapshot); `ParsedFact` + `EventKindCode`, `RuleCode`, `RuleVersion`, `Spans`;
  `EventType` = legacy через `EventKindLegacyMap` (kind без enum → `Unknown`, `EventKindId` за кодом). `TargetBuilder`/`EventKindIndex.Stamp`: спершу
  `EventKindCode` факту, потім legacy. `ParserMetadata` (legacy `targets`) + `rulesetVersion`, `ruleCode`.
- `ParserHandler`: `versions.ruleset_id = "v{n}"` (або `builtin`), `versions.rules = rule-0.2`; evidence `rule_id = rule_code`, `rule_version = "v{n}/{rule_version}"`,
  `rules[]` як раніше (+ `span` з першого збігу). Контракти — additive (усі поля вже в `$defs/evidence`). Один snapshot на job = «ruleset pinned per job».

### D4. Shadow mode

- `ParserHandler`: якщо існує ruleset у state `shadow` (snapshot `IndexProvider.ShadowRules`) і `Parsing:ShadowEnabled` (default true) — після live-парсингу
  запускає resolver з shadow-набором на тих самих сегментах; розбіжності (kind або rule) → `event_kind_rule_shadow` у тій самій tx стадії (bounded: лише
  сегменти з різницею), `stage_results.outputs.shadow = {version, segments, disagreements}`. Live результат не змінюється.
- `GET /api/admin/rulesets/{v}/shadow/report`: агрегати (за парою live→shadow kind, за правилом, приклади raw ids), вікно часу.

### D5. Authoring API (Admin, JSON; UI — окремо/P13)

`Puluj.Admin` → reference `Puluj.Processing` (без циклу). Усі мутації вимагають `actor` і `reason` (400 без них), пишуть audit; RBAC = наявний admin token
(ролі — поза P08, зафіксувати).

| Endpoint | Дія |
|---|---|
| `GET /rulesets`, `GET /rulesets/{v}` | список версій зі станом/audit; правила версії |
| `POST /rulesets` `{parentVersion?, actor, reason}` | draft: копія правил parent (default — active) |
| `PUT /rulesets/{v}/rules` | замінити правила draft (лише `draft`); валідація структури |
| `POST /rulesets/{v}/validate` | `RulesetValidator`: regex compile/timeout на зразках, порожні стеми, word boundaries (стем ≥ 2 символи, без пробілів), Unicode NFC, невідомий kind/мова/source, negation без positive, дублікати `rule_code`, конфліктні правила (однакові patterns → різні kinds) без різного priority, source scope невідомі коди; звіт `{ok, errors[], warnings[]}` + audit `validated` |
| `POST /rulesets/{v}/preview` `{texts[]? \| sample: {sourceCodes?, since?, limit≤200}}` | розбір `RuleParser` із draft і з active; per-segment diff |
| `POST /rulesets/{v}/corpus` | `RulesetEvaluator` на `data/corpus/cases.json` (+ `data/corpus/kinds.json`): per-kind TP/FP/FN, precision/recall/F1, список розбіжностей |
| `POST /rulesets/{v}/shadow` | `draft → shadow` (один shadow одночасно) |
| `POST /rulesets/{v}/publish` | вимагає успішний validate у audit; `shadow|draft → published + active`, попередній active → `superseded`; audit |
| `POST /rulesets/rollback {version, actor, reason}` | активує старішу `published/superseded` версію; поточна → `superseded`; audit `rolled_back` |

Логіка — у `Puluj.Processing/Rules/RulesetService.cs` (Infrastructure-level, тестується integration без HTTP); endpoints — тонкі.

### D6. Нові kinds (§8.2/8.3) — corpus → shadow

- `data/corpus/kinds.json`: для кожного нового kind позитивні/негативні/неоднозначні приклади з очікуваним `kind` (або `null`): `fire.reported`,
  `damage.reported`, `infrastructure.outage`, `civil_defence.notice`, `evacuation.notice`, `air_defence.interception.reported`; існуючі 35 cases
  оцінюються через `event → kind` mapping. `impact.confirmed`, `casualties.reported` — без правил (policy/evidence, §8.2).
- `data/taxonomy/event-rules-v2-draft.json`: правила нових kinds з negative patterns (напр. `fire.reported` без «пожежн(а) небезпека»; `infrastructure.outage`
  «знеструм|відключен.*світл» з негативом «відновлен»); `air_defence.interception.reported` — `збит` **не** переноситься з `air_defence.activity`
  (parity), а окреме правило вищого priority у draft — рішення для shadow/огляду, не для live.
- Оцінка якості: `P08-quality-report.json` (v1 і v2-draft на corpus+kinds: precision/recall per kind) — evidence, не gate для live.

### D7. Тести

- Processing (unit): parity v1 ↔ `EventTypeMatcher` (корпус + синтетика); tie-break; negative veto; language/source scope/effective window; regex
  timeout → skip; snapshot незмінний посеред повідомлення; `RuleParser` provenance; kind без enum → `Unknown` + `EventKindId`; validator (кожна
  перевірка); evaluator (P/R на відомому наборі); нові kinds golden (`kinds.json`) на v2-draft.
- Integration (PostGIS): міграція Up/Down; seeder bootstrap v1 (ідемпотентно; не чіпає існуючі); `IndexProvider` вантажить active/shadow + pin;
  `RulesetService`: draft → rules → validate → shadow → publish → rollback з audit-рядками й інваріантом «один active»; legacy pipeline пише
  `ParserMetadata.rulesetVersion` і `event_kind_id` для kind без enum.
- Messaging (Stage): `parse.completed.versions.ruleset_id = v1`, evidence `rule_id/rule_version/span`; shadow-розбіжності записані, live факти незмінні,
  `outputs.shadow`.
- Admin: validator/evaluator unit; endpoint-фільтр `actor/reason` (unit на handler-функції).
- Corpus/evidence: `P08-quality-report.json`, `P08-rules-evidence.md`, TRX `p08-*`.

### D8. Документація

ADR-0008 (§ rules: модель, стани, tie-break, provenance, shadow), ADR-0003 (versions: `ruleset_id` формат), контракти README («Runtime (P08)»:
evidence поля), `docs/README.md` (admin API rulesets), `fork-deployment.md` (`Parsing:RulesetPin`, `ShadowEnabled`, seed bootstrap), plan §17,
handoff §16.4, manifest.

## Порядок виконання

1. Схема + entity + міграція + seeder + seed-файл. 2. `RulesetIndex`/`EventKindResolver`/`RuleParser` + parity unit. 3. Провenance у ParserHandler/FactMapper/
legacy metadata + Stage tests. 4. `RulesetService` + validator + evaluator + integration. 5. Shadow. 6. Admin endpoints. 7. kinds.json + v2 draft + quality report.
8. Docs, evidence, review, handoff, manifest, commit.

## Ризики / рішення

- Parity: будь-яка зміна live-поведінки заборонена в v1 (gate — unit parity + `CorpusTests` без змін очікувань).
- `Puluj.Admin` → `Puluj.Processing`: Admin отримує `Normalizer`/`RuleParser` для preview; `IndexProvider` не запускається як hosted service в Admin —
  snapshots вантажаться на запит (`IndexProvider.Load*Async` статичні).
- Regex у правилах: `NonBacktracking` не підтримує lookaround/backreferences — validator повідомляє; timeout 100 мс.
- Один shadow одночасно; shadow-рядки bounded розбіжностями; retention — разом із stage_results (P16).
- RBAC/ролі для authoring — поза P08 (P12/P13); actor — з body, audit фіксує.

## Незалежне review плану — p08_review: approve after fixes → внесено

| # | Finding | Рішення в плані |
|---|---|---|
| B1 | Control-flow `RuleParser` ключується на `EventType.Unknown/TargetObserved`; kind без enum губиться/успадковує ціль; `FactMapper` бере kind з legacy enum → drift stage↔legacy | D3: `RuleParser` працює з `Resolution?` (правило спрацювало/ні) і `ParsedFact.EventKindCode`; header/carry/inherit-логіка — за `EventKindCode ∈ {target.observed, null}`; `FactMapper.ToFact`: `event_kind_code = fact.EventKindCode ?? LegacyMap`; unit «kind без enum» через `RuleParser` + `FactMapper` + `TargetBuilder` |
| B2 | Parity-деталі: header-veto = пропуск правила з продовженням перебору; language `*`/scope null у v1; launch-детектор `StartsWith` окремо; `StemMatch(exact:false)` + `MatchesWithin(window)`; пріоритети v1 = порядок фраз; конкурентні сегменти в parity-наборі | D2/D3 зафіксовано саме так; `LaunchDetector` у `RuleParser`; parity — корпус + синтетика + fuzz 10k (N10) покомпонентно (type, rule, launch) |
| B3 | Shadow у live-tx: збій shadow валить live; дублі при redelivery; unbounded | D4: обчислення у `PrepareAsync` у try/catch → `outputs.shadow.error`; insert лише при `stageId != null` під `SAVEPOINT` з rollback-to-savepoint; cap `Parsing:ShadowMaxRowsPerHour` (default 5000) на версію — далі лише лічильник |
| N1 | Snapshot ruleset читається двічі | `RuleParser.Parse(normalized, ctx, ruleset)` → `ParseResult{Facts, RulesetVersion}`; handler/legacy беруть один snapshot |
| N2 | Validate stale після PUT | `publish` сам виконує validator (audit `validated` + `published`) |
| N3 | Лаг 10 хв | окремий poll `event_kind_rulesets` кожні 30 с (легкий `max(version) where is_active`, refresh рулесету при зміні); задокументовано |
| N4 | Іммутабельність/`enabled`/effective/confidence_modifier | рядки не-draft версій ніколи не оновлюються; `effective_from/to` **прибрано** з P08; `confidence_modifier` зберігається, не застосовується (v1 = 0) — задокументовано |
| N5 | `rule_version` | на `PUT rules`: успадковується з parent, якщо (kind, patterns, negatives, hints, language, scope, priority) незмінні, інакше parent+1; нове правило = 1 |
| N6 | `span` semantics | `span` без змін; додано additive `ruleset_version`, `rule_code`, `rule_span{start,end}` у `$defs/evidence` (+ README, fixtures) |
| N7 | `fallback_reason` | пропуски правил → `outputs.rules_skipped[]`, не `fallback_reason` |
| N8 | Admin preview/індекси; розташування сервісу | Admin: `IndexProvider` singleton без hosted, lazy refresh з TTL 10 хв; entity — Domain, EF — Infrastructure, `RulesetService`/`RulesetValidator` — **Infrastructure** (`Puluj.Infrastructure/Rules`), resolver/evaluator/preview — Processing; Admin → Processing reference |
| N9 | Feature flag нових kinds | resolver пропускає правила з `event_kinds.enabled=false` (snapshot); validator — warning |
| N10 | Fuzz parity | додано (10k випадкових токен-послідовностей зі стемів+шуму, порівняння type/rule/launch) |
| N11 | Тести, що не доводять | shadow: порівняння facts/outputs з `ShadowEnabled=false`; regex — **вилучено з P08** (N12); негативні кейси сервісу (409 на published, publish без validate → validator виконується, rollback на draft → 409, конкурентні publish → один active), partial unique `WHERE state='shadow'` |
| N12 | Звуження | patterns — лише `stems` (regex — поза P08, validator відхиляє невідомий type); preview — лише `texts[]`; v2-draft — 3 kinds з корпусом (`fire.reported`, `infrastructure.outage`, `air_defence.interception.reported`); shadow report — мінімальні агрегати |
| N13 | Схема | `version` = PK; `state` check; shadow `raw_message_id` без FK; `EventTypeMatcher` — frozen (коментар + тест parity як єдине джерело) |
| Q1 | Pin per run | per message (delivery) у P08; run-level pin — P14 |
| Q2 | Legacy loop без shadow | так, shadow — лише stage parser (документовано) |
| Q3 | Pin на неіснуючу/неопубліковану версію | warning + active; draft/shadow дозволені для canary лише через явний `Parsing:RulesetPinAllowDraft=true` |
| Q4 | `retired` | прибрано |
| Q5 | `rules[]`/`ParserMetadata.rules` | без змін (`event:<стеми>`); `rule_code`/`rulesetVersion` — додаткові поля |
| Q6 | `stage_version` | лишається `rule-0.1`; `ruleset_id` несе версію |
