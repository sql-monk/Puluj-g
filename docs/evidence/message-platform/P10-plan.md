# P10 — incident worker, evidence, консервативне злиття, ревізії, admin-команди — план

Task: [P10 / issue #12](https://github.com/sql-monk/Puluj-g/issues/12). Залежності: P06 (`observations.recorded`), P07 (`event_kinds`), P08 (rules) — done;
спирається на P09 (writers, lock hierarchy, revisions, ADR-0009). Base commit: `99a9f33`. Gate: conservative merge/split і race tests, immutable evidence,
as-of history, echo не підвищує state.

## Стан коду на старті (звірено)

- Incident-факти (`impact.explosion.reported`, `impact.confirmed`, `fire.reported`, `damage.reported`, `infrastructure.outage`, `air_defence.activity`,
  `air_defence.interception.reported`; `state_model = reported_confirmed_resolved`, `creates_incident = true`, `dedup_policy = null`) після P09 пишуться
  **track-worker'ом** лише як рядки `targets` (fact writer до P10); `expected_branches` мапить `incident` → `track-worker` (P09 N1). Агрегату немає.
- Topology: `incident-worker` `planned` (bindings `observations.recorded`, emits `incident.changed`), схема `incident.changed` вимагає `incident_id, change
  (created|updated|merged|split|resolved|retracted|suppressed), revision, effective_at, recorded_at, observation_ids, event_kind_code, state
  (reported|confirmed|resolved|retracted), generation_id`, опційно `location`, `merged_into_incident_id`, `policy_version`. `projection`/`message-analytics`
  planned → `archive` має бути required subscriber (як для track/alert, ADR-0002 v6).
- Runs без generation (`processing.generations` порожня; P14). Legacy loop не має поняття incident (карта показує вибухи як `targets`).
- Інфраструктура P09 переюзується: `WriterSupport` (Store shared, guards `legacy_owned`/`already_written`, `LinkObservationsAsync`), `TargetMaterializer`,
  `ConsumerDbContext`, паттерн revision/`last_event_id`, `by_manifest` expected set.

## Рішення

### D1. Схема (additive, міграція `AddIncidents`)

- `incidents`: `incident_id bigserial`, `generation_id uuid NOT NULL`, `run_id uuid`, `event_kind_id int FK RESTRICT`, `state text` check
  (`reported|confirmed|resolved|retracted`), `suppressed bool` (адмін-приховування без зміни state), `first_reported_at`, `last_reported_at`, `event_at`
  (effective), `location_kind`, `location_place_id`, `geometry geography(Point)` (лише з evidence), `accuracy_km`, `confidence` (max по evidence),
  `source_count`, `independent_source_count int NULL` (без методики — завжди NULL, ADR), `canonical_observation_id uuid`, `revision int`, `closure_reason`,
  `last_event_id`, `last_correlation_id`, `created_at`, `updated_at`. Індекси: `(event_kind_id, event_at)`, `(state, last_reported_at)`, `(generation_id)`.
- `incident_observations`: `incident_id FK`, `observation_id uuid`, `legacy_target_id bigint NULL`, `relation` (`canonical | supports | echo | confirms |
  resolves | retracts | ambiguous`), `score numeric`, `decision_reason jsonb` (кандидати, відстані, вікна), `policy_version`, `linked_at`, `source_id`;
  unique `(incident_id, observation_id)` **і** unique `(observation_id)` — observation належить одному incident у generation (unique `(generation_id,
  observation_id)` через денормалізований `generation_id`).
- `incident_revisions`: `incident_id`, `revision`, `effective_at`, `recorded_at`, `triggering_event_id uuid`, `actor`, `reason`, `change`,
  `snapshot jsonb` (повний стан incident + перелік links) — as-of history відтворюється зі snapshot; unique `(incident_id, revision)`.
- Down симетричний.

### D2. `incident-worker` (`IncidentWriterHandler`, subscription `incident-worker`)

- Fact writer incident-фактів: рядки `targets` (як P09; track-worker **перестає** писати `incident`-категорію, `expected_branches` `incident` →
  `incident-worker`). Prepare: матеріалізація + kind/policy без стану БД. Apply: `Store` shared → `incident:kind:{event_kind_id}` exclusive (sorted; один
  kind на факт — межа кандидатів = kind, §7 «kind + time + spatial»; crossover між kinds не зливається) → guards → insert `targets` → `IncidentPolicy`.
- **Candidate policy** (`IncidentPolicy`, policy_version `incident-1`): кандидати = incidents того самого kind і generation, state ∈ reported|confirmed,
  `event_at` у вікні kind (`map_lifetime` як консервативне вікно: explosion 2 год, fire 6, damage/outage 12, air_defence 1) **і** просторово: та сама
  `location_place_id` або спільний регіон з відстанню центроїдів ≤ `accuracy_a + accuracy_b + slack(5 км)` (containment район ⊂ область враховується
  через gazetteer hierarchy; facts без локації — лише за place_id; без place і без geometry → завжди новий incident, `relation canonical`, `location_kind
  unknown`). Score = `0.6·time + 0.4·space` (лінійно у межах вікна/радіуса); threshold 0.55; **ambiguity margin 0.1**: два кандидати з близькими
  score → новий incident з `decision_reason.ambiguous = [ids]` і `relation ambiguous` (review), не злиття. Причина/score зберігаються в link.
- **Relation**: те саме джерело у вікні → `echo` (не змінює `source_count`, не піднімає state); інше джерело → `supports` (`source_count`++);
  `impact.confirmed` (або kind з `confirms` у policy) з іншого джерела → `confirms` → state `confirmed`; `state` ніколи не піднімається лічильником каналів.
  Resolution/retraction лише адмін-командами (kinds-«завершень» у каталозі немає); `target.cancelled`/alert-кінець incidents не чіпають («unknown
  cancellation не закриває»).
- Ревізія при кожній зміні (`revision`++, `incident_revisions` snapshot, `last_event_id`), `incident.changed{created|updated}` (aggregate `incident:{id}`,
  partition `incident:kind:{code}`, `generation_id`, `location` за §8.5: `kind` + `place_id` + `accuracy_km`, geometry лише якщо є в evidence).
- Generation: `run.generation_id` або стала «live» generation (`processing.generations` рядок створюється on demand, `is_active`); ключ уніку links включає
  generation — replay (P14) з іншою generation не з'єднає набори.

### D3. Admin-команди через той самий state writer

- `IncidentStateWriter` (Processing): `ResolveAsync`, `RetractAsync`, `ConfirmAsync` (ручне підтвердження з reason), `SuppressAsync`/`UnsuppressAsync`,
  `MergeAsync(sourceId → targetId)` (links переносяться з `decision_reason.merged_from`, source → `retracted{merged}`, `merged_into_incident_id`), `SplitAsync(id,
  observationIds → новий incident)`; кожна — власна tx: `Store` shared → kind lock → revision++ → `incident_revisions{actor, reason}` → outbox
  `incident.changed{resolved|retracted|suppressed|merged|split|updated}`; raw evidence/`targets` не редагуються. Worker і Admin викликають один клас.
- Admin endpoints `/api/admin/incidents`: `GET` (list: state/kind/window), `GET /{id}` (incident + links + revisions), `POST /{id}/{resolve|retract|confirm|
  suppress|unsuppress}`, `POST /merge`, `POST /{id}/split` — усі з `actor`/`reason` (400 без них), 404/409 за станом. Admin реєструє `IncidentStateWriter` +
  `OutboxWriter` (є в Infrastructure).

### D4. Топологія / контракти

`topology.json` v7: `incident-worker` → `active` (lanes live/history), `archive` required для `incident.changed`; `by_manifest` без змін. `FinalizerHandler.ExpectedBranches`:
`incident` → `incident-worker`, `info` → `track-worker`. `incident.changed` fixtures — перевірити/оновити (`policy_version`, `location`).

### D5. Тести (Messaging.Tests `IncidentWriterTests`, PostGIS + RabbitMQ; Processing.Tests `IncidentPolicyTests`)

| # | Сценарій | Доказ |
|---|---|---|
| I01 | «Вибухи у Харкові» → `targets{observation_id}`, incident `reported` revision 1, link `canonical`, `incident_revisions` 1, `incident.changed{created}` валідний (generation_id, location.kind city); дубль події → inbox duplicate; track-worker цей факт не пише | provenance |
| I02 | **Concurrent create/dedup**: 2 репліки incident-worker на бар'єрі, два повідомлення різних джерел про вибухи в Харкові за 3 хв → 1 incident, links `canonical` + `supports`, `source_count 2`, state `reported` (без auto-confirm), revisions 1, 2 | race |
| I03 | **Conservative merge**: інша область → окремий; те саме місто через > вікна → окремий; два «близькі» кандидати (ambiguity) → окремий з `relation ambiguous` і `decision_reason.ambiguous`; district всередині oblast у вікні → злиття (containment) | policy |
| I04 | **Echo не підвищує state**: 3 пости одного джерела → links `echo`, `source_count 1`, state `reported`; `impact.confirmed` з іншого джерела → `confirms`, state `confirmed`, `incident.changed{updated}` | state policy |
| I05 | **Out-of-order cancellation / resolution**: admin `resolve` (effective_at T) → `resolved` revision; пізніший факт з `event_at > T` → новий incident, не reopen; `target.cancelled`/`alert.air_raid.ended` не змінюють incidents | closure semantics |
| I06 | **Replay idempotency**: повторна публікація тієї самої `observations.recorded` → noop; інший набір observations того самого raw → `already_written` noop; links unique | idempotency |
| I07 | **As-of history**: після 3 змін — `incident_revisions` 1..3 зі snapshot; стан «на момент revision 2» відтворюється зі snapshot і дорівнює тому, що бачив клієнт (`incident.changed` revision 2) | revisions |
| I08 | **Admin commands**: merge (links перенесено з reason, source retracted/merged_into), split (нові incident з переліченими observations), retract, suppress — revisions з actor/reason, `incident.changed` за кожним, `targets`/`processing.observations` незмінні (hash до/після) | state writer |
| Unit | `IncidentPolicy`: score/threshold/margin, вікна за kind, containment, echo vs supports, confirms | Processing.Tests |

### D6. Документація

`ADR-0010-incidents.md` (модель, policy, state machine, generation, admin-команди через state writer, межі: independent_source_count, crossover kinds,
geometry), ADR-0002 (v7), ADR-0006 (таблиці), ADR-0009 (owner incident, track-worker без incident-фактів), README контрактів («Runtime (P10)»),
`docs/README.md` (admin API incidents), `fork-deployment.md` (роль `incident-worker` у cutover), plan §17, evidence `P10-incidents-evidence.md`, handoff, manifest.

## Порядок

1. Схема/entity/міграція. 2. `IncidentPolicy` + unit. 3. `IncidentStateWriter` + `IncidentWriterHandler`; track-worker/finalizer routing. 4. Topology v7, DI/ролі
(`incident-worker` у `StageRoles.DomainWriters`, deploy.ps1 cutover roles). 5. Admin endpoints. 6. Тести I01–I08. 7. Docs, review, handoff, commit, push.

## Ризики / межі

- Межа кандидатів = kind: вибух і пожежа в одному місці — різні incidents (crossover — окреме рішення після даних). `independent_source_count` — NULL.
- Geometry: лише `location.geometry` з факту (центроїд місця з `accuracy_km`) — на мапі показувати коло похибки, не точку (P11).
- Generation «live» — тимчасова стала до P14.

## Незалежне review плану — p10_review: approve after fixes → внесено

| # | Finding | Рішення в плані |
|---|---|---|
| B1 | `confirms` через інший kind суперечить lock/candidate scope «один kind» | policy: `confirms` мапа в `dedup_policy` kind'а (`impact.confirmed → [impact.explosion.reported]`); candidate kinds факту = {K} ∪ kinds, які K підтверджує; lock = **усі** ці kind ids sorted exclusive; `impact.confirmed` без кандидата → власний incident kind `impact.confirmed`, state `reported`; state `confirmed` лише при link `confirms` з джерела ≠ canonical |
| B2 | Перехід fact-writer: старі події в черзі track-worker; guard-набори | track-worker вирішує за `payload.expected_branches`: якщо `incident-worker` там **немає** — пише incident-рядки як у P09; інакше пропускає. Guard `already_written` в обох writers — повний набір observation ids події (як у P09). Тест: змішаний raw (target + incident) → обидва писачі, один incident, обидва `legacy_target_id` |
| B3 | NOTIFY `TargetCreated` для incident-рядків | incident-worker емітує `TargetCreated` + `metrics.TargetCreated` у `AfterCommit` (I01) |
| N1 | Вікно з `map_lifetime` суперечить ADR-0008 | seed `dedupPolicy {windowMinutes, slackKm, confirms[]}` для incident kinds (`policyVersion` 2); `IncidentPolicy` читає `EventKind.DedupPolicy`, дефолти в коді; `policy_version = incident-1/p{catalog}` |
| N2 | Просторовий критерій | `Correlator.AnchorOf` + `SpatialAnchor.GapTo` (полігони admin-одиниць, радіус для точок); gap ≤ slack → кандидат; кандидати з іншого регіону в межах вікна → `decision_reason.near_candidates` (review), не merge |
| N3 | Envelope admin-подій; outbox off | як `DomainWatchdog.Command`: causation = `last_event_id` / UUIDv5 `legacy:`, correlation = `last_correlation_id` / UUIDv5, run = open live, lane live, `occurred_at` = effective; Admin без `Messaging:Outbox:Enabled` → 409 (без тихого запису) |
| N4 | Контракт suppressed/merge/split | `incident.changed` +`suppressed: boolean` (additive); unsuppress → `updated{suppressed:false}`; merge: source `merged{merged_into_incident_id}` (state `retracted`, `closure_reason merged`), target `updated` з observation_ids перенесених; split: новий `split` (`decision_reason.split_from`), source `updated`; fixture/тести реєстру оновити (archive required) |
| N5 | Out-of-order/late facts | вікно симетричне до `incident.event_at`; `event_at`/`first_reported_at` = min по evidence; resolved incident: факт з `event_at ≤ closure effective_at` → link `supports` без reopen; після — новий incident (I05) |
| N6 | Ідемпотентність links | існуючий link для observation → skip без revision++; unique лише `(observation_id)`; I06 + redelivery з іншим event_id |
| N7 | Тест-інфраструктура | fixture: TRUNCATE incidents/links/revisions, `IncidentWorker`, роль у `AddPulujDomainWriters`, W06/DomainWriterTests стартують incident-worker; для kinds поза v1-правилами — синтетичні `observations.recorded` через `OutboxWriter` (raw через `IngestAsync`) |
| N8 | `legacy_target_id` без FK; reset | без FK; legacy `ResetAsync` incidents не чіпає (документовано, заміна — P14) |
| N9, N10 | Docs, generation live | ADR-0005/completion-manifest/ADR-0010 history mode; live generation = UUIDv5(`generation:live`), `INSERT … ON CONFLICT DO NOTHING`, `is_active` |
| Q1–Q5 | вікно від `event_at`; location = найточніший evidence (мін. accuracy), ніколи не розширюється; suppressed лишається кандидатом; merge різних kinds → 409; echo через 40 хв = echo (межа policy) |
