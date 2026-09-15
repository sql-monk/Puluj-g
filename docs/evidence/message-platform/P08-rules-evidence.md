# P08 — версіоновані правила подій: evidence

Джерела: `Puluj.Processing.Tests` (parity/resolver/validator/evaluator, без БД), `Puluj.Integration.Tests` (`P08RulesetTests`, Testcontainers PostGIS
17 + PostGIS), `Puluj.Messaging.Tests` (`StageTests` S09/S10, PostGIS + RabbitMQ 4 quorum queues; підсумки у
[`messaging-crash-evidence.json`](messaging-crash-evidence.json) `P08-S09/S10`), `Puluj.Admin.Tests`, звіт якості
[`P08-quality-report.json`](P08-quality-report.json). TRX — `test-results/p08-*.trx`.

## Gate §8.3

| Gate | Тест | Підсумок |
|---|---|---|
| Seed parity (v1 ≡ `EventTypeMatcher`) | `RulesetParityTests`: seed-структура (24 правила, порядок → priority, `language *`, header-hint лише на `alert.air_raid.started`); корпус 35 cases + 11 конкурентних сегментів — факти field-by-field (EventType, target, hedged, count, launch, level, rules[], places, direction) під builtin і v1; **10 000** випадкових токен-послідовностей зі словника стемів/суфіксів/шуму × {ціль, без цілі} — покомпонентно (type, rule, launch) | ✅ 0 розбіжностей |
| Deterministic tie-break | `ResolverTests`: priority → найраніша позиція → `rule_code`; вікно; negative veto лише свого правила; language/source scope/`enabled`/kind disabled; header-hint = пропуск правила з продовженням перебору | ✅ |
| Kind без enum (review B1) | `ResolverTests`: `RuleParser` → `TargetBuilder` (`EventKindId` за кодом, `EventType=Unknown`) → `FactMapper` (`event_kind_code=fire.reported`, `category=incident`, evidence `ruleset_version/rule_code/rule_span`); рядок під заголовком «Шахеди:» не успадковує ціль; з ціллю legacy = `TargetObserved` | ✅ |
| Provenance на кожному результаті | S09: `parse.completed.versions.ruleset_id = v1`, evidence `ruleset_version=v1`, `rule_code=event:вибух`, `rule_code_version=1`, `rule_span`, `rule_id` без змін; `stage_results.versions`; legacy `parser_metadata.rulesetVersion/ruleCode/eventKindCode` (`P08RulesetTests`) | ✅ |
| Golden positives/negatives/ambiguity, P/R | `data/corpus/kinds.json` (22 сегменти: 9 позитивних, 5 негативних, 3 неоднозначних для 3 нових kinds + 4 legacy) через `RulesetEvaluator` | v1: accuracy 0.5 (нові kinds невідомі — очікувано, legacy 4/4); **v2-draft: accuracy 0.955**, `fire.reported` P=1/R=1, `infrastructure.outage` P=1/R=1, `air_defence.interception.reported` P=1/R=1; єдиний mismatch — `intercept-n02` (ambiguous: «не збито» → v1 `event:збит` активність) |
| Shadow disagreements не змінюють live | S10: той самий текст без shadow і з shadow v2 (fire priority 900 > `event:вибух` 810) — `facts` і `versions` байт-у-байт однакові, `outputs` рівні (крім `shadow`, id, duration); `event_kind_rule_shadow`: 1 рядок `impact.explosion.reported → fire.reported`, `outputs.shadow{live_version 1, shadow_version 2, disagreements 1}`; `ShadowReport` by kind | ✅ |
| Audit publish/rollback | `P08RulesetTests.Authoring_flow`: draft (копія v1) → `PUT rules` (rule_version: незмінне 1, змінений priority → 2, нове → 1; значення з payload ігнорується) → validate → shadow (другий shadow → 409 і DbUpdateException від partial unique) → **stop shadow** (→ draft, `shadow_stopped`) → shadow → publish (validate повторно; v1 → superseded; audit `created, rules_replaced×2, validated, shadow_started, shadow_stopped, shadow_started, validated, published`) → повторний publish → conflict; draft з конфліктом → `RulesetValidationException` з `conflicting_rules`, validation в audit; довжина `rule_code` > 128 → 409, не 500; rollback до v1 (`rolled_back{from_version 2}`, v2 rules незмінні) → повторний rollback → conflict | ✅ |
| Один active під гонкою | `Concurrent_publishes`: 4 паралельні publish двох draft → рівно 2 успіхи, 1 active, 1 published (advisory lock + `DeactivateAsync` окремим statement через partial unique) | ✅ |
| Pinned per job / poll / pin | `Index_provider_pins_active_then_pin_then_shadow…`: `Rules=v1`, після `StartShadow` → `ShadowRules=v2` без зміни live; `RulesetPin=99` → active лишається, pin на shadow-версію лише з `RulesetPinAllowDraft`; legacy pipeline пише `rulesetVersion/ruleCode/eventKindCode` | ✅ |
| Seed bootstrap ідемпотентний | `Seed_bootstraps_v1_once…`: v1 published/active від `seed`, 24 правила, повторний seed — noop | ✅ |
| Міграція Down/Up | `Migration_down_removes_rule_tables…`: 4 таблиці зникають/повертаються, seed знову створює v1 | ✅ |
| Admin API | `RulesetEndpointsTests`: без actor/reason → 400; 404/409/422/400 mapping | ✅ |

