# Контракти шини повідомлень (P01)

Machine-readable частина ADR-0002…0005 ([`docs/adr/`](../../docs/adr/README.md)). Статус: **accepted** (P02 spike підтвердив topology на RabbitMQ 4.3;
P03 runtime). `topology.json` **вбудовується** у `Puluj.Infrastructure` як embedded resource (`Puluj.Infrastructure.csproj`), тож runtime
(`TopologyRegistry`, `TopologyRegistrar`, `TopologyDeclarer`) читає той самий файл, що й контрактні тести; `tests/Puluj.Messaging.Tests`
перевіряє, що embedded копія збігається з файлом. Поточна версія — **5** (P03: `archive`; P04: `raw-writer`; P05: `normalizer`, `parser`; P06:
`finalizer`, `llm-worker` → `active`). Зміни — лише разом із тестами
`tests/Puluj.Messaging.Contracts.Tests` і, за потреби, новим `topology_version`.

| Файл | Що це |
|---|---|
| `topology.json` | Registry очікуваних підписок: events (kind, producer, schema, required/optional/conditional subscriptions), subscriptions (bindings, lanes, emits, queue_policy, idempotency, owner_task, status), producer roles, queue policies, bridge на legacy NOTIFY |
| `completion-manifest.json` | Terminal outcomes аналізу й delivery, доменні гілки за категорією observation, workflow stages |
| `schemas/envelope.schema.json` | JSON Schema 2020-12 envelope (plan §5.1) з умовними required за scope події |
| `schemas/common.schema.json` | Спільні `$defs` (observation, location, error, versions, fencing token) |
| `schemas/events/*.schema.json` | Payload кожного event type |
| `asyncapi.yaml` | AsyncAPI 3.0: channels/operations/bindings; **генерується** з `topology.json` |
| `fixtures/valid/*.json` | Повний приклад кожного event type + identity/lane/additive/payload_ref варіанти |
| `fixtures/invalid/*.json` | `{reason, expected_error_contains, event}` — має падати валідацію |
| `fixtures/sequences/*.json` | Republish того самого event_id; analytics out-of-order |
| `fixtures/identity-cases.json` | Legacy `source_message_id` → `(source_message_key, source_revision)` для Telegram/alerts.in.ua |
| `fixtures/compatibility.json` | Правило `major_equal`, матриця compatible/quarantine, breaking examples |
| `tools/gen-asyncapi.py` | Генератор `asyncapi.yaml` з `topology.json` |

## Команди

```powershell
dotnet test tests/Puluj.Messaging.Contracts.Tests/Puluj.Messaging.Contracts.Tests.csproj
python contracts/messaging/tools/gen-asyncapi.py   # після зміни topology.json
```

## Як додати event type

1. `schemas/events/{type}.schema.json` з `$id` `https://puluj.local/contracts/messaging/schemas/events/{type}.schema.json`;
   без `additionalProperties: false` на верхньому рівні (ADR-0003: additive = сумісно).
2. `topology.json.events.{type}`: kind, producer (має бути у `subscriptions` або `producer_roles` з цим `emits`),
   schema, `schema_version: "1.0"`, scope, `replay_source`, required subscriptions (для `replay_source` — `archive`).
3. Envelope: додати тип у `$defs.eventType.enum` і до відповідного `allOf` блоку (message/aggregate scope).
4. `fixtures/valid/{type}.json`; за потреби invalid fixture.
5. Тести: hard-coded перелік у `TopologyRegistryTests.ExpectedEventTypes`/`ExpectedRequired` — оновити свідомо.
6. `topology_version` +1, якщо змінюється набір очікуваних підписок; `python tools/gen-asyncapi.py`.

## Як додати subscription

`topology.json.subscriptions.{id}`: bindings (кожна подія має перелічити її в required/optional/conditional),
lanes, emits (кожна — з `producer` = цей id), `queue_policy` (`required` для required), `idempotency`,
`owner_task`, `status: planned`. Audit/analytics/projection підписки — `emits: []`. Потім `topology_version` +1,
`ExpectedSubscriptions` у тестах, generator.

## Runtime (P03)

- Черги оголошуються і deliveries очікуються лише для підписок зі `status` `active`/`paused` у `messaging.subscriptions` поточної
  `topology_version` (перший insert — з файлу, далі БД); `planned` підписки нічого не отримують до активації в новій версії.
- Bridge-`raw.stored` (collector без `ingress.received`, fallback): `causation_id = event_id` (root), `correlation_id` = UUIDv5(`source_id`, `source_message_key`),
  identity з legacy `source_message_id` за `fixtures/identity-cases.json` — ADR-0003.
- Ingress (P04): collectors публікують `ingress.received` (root, `causation_id: null`) з явною identity, `legacy_source_message_id`,
  `content_hash` (SHA-256 на оригінальному JSON — raw-writer копіює його в `raw_messages.hash`), `collector{name,version,instance}`,
  `checkpoint`; raw-writer відповідає `raw.stored{is_new}` з `causation_id` = id ingress-події.
- Consumer перевіряє `event_type` за registry і `schema_version` MAJOR = supported (`compatibility.json`, `major_equal`); невідповідність →
  `processing.quarantine` без retries. Повна JSON-Schema валідація — лише в тестах (`tests/Puluj.Messaging.Tests/Unit`).

## Runtime (P05): стадії normalizer і parser

- `message.normalized` (`NormalizerHandler`): `text_kind` `text | structured | empty`; `structured_kind` = `alerts_in_ua.alert.started|finished`;
  `normalization_version` = `norm-1`; `normalized_text_hash` = SHA-256 нормалізованого тексту. Стадія `normalize` у `processing.stage_results`.
