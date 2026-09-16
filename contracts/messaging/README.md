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

## Runtime (P08): версіоновані правила

- `parse.completed.versions.ruleset_id` = `v{n}` (версія `event_kind_rulesets`, один snapshot на job) або `builtin` (до seed каталогу правил);
  `versions.rules` лишається `rule-0.1` (stage_version не змінюється).
- `$defs/evidence` (additive): `ruleset_version` (`v{n}|builtin`), `rule_code` (стабільний код правила, для v1 = `event:<стеми>`), `rule_code_version`,
  `rule_span {start,end}` (символи збігу в сегменті). `span`, `rule_id`, `rule_version`, `rules[]` — без змін (P05).
- `event_kind_code` факту — з правила, що спрацювало (kind без legacy enum можливий після publish відповідної версії); `attributes.legacy_event_type`
  тоді `Unknown` (або `TargetObserved` для факту з ціллю).

## Runtime (P14): replay runs, generations (topology v9)

- v9: `incident-worker.lanes += replay`; `producer_roles.replay` емітить `raw.stored` (lane `replay`, `is_new:false`, `processing_run_id` = replay run,
  producer `replay@{instance}`) — secondary producer; `raw.stored.producer` (`raw-writer`) лишається документарним для live/history.
- `processing.runs` (replay): `scope {source_ids?, from, to, catchup_from?, verification?, promoted_from?}`, `checkpoint {published, total, lastRawMessageId,
  lastPublishedAt, ingestCeiling, done, error, failures}`; стани `created|running|paused|verified|promoted|rolled_back|cancelled|failed` (+ live `superseded`).
  Один відкритий replay run. `processing.generations`: рівно одна `is_active`; live writers пишуть в active (advisory lock `generation:active`).
- Admin DTO — `RunDto`, `RunCheckpointDto`, `ReplayCreateRequest`, `RunActionRequest` (`Puluj.Contracts/MessagingOpsDtos.cs`); `control_audit` дії `run:*`.
- Shadow: replay lane не досягає track/alert-worker/projection; incident-worker у replay lane пише лише incidents generation run'а (без `targets`, NOTIFY).

## Runtime (P13): ops controls, worker status, reconciliation report

- `WorkerStatusDto` (additive): `consumers[] {subscription, lane, queue, state active|paused|draining, consuming, inFlight, prefetch, consumerTag,
  delivered, duplicates, requeued}` — lane-runtime кожного консюмера процесу; `broker {connected, endpoint}` — зʼєднання процесу з брокером
  (null у процесів без broker-ролей). `Runtime:Reconciliation:Report` (`ReconciliationReportDto`) — останній прохід reconciliation, пише `messaging`.
- `messaging.subscription_lanes` — операторський стан lane'а; консюмер читає його до першого `basic.consume` і кожні `Messaging:Consumer:ControlPoll`
  (5 с): `paused` → `basic.cancel` тегу `{subscription}@{instance}:{lane}` (in-flight доробляються), `draining` → до порожньої черги, потім `paused`
  (actor `system`, audit `drained`). Expected set не змінюється. `messaging.control_audit` — кожна команда (pause/resume/drain/retry/waive/status/scale).
- `processing.deliveries` (additive): `lane`, `occurred_at` — заповнює `OutboxWriter` (NULL для старих рядків).
- Snapshot/alarms/explorer DTO — `Puluj.Contracts/MessagingOpsDtos.cs` ([ADR-0012](../../docs/adr/ADR-0012-ops-controls.md)).

## Runtime (P11): projection і read-side

- `projection` (`ProjectionHandler`, topology v8, lanes live/history): `incident.changed` → після commit NOTIFY `puluj_events` `{type: IncidentChanged, id,
  at, rev}` — backplane для всіх API-реплік (кожна LISTEN → SignalR `IncidentUpserted` (revision 1) | `IncidentRevised`); `track.changed`/`alert.changed`
  → `noop writer_notifies` (receipt для completion; NOTIFY емітують writers); replay lane / неактивна generation → `noop not_live`. NOTIFY — at-most-once:
  `ListenerReconnected` (не транспортна подія) → hub `Resync(at)` → клієнт перезавантажує вікно. Rolling deploy: невідомий `type` у NOTIFY ігнорується.
