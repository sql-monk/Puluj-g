# ADR-0010 — Incidents: схема, owner `incident-worker`, консервативне злиття, ревізії, admin-команди

Статус: accepted (P10). Стосується плану §7, §8.4–8.5, ADR-0002 (v7), ADR-0005 (generation), ADR-0006, ADR-0008 (`dedup_policy`), ADR-0009 (lock hierarchy).

## Контекст

До P10 incident-факти (`impact.*`, `fire.reported`, `damage.reported`, `infrastructure.outage`, `air_defence.*`) лежали лише як рядки `targets` від
track-worker'а; агрегату «подія» не було, карта показувала кожен пост окремо, оператор не мав команд. Спроба «зливати» пости евристикою без evidence і
ревізій суперечить §8.5 (immutable evidence, as-of history) і §8.4 (state ніколи не піднімається лічильником каналів).

## Рішення

1. **Схема** (міграція `AddIncidents`): `incidents` (state check `reported|confirmed|resolved|retracted`, `suppressed` окремо від state, `event_at` =
   min по evidence, `first/last_reported_at`, location = найточніший evidence: `location_kind`, `location_place_id`, `geometry` (лише з факту), `accuracy_km`,
   `confidence` = max, `source_count`, `independent_source_count` **завжди NULL** (методики незалежності джерел немає — не вигадуємо), `canonical_observation_id`,
   `revision`, `closure_reason`, `merged_into_incident_id`, `last_event_id`/`last_correlation_id`, `generation_id`, `run_id`); `incident_observations` (PK
   `(incident_id, observation_id)`, **unique `(observation_id)`** — observation належить одному incident; `relation` ∈ `canonical|supports|echo|confirms|ambiguous|moved`,
   `score`, `decision_reason` jsonb — кандидати, gap, dt, `near_candidates`, `ambiguous`, `merged_from`/`split_from`; `policy_version = incident-1/p{catalog}`;
   `legacy_target_id` без FK); `incident_revisions` (`(incident_id, revision)` unique, `change`, `effective_at` (evidence), `recorded_at` (годинник),
   `triggering_event_id`, `actor`, `reason`, `snapshot` jsonb — повний стан + links; as-of = snapshot ревізії).
2. **Owner.** `incident-worker` — fact writer incident-observations (рядок `targets` + `LinkObservationsAsync`, NOTIFY `TargetCreated`, як у P09) і єдиний
   writer агрегату через `IncidentStateWriter`. Finalizer: `expected_branches` `incident → incident-worker`; track-worker пропускає incident-факти лише коли
   подія називає цю гілку (старі v6-події в чергах дописує сам — B2). Guards P09 (`legacy_owned`, `already_written` за повним набором observation ids)
   спільні.
3. **Locks** (ADR-0009 hierarchy): `Store` shared → `incident:kind:{event_kind_id}` exclusive для kind факту **і** всіх kinds, які він підтверджує
   (sorted). Кандидати читаються під lock'ом → race двох реплік дає один incident (I02). Admin-команди беруть ті самі locks у власній tx.
4. **Policy `incident-1`** (`IncidentPolicy`, pure): кандидати — incidents тієї ж generation, kind ∈ {K} ∪ `confirms(K)`, `|event_at − effective_at| ≤ window`
   (симетрично; late facts — N5), просторово `SpatialAnchor.GapTo ≤ slackKm` (полігон admin-одиниці або радіус; containment район/область ⊂ — gap 0);
   без geometry — лише той самий `place_id`; `resolved` — лише факт з `effective_at ≤ closure effective_at` (ревізія `resolved`), без reopen;
   `retracted` (hoax, merged away — `merged_into_incident_id`) — ніколи не кандидат (review B3/Q2: мертвий incident не накопичує evidence). Score `0.6·time + 0.4·space`, threshold 0.55, **ambiguity margin 0.1** → окремий incident `relation ambiguous` (review), не здогадка;
   кандидати за межами slack, але ≤ 3·slack або в тому самому регіоні → `decision_reason.near_candidates`. Параметри — `event_kinds.dedup_policy`
   `{windowMinutes, slackKm, confirms[]}` (seed `policyVersion 2`), дефолт `120/5/[]`. Crossover kinds (вибух ≠ пожежа) не зливається; merge різних kinds → 409.
5. **Relation/state.** Джерело, яке incident уже чув (будь-який link або canonical) → `echo` (не змінює `source_count`, не піднімає state); нове
   джерело → `supports`; kind із `confirms`, що містить kind incident'а, з нового джерела → `confirms` → `reported → confirmed` (review B4: incident
   без canonical source — ambiguous/split — теж не підтверджується своїм каналом). Інших автоматичних переходів немає: `resolved`/`retracted` — лише команди;
   `target.cancelled`/кінець тривоги incidents не чіпають (unknown cancellation не закриває). Location: найточніший evidence (мін. `accuracy_km`), ніколи не
   розширюється; `confidence` — max.