- `parse.completed` (`ParserHandler`, лише правила/структурований adapter, без LLM): `attempt_id` — UUIDv7 спроби парсингу (не `processing.attempts.attempt_id`,
  який bigint; зв'язок — `stage_results.outputs.attempt_id` і `attempts.stage_result_id`); `method` `rules | structured | none`; `versions`
  {normalization, rules, ruleset_id, catalog_policy}; `facts[]`: `event_kind_code` за `EventKindLegacyMap`, `category` з каталогу `event_kinds`,
  `effective_at`, `location` {kind, place_id, geometry Point, accuracy_km, text}, `confidence`, `evidence` {segment_index, span, rule_id = join(rules),
  rule_version, rules[]; для structured — structured_field: "alert"}, `attributes` — **усі** поля legacy `Target` (legacy_event_type, identification_*,
  parser_version, segment_*, language, alert_level, target_*_id, model/classification_confidence, object_count[_is_approximate], location_*, origin/destination_place_id,
  direction_*, confidence, event_kind_id, parser_metadata; для правил ще hedged, is_launch, places[]) — щоб fact writer (P06) відтворив рядок `targets`
  без втрат (parity з legacy перевіряє `StageTests.S08`). Outcomes: `facts`, `no_facts` (+`fallback_reason` `llm_disabled|llm_skipped_lane|llm_skipped_stale`),
  `unsupported` (`empty`), `needs_llm` (+ команда `llm.requested`), `failed{error.code=normalization_drift}`.
- Structured adapter не читає і не пише `air_alerts`: факт `alert.air_raid.started|ended` несе `parser_metadata.startedAt/finishedAt/sourceAlertId` для alert worker (P09);
  нерозв'язане місце → `location {kind: unknown, text: <title з feed>}` без вигаданого place_id.
- Семантика полів: `evidence.span` — відносно `segment_text` і завжди покриває весь сегмент (позиція в `normalized_text` — не зберігається);
  `hedged`/`is_launch`/`places[]` є лише у rules-фактах (відсутність = false/порожньо для fact writer); `llm.requested` і `parse.completed{needs_llm}` —
  sibling-події з тим самим `causation_id` (вхідний `message.normalized`), зв'язок між ними — `rules_context.attempt_id`; `message.normalized` іншої
  `normalization_version` парсер відхиляє як transient (після ліміту спроб — quarantine `attempts_exhausted`, причина в `error`), той самий version з іншим
  hash → `parse.completed{failed, error.code=normalization_drift}`; `failed`-стадія не блокує повтор — наступна коректна доставка того самого raw/run
  переписує її (successful стадії лишаються ідемпотентними).
- Shadow-режим: стадії працюють поруч із legacy `ProcessingLoop` (той далі пише `targets`/`air_alerts`/`ProcessingStatus`); `parse.completed`/`llm.requested`
  чекають у paused-чергах finalizer/llm-worker до P06.

## Runtime (P06): llm-worker і finalizer

- `llm.completed` (`LlmWorkerHandler`): `fencing_token` = токен lease з `processing.attempts` (job `llm:{request_id}`), `facts[]` за тим самим
  `FactMapper`, що й parser (evidence `rule_id = llm`, `rule_version = llm-{model}-p{prompt}`), `usage` + `cost_usd`, `audit_id` (additive) → `llm_requests`;
  `outcome needs_review` = відмова моделі; `usage` = `input_tokens`, `output_tokens`, `cache_read_tokens`, `cost_usd` (cache-creation токени — лише в audit).
  `llm.failed` публікується **лише** `final:true` (`attempts` ≥ 1 = job-рядки `failed`, включно з terminal без виклику; коди `provider_timeout`,
  `rate_limited`, `provider_error`, `invalid_response` (також відповідь, яку mapper не приймає), `no_api_key`, `budget_unavailable`, `deadline_exceeded`,
  `normalization_drift`, `attempts_exhausted`); non-final повтори — transient redelivery без події. Кожна terminal подія несе `fencing_token` власного
  job-рядка (актуальний максимум), тож finalizer її не відкидає.
- `observations.recorded` (`FinalizerHandler`): `extraction_result_id` = `processing.extractions.extraction_id`, `extraction_version` 1, `observations[]` =
  факти з `observation_id` (UUIDv7, рядки `processing.observations`), `legacy_target_id` відсутній у compat window, `expected_branches` за manifest
  (`domain_branches_by_observation_category`). Публікується лише для outcome `completed` з фактами.
- `message.analysis.completed`: для кожного terminal outcome (`completed | no_facts | unsupported | needs_review | failed`); `timings` — лише наявні
  значення: raw (`received_at`), архів `raw.stored` (`stored_at`, відсутній, якщо archive відстає), `stage_results` (`normalized_at`, `parsed_at`),
  `llm_completed_at` (`occurred_at` події worker'а) і `finalized_at`; `llm_request_ids` для method `llm`; `versions` = normalization/rules зі стадії
  `awaiting_llm` + `model/prompt` з `llm.completed`. Пізній результат (fencing) і будь-який вхід після extraction → receipt `noop`.

## Правила сумісності

- `schema_version` `MAJOR.MINOR`: той самий MAJOR — сумісно; інший → `quarantined`, не exception.
- Нове optional поле — MINOR; видалення/перейменування required, зміна типу — MAJOR.
- Envelope-поля додаються лише optional; required набір §5.1 не змінюється без нового ADR.
