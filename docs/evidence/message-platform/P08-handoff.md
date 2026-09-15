# P08 — версіоновані правила подій, resolver, preview/corpus/shadow, нові kinds

Task: [P08 / issue #9](https://github.com/sql-monk/Puluj-g/issues/9).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p08_review`): approve after fixes → B1 (stop shadow), B2 (corpus
в Admin-образі), N1–N13 виправлено/задокументовано → повторний прогін зелений. Rollout не виконувався.
Owner: Claude Code (Opus 5), агент `p08`. Reviewer: план — `p08_review` (approve after fixes; B1 control-flow за «правило спрацювало», B2 parity-деталі,
B3 shadow поза live-tx — внесено до старту, `P08-plan.md`); результат — `p08_review` (таблиця нижче).
Base commit: `ea92aba` (P06); результуючий commit — цей handoff комітиться разом із кодом (`P08-build-manifest.json` перелічує файли).

## Результат для споживача

- **Каталог правил у БД** (міграція `AddEventKindRules`): `event_kind_rulesets` (версія = identity; `draft | shadow | published | superseded`; один
  `is_active`, один `shadow` — partial unique), `event_kind_rules` (стеми в порядку у вікні, negative veto, priority, language, source scope, hints,
  `rule_version`), `event_kind_ruleset_audit`, `event_kind_rule_shadow`. Рядки не-draft версій незмінні.
- **Bootstrap v1** з `data/taxonomy/event-rules.json` — 24 фрази замороженого `EventTypeMatcher` (priority = порядок, `language *`); parity gate:
  корпус + конкурентні сегменти + 10 000 fuzz-послідовностей — 0 розбіжностей. Далі БД володіє правилами; seed добудовує перерваний bootstrap.
- **`EventKindResolver` + `RulesetIndex`**: snapshot pinned per job (один на повідомлення в stage `parser`, legacy `RawMessageProcessor`, `LlmParser`);
  tie-break `priority → найраніший збіг → rule_code`; header-hint; `enabled`/kind disabled (feature flag нових kinds) не спрацьовують.
- **Provenance**: `parse.completed.versions.ruleset_id = v{n}|builtin`; evidence (additive у контракті) `ruleset_version`, `rule_code`,
  `rule_code_version`, `rule_span`; legacy `parser_metadata.rulesetVersion/ruleCode/eventKindCode/eventKindPolicyVersion`.
- **Kind без legacy enum**: факт існує (control-flow парсера — «правило спрацювало»), `Target.EventKindId` за кодом, `EventType = Unknown`
  (`TargetObserved` з ціллю), `event_kind_code` з правила — без drift stage↔legacy.
- **Shadow mode** у stage `parser`: порівняння з набором у стані `shadow` поза транзакцією, розбіжності в `event_kind_rule_shadow` під `SAVEPOINT`
  (live-результат не залежить), cap `Parsing:ShadowMaxRowsPerHour`, `outputs.shadow{live_version, shadow_version, segments, disagreements, error}`.
- **Authoring API** `/api/admin/rulesets`: list/get, draft (копія active), `PUT rules` (rule_version успадковується/+1, ніколи з payload), validate
  (13 класів помилок + warnings), preview (`texts[]` ≤ 200, per-segment diff з baseline), corpus (`data/corpus/kinds.json` → P/R/F1 per kind), shadow /
  **shadow/stop**, shadow/report, publish (validator повторно; попередній active → superseded), rollback (активує старішу версію). Усі мутації — actor +
  reason (400), 404/409/422/400 за станом. Поширення publish/rollback — `Parsing:RulesetPollSeconds` (30 с); `Parsing:RulesetPin[AllowDraft]` для canary.
- **Нові kinds**: `data/corpus/kinds.json` (22 сегменти: позитивні/негативні/неоднозначні для `fire.reported`, `infrastructure.outage`,
  `air_defence.interception.reported` + legacy), `data/taxonomy/event-rules-v2-draft.json` — зразок draft; `P08-quality-report.json`: v2-draft accuracy
  0.955, P=1/R=1 для трьох kinds (1 ambiguous mismatch). Live не змінюється — publish v2 = окреме рішення після shadow/review.
- Admin: reference на `Puluj.Processing`, `IndexProvider` без hosted (TTL 10 хв, вказівники правил — на запит), `data/corpus/` в образі.