- Read-side контракти (`Puluj.Contracts`, camelCase + GeoJSON): `IncidentDto`, `IncidentDetailsDto`, `IncidentPageDto`, `SnapshotDto +incidents,
  +incidentsTruncated`, `MapConfigDto +incidentHours`; `GET /api/incidents` (вікно ≤ 7 діб, keyset cursor, `mode=effective|recorded&asOf`), `GET /api/incidents/{id}
  (?revision=)`. Precision — з `LocationKind` (ADR-0011 п.2).

## Runtime (P10): incident-worker

- `incident.changed` (`IncidentWriterHandler` → `IncidentStateWriter`): `aggregate_id = incident:{id}`, `aggregate_revision` = `incidents.revision`,
  `partition_key = incident:kind:{event_kind_code}`; `change`: `created` | `updated` (link, confirm, unsuppress) | `resolved` | `retracted` | `suppressed` |
  `merged` (source; `merged_into_incident_id`, state `retracted`) | `split` (новий incident); `observation_ids` — прив'язані/перенесені цією зміною;
  `suppressed` (additive v7, top-level властивість payload); `location {kind, place_id, accuracy_km, geometry?}` — найточніший evidence;
  `policy_version = incident-1/p{catalog}` (як у links); fixture `fixtures/valid/incident.changed.json` — за реальною подією I01;
  `generation_id` — `run.generation_id` або live `UUIDv5(generation:live)`; `occurred_at` = effective, `recorded_at` = clock. Admin-команди — той самий
  writer, envelope як у watchdog-команд (`causation_id = last_event_id`). Уже прив'язана observation → без події (N6).
- Topology v7: `incident-worker` active (lanes `live`, `history`); `archive` required для `incident.changed`; `by_manifest`: `incident → incident-worker`.

## Runtime (P09): track/alert writers і watchdog

- `track.changed` (`TrackWriterHandler`): `aggregate_id = track:{id}`, `aggregate_revision` = `target_tracks.revision` (лише writers інкрементують),
  `partition_key = track:cat:{category}`, `category` = код таксономії (`UAV`, `MISSILE`, …); `change`: `created` (трек відкрито цією доставкою) |
  `updated` | `cancelled` (відбій, `target.cancelled`) | `expired` (команда watchdog'а); `closed` — reserved (таймаут іде через команду → `expired`);
  `observation_ids` — observations цього raw на треку (для `cancelled` — факти-причини доставки); `reason` при закритті; `occurred_at` envelope =
  `effective_at` агрегату, `recorded_at` — годинник writer'а.
- `alert.changed` (`AlertWriterHandler`): `aggregate_id = alert:{id}`, `scope {place_id, external_alert_id? (structured), level?}`, `partition_key =
  alert:region:{regionPlaceId}`; `change` визначається порівнянням pre/post-image інтервалу в tx: `started | ended | updated (рівень, start після end,
  тихе закриття за MaxAge) | expired`; `cancelled` не використовується.
- `track.expiry.requested`/`alert.expiry.requested` (`DomainWatchdog`, producer `watchdog@instance`): `event_id` = UUIDv5(тип, aggregate, revision,
  хвилина watermark) — не UUIDv7; `causation_id` = `last_event_id` агрегату (або UUIDv5 `legacy:{aggregate}` для агрегатів legacy loop);
  `correlation_id` = `last_correlation_id` або UUIDv5(aggregate); lane `live`, run = open live run. Owner: `stale_revision`/`not_active`/`still_fresh` → `noop`.
- Expected deliveries `observations.recorded` = `archive` + гілки з `expected_branches` (`by_manifest`), що active/paused; `track.changed`/`alert.changed`
  — `archive` required (v6).
- Receipts writers: `noop` reasons `legacy_owned`, `already_written`, `no target facts…`, `no alert facts…`, `stale_revision`, `still_fresh`.

## Правила сумісності

- `schema_version` `MAJOR.MINOR`: той самий MAJOR — сумісно; інший → `quarantined`, не exception.
- Нове optional поле — MINOR; видалення/перейменування required, зміна типу — MAJOR.
- Envelope-поля додаються лише optional; required набір §5.1 не змінюється без нового ADR.