6. **Ревізії й події.** Кожна зміна — `revision++`, рядок `incident_revisions` зі snapshot, `incident.changed` (`aggregate_id = incident:{id}`,
   `partition_key = incident:kind:{code}`, `change` ∈ `created|updated|merged|split|resolved|retracted|suppressed`, `+suppressed` (additive v7), `location`
   {kind, place_id, accuracy_km, geometry?}, `policy_version`, `generation_id`, `occurred_at` = effective, `recorded_at` = clock). Уже прив'язана observation
   (redelivery з іншим `event_id`, інший run) → skip без revision (N6). `archive` — required subscriber до появи `projection`.
7. **Admin-команди через той самий writer.** `resolve|retract|confirm|suppress|unsuppress|merge|split` — `IncidentStateWriter.CommandAsync`: власна tx,
   Store shared + kind locks, `actor`/`reason` обов'язкові, `effective_at` опційний (default — now); envelope як у watchdog-команд (N3):
   `causation_id = last_event_id` (або UUIDv5 `legacy:`), `correlation_id = last_correlation_id`, run = open live; `effective_at` команди — між
   `event_at` і now (Q6). `updated_at` — завжди годинник, effective time — у ревізії. Merge: та сама generation (409 інакше), links source → target як
   `moved` (`decision_reason.merged_from`, `original_relation`), target бере найточнішу локацію/max confidence (як worker), source
   `retracted{merged, merged_into_incident_id}`, retracted не бере участі; split: новий incident того ж kind (`split_from`; локація — найточніший з
   перенесених рядків), source `updated` (state `confirmed` → `reported`, якщо link `confirms` пішов). Raw evidence, `targets`, `processing.observations` не редагуються ніколи (I08 — hash до/після). Admin без
   `Messaging:Outbox:Enabled` → 409: команда без події не виконується.
8. **Generation.** `run.generation_id` або стала live generation `UUIDv5(generation:live)` (`processing.generations` on demand, `INSERT … ON CONFLICT DO
   NOTHING`) — до P14 (orchestration/promote). Links різних generations не перетинаються (replay з іншою generation не з'єднає набори).

## Альтернативи

- Incident як «трек» у `target_tracks` — відкинуто: інша state machine, admin-команди, immutable links.
- Автоматичне `resolved` за таймаутом — відкинуто (§8.4: невідома природа закінчення; оператор або майбутній kind-«завершення»).
- Crossover merge (вибух → пожежа → пошкодження) — відкладено до даних про частоту; зараз три incidents і `near_candidates` для ревʼю.

## Наслідки

- `dedup_policy` стає частиною каталогу (ADR-0008) — зміна вікна/slack — зміна `policyVersion`, стара `policy_version` лишається в links.
- Один kind lock — serial ceiling для домінантного kind (`impact.explosion.reported`); finer partitions — після вимірів (як категорія для tracks).
- `ReprocessService.ResetAsync` incidents не чіпає (N8); повний reset домену — P14.

## Межі

- incident-worker — третій writer через тригери `targets` (`source_daily_stats`, `source_copies`, `target_links`): advisory-locks не циклять
  (родини `track*`/`alert:*`/`incident:kind:*` не перетинаються в одній tx), row-lock цикли з track-worker на одному джерелі розв'язує `40P01` +
  requeue (Q1) — лічильники `40P01` знімати після cutover.
- Crossover асиметричний (Q3): звіт про вибух ніколи не приєднується до incident'а, заснованого `impact.confirmed`; `impact.confirmed` з двома
  кандидатами (вибух і confirmed-kind) може дати `ambiguous`.
- `is_active` generation обчислюється на insert (Q4): два різні generation id одночасно — обидва active; P14 orchestration.
- Невідомий kind у worker'і → refresh індексу, далі `PermanentDeliveryException unknown_kind` (N9); в Admin індекс вантажиться перед командою (B2),
  подія ніколи не несе числовий код.

## Відкрите

`independent_source_count` (методика) — Ops/P15; crossover policy — після даних; projection/`NOTIFY IncidentChanged` для карти — P11; replay lane — P14;
partition finer than kind — після benchmark.

## Перевірка

`IncidentPolicyTests` (12, pure), `IncidentWriterTests` I01–I09 (PostGIS + RabbitMQ: provenance, race, policy incl. deterministic ambiguity,
echo/confirms, out-of-order closure, replay idempotency, as-of, admin incl. fact after merge, mixed raw for both owners), `IncidentEndpointsTests`
(Guard mapping, outbox off), `P10-incidents-evidence.md`.