Docs: ADR-0008 (розділ «Правила розпізнавання (P08)»), ADR-0003 (`ruleset_id`), README контрактів («Runtime (P08)», schema evidence + fixtures),
`docs/README.md` (API, «Розширення без коду»), `fork-deployment.md`, plan §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (Testcontainers 4.15.0).
Усі під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p08-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Processing.Tests` (+15: parity 3, resolver/validator/evaluator 12; `PULUJ_EVIDENCE_DIRECTORY` → quality report) | 0 | **120/0/0** |
| `dotnet test tests/Puluj.Integration.Tests` run1 → run2 → run3 (+5 `P08RulesetTests`) | 0 → 1 → 0 | 37/0/1 → 36/1 (очікування audit після правок) → **37/0/1** |
| `dotnet test tests/Puluj.Messaging.Tests --filter StageTests` run1 → run3 (+S09, S10) | 1 → 0 | 5/5 → **10/0** |
| `dotnet test tests/Puluj.Messaging.Tests` (двічі, до і після review-правок) | 0 | **66/0/1** (skip — P04-C06 W1c) |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (+1 invalid fixture) | 0 | **61/0/0** |
| `dotnet test tests/Puluj.Admin.Tests` (+6) | 0 | 62/0/0 |
| `dotnet test tests/Puluj.Api.Tests` / `Puluj.Analytics.Tests` | 0 | 30, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |

## Evidence

[`P08-rules-evidence.md`](P08-rules-evidence.md), [`P08-quality-report.json`](P08-quality-report.json), `messaging-crash-evidence.json` (P08-S09/S10).

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | Shadow-стан не мав виходу, окрім publish | `StopShadowAsync` (shadow → draft, audit `shadow_stopped`), `POST /{v}/shadow/stop` | `Authoring_flow` |
| B2 | Admin-образ без `data/` → corpus «accuracy 1.0 на 0 кейсах» | `Dockerfile.admin` копіює `data/corpus/`; відсутній файл → 404, порожній корпус → 400 | код, docs |
| N1 | `eventKindPolicyVersion` зникав для rule-fired targets | `TargetBuilder` пише policy version разом із provenance | build |
| N2 | `rule_version` нового правила з payload | завжди 1 для правила без parent; parent → успадкування/+1 | `Authoring_flow` (payload 7 → 1) |
| N3 | Перерваний bootstrap лишав `builtin` назавжди | seeder перевіряє `IsActive`, добудовує seed-draft, warning для інших станів | код |
| N4 | `DbUpdateException` → 500 | перевірки довжин у сервісі, mapping `DbUpdateException` → 409 | `Authoring_flow` (rule_code 129 → conflict) |
| N5 | Fixtures без нових полів | `parse.completed.json` оновлено, invalid `evidence-bad-ruleset-version.json` | Contracts 61/0 |
| N6 | Прогалини тестів | pin (unknown/shadow/AllowDraft), partial unique shadow через DbUpdateException, `Assert.ThrowsAsync` замість `ContinueWith`; cap/`ShadowEnabled=false` — задокументовано як непокрите | Integration 37/0/1 |
| N7 | Sample у shadow report не «останні» | `GroupBy(raw) → max(shadow_id) DESC` | код |
| N8, N9, N13, Q1–Q4 | Pin на draft, евристика мови, docs-дрібниці, rule_version per lineage, validate з audit, обсяг корпусу, Down-тест на спільній БД | задокументовано (ADR-0008, README, план); validate тепер вимагає actor/reason | docs |
| N10, N11 | Мертвий код, затінення `ruleset` | прибрано `ActiveVersionAsync/ShadowVersionAsync`, `States`, `IsActive`, `RuleName`, `LoadedRule.EventKindId`, `BuiltinRulesetId`; `rulesetOptions` | build |
| N12 | Baseline preview відставав на TTL | `AdminIndexes` перечитує вказівники правил на кожен запит | код |

Reviewer підтвердив: parity за конструкцією + тести; B1–B3 плану втілено; один snapshot на job у stage і legacy; shadow під savepoint без впливу на live;
інваріанти сервісу під advisory lock + partial unique (гонка доведена); міграція ↔ конфігурація, Down/Up; контракти additive; docs узгоджені з кодом.

## Відомі обмеження / невиконані перевірки

- Regex-патерни, `effective_from/to`, RBAC-ролі, UI адмінки, shadow у legacy loop, pin на рівні run — поза P08 (ADR-0008).
- `language`-scope спирається на евристику мови — рекомендовано `*`; source scope у preview/corpus лише з `sourceCode`.
- Cap shadow-рядків і `ROLLBACK TO SAVEPOINT` — перевірені кодом, не тестом; HTTP-рівень Admin без WebApplicationFactory.
- Корпус нових kinds — початковий (22 сегменти); стратифікований ручний review і розширення — перед canary (P12/P13).
- Project card — токен без scope; статус у issue/§17.

## Rollout / rollback / input ownership

Default deploy: міграція `AddEventKindRules` + bootstrap v1 при старті Worker (`Seed__SeedEventKindRules`, default true) — live-поведінка ідентична
(parity). Нові правила — лише через Admin API (draft → validate → preview/corpus → shadow → publish); rollback версії — `POST /rulesets/rollback`;
rollback міграції — `Down` (resolver → builtin). Ownership: `Puluj.Infrastructure/Rules` (сервіс/валідатор/DTO), `Puluj.Processing/Rules`
(index/resolver/evaluator), `Puluj.Admin/Endpoints/RulesetEndpoints.cs`, seed-файли й корпус (P08); `EventTypeMatcher` — frozen.

## Чекбокси issue #9

- [x] Ruleset pinned per job (snapshot на повідомлення, `versions.ruleset_id`, evidence provenance), golden parity (корпус + fuzz) й нові cases
  (`kinds.json`, evaluator), audit publish/rollback (сервіс + тести), оцінка якості (`P08-quality-report.json`)
- [x] Тести на актуальній збірці з реальними PostGIS + RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (evidence additive + fixtures), міграція/rollback, конфігурація (`Parsing:*`, `Seed:SeedEventKindRules`, Dockerfile.admin), документація оновлені

## Наступний task

**P09** (issue #10) — track/alert writers з `observations.recorded`, locks/revisions, watchdog commands, SQL trigger ownership (§7).