## Запуски

| Файл | Результат | Примітка |
|---|---|---|
| Processing (`p08-Puluj.Processing.Tests.trx`) | **120 / 0** | +15 (parity 3, resolver/validator/evaluator 12); `CorpusTests` без змін очікувань |
| `p08-stage-run1.trx` → run2 → run3 | 5 fail → 1 → **10 / 0** | run1: `$ref` схем залежав від порядку тестів (виправлено в `ContractSchemas.Options`); run2: S10 порівнював `effective_at` різних моментів (фіксований `publishedAt`) і shadow-правило з нижчим priority не давало розбіжності (900); run3: EF-проєкція GroupBy у record (виправлено) |
| `p08-Puluj.Messaging.Tests.trx` | **66 / 0 / 1** | повний проєкт (skip — P04-C06 W1c) |
| `p08-Puluj.Integration.Tests.trx` → `-run2` → `-run3` | 37/0/1 → 36/1 → **37 / 0 / 1** | +5 `P08RulesetTests` (перший прогін 3/2: rule_version брався з payload; activation в одному SaveChanges порушував partial unique); run2 після review-правок — очікування audit-списку (другий `PUT rules`) |
| `p08-Puluj.Admin.Tests.trx` | 62 / 0 | +6 |
| `p08-Puluj.Messaging.Contracts.Tests.trx` | 60 → **61** / 0 | fixture `parse.completed.json` з новими evidence-полями + invalid `evidence-bad-ruleset-version.json` (після review N5) |
| `p08-Puluj.Messaging.Tests-run2.trx` | **66 / 0 / 1** | повторно після review-правок |
| Api / Analytics | 30, 33 | регресія |

## Що не покрито (свідомо)

- Regex-патерни, `effective_from/to`, RBAC-ролі, UI адмінки — поза P08 (план N12/N4; ADR-0008).
- Shadow — лише stage `parser`; legacy `RawMessageProcessor` не порівнює (Q2).
- HTTP-рівень Admin (WebApplicationFactory) не тестується — сервіс і mapping статусів покриті окремо.
- Production-подібний обсяг shadow-рядків/cap не навантажувався (cap і `ROLLBACK TO SAVEPOINT` перевіряються кодом, не тестом); `ShadowEnabled=false`
  не тестується окремо (fixture-конфіг статичний) — S10 порівнює «без shadow-набору» з «shadow-набір».
- Корпус `kinds.json` — 22 сегменти (початковий набір для 3 kinds); розширення і стратифікований ручний review — перед canary нових kinds (P12/P13).
