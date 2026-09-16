# P09 — track/alert writers, locks/revisions, watchdog commands, SQL trigger ownership — план

Task: [P09 / issue #10](https://github.com/sql-monk/Puluj-g/issues/10). Залежності: P06 (`observations.recorded`, extraction), P07 (`event_kinds`) — done.
Base commit: `4849172` (P08). Gate задачі: candidate-create race, cross-region/time-window, alert start/end/cancel, expiry race, hot-row benchmark.

## Стан коду на старті (звірено)

- **Legacy writers** (усі в одній tx під глобальним `AdvisoryLocks.Store`): `RawMessageProcessor` → `targets` + `ITargetSink`: `TextAlertSink`
  (`air_alerts` для текстових тривог/відбоїв, ключ `text:{place}:{startUnix}`, MaxAge 3 год, каскад по нащадках місця) і `CorrelationSink` (дублікати,
  candidate tracks за категорією/вікном, `target_tracks`/`track_targets`/`target_track_revisions`, закриття треків у регіоні за відбоєм/`target.cancelled`);
  структуровані тривоги — `AlertsInUaHandler` (`air_alerts` за зовнішнім `SourceAlertId`, старт/кінець у будь-якому порядку). `TrackWatchdog` (hosted у ролі
  `processing`): закриває треки за `2×` вікном класу і текстові тривоги за MaxAge, теж під Store. Повідомлення — `PgNotify` (`PulujEvent`).
- **SQL-тригери** (P00-inventory): `trg_targets_insert_kinematics` (AFTER INSERT targets → `source_daily_stats` upsert, `target_anchors`, `puluj_link_target`),
  `trg_targets_duplicate` (AFTER UPDATE duplicate_of → links). Спрацьовують на будь-який insert у `targets`, незалежно від writer.
- **Платформа**: `observations.recorded` (P06) несе факти з `attributes` = усі поля legacy `Target` (parity P05-S08); `expected_branches` за категорією;
  topology: `track-worker`/`alert-worker` `planned` (bindings `observations.recorded` + `*.expiry.requested`, emits `track.changed`/`alert.changed`,
  idempotency `inbox + aggregate_revision`), `watchdog` — producer команд через outbox. Envelope aggregate-scope вимагає `aggregate_id`,
  `aggregate_revision`, `causation_id` (uuid). Агрегати не мають `revision`; `targets` не мають зв'язку з `observation_id`.
- Shadow-режим: стадії/finalizer не пишуть домен; legacy loop — єдиний writer до cutover.

## Рішення

### D1. Схема (additive, міграція `AddAggregateRevisions`)

- `targets.observation_id uuid NULL` + partial unique (`WHERE observation_id IS NOT NULL`) — ідемпотентність запису факту; `targets.written_by text NULL`
  (`legacy | track-worker | alert-worker`) — видимість подвійного writer.
- `target_tracks.revision int NOT NULL DEFAULT 0`, `last_event_id uuid NULL`, `last_correlation_id uuid NULL`; те саме для `air_alerts`. `revision` =
  `aggregate_revision` у `track.changed`/`alert.changed`; `last_event_id` — causation для команд watchdog'а (aggregate-scope вимагає uuid).
- Down симетричний; legacy код ці колонки не читає.

### D2. Матеріалізація фактів → `Target` (`Domain/TargetMaterializer`)

`observations.recorded.observations[]` → `Target` (усі поля з `attributes`, `location.geometry` → Point, `effective_at` → ObservedAt, `observation_id`,
`written_by`). Gate: reverse-parity unit — `FactMapper.ToFact(materialize(fact)) ≡ fact` (canonical) для корпусу фактів P05/P06; integration — той самий
набір повідомлень через legacy `RawMessageProcessor` і через нові writers дає однакові `targets`/`target_tracks`/`track_targets`/`air_alerts` (без id/часів
запису) — «parity by construction», бо writers **переюзують legacy sinks** (`TextAlertSink`, `CorrelationSink`, `AlertsInUaHandler`-логіку через
структуровані факти) замість нової кореляції.

### D3. `track-worker` (`TrackWriterHandler`, subscription `track-worker`)

- Вхід `observations.recorded`: факти категорії `target` → матеріалізація → `PrepareAsync` (поза tx): лише парсинг/материалізація, без читання стану
  (кандидати читаються під lock). `ApplyAsync`: locks у детермінованому порядку — `pg_advisory_xact_lock_shared(hashtext('track'))` + для кожної категорії
  (sorted) `pg_advisory_xact_lock(hashtext('track:cat:{id}'))`; guard подвійного writer (`targets` для цього raw з `written_by='legacy'` → `noop`
  `legacy_owned`); insert `targets` з `observation_id` (`ON CONFLICT DO NOTHING` → повтор = noop); `CorrelationSink.OnTargetsAsync` (EF, той самий код:
  дублікати, кандидати, create/attach, revisions); для фактів `alert.air_raid.ended`/`target.cancelled` — закриття треків у регіоні (`CloseTracksInRegion`,
  legacy-логіка; при відбої без категорії — exclusive `hashtext('track')`); revision++ і `last_event_id` для кожного зміненого треку; `track.changed{created |
  updated | closed | cancelled}` (aggregate_id `track:{id}`, aggregate_revision, partition_key = category) у outbox тієї ж tx.
- `track.expiry.requested` (команда): lock категорії треку; re-read; `revision != expected_revision` → `noop stale_revision`; не active → noop; `last_seen +
  window·CloseAfterWindows > watermark` → noop `still_fresh`; інакше `Closed/timeout`, revision++, `track.changed{expired}`.
- Candidate-create race: два репліки з двома спостереженнями однієї категорії одночасно → другий re-read під lock бачить створений трек → attach (1 трек).
  Cross-region/time-window: різні регіони або поза вікном → окремі треки (legacy Correlator).

### D4. `alert-worker` (`AlertWriterHandler`, subscription `alert-worker`)

- Факти категорії `alert` (`alert.air_raid.started|ended`): матеріалізація; lock `pg_advisory_xact_lock(hashtext('alert:region:{regionPlaceId}'))`
  (регіон за gazetteer; невідомий → `alert:unknown`); guard legacy; insert `targets`; `IdentificationMethod.Structured` → логіка `AlertsInUaHandler`
  (зовнішній `source_alert_id` з `attributes.parser_metadata.sourceAlertId`, start/end у будь-якому порядку); текстові → `TextAlertSink.OnTargetsAsync`;
  revision++/`last_event_id`; `alert.changed{started | ended | updated | cancelled}` (scope `{place_id, external_alert_id?, level?}`, aggregate `alert:{id}`).
- `alert.expiry.requested`: lock регіону; revision check; текстова тривога з `EndedAt == null` і `started + MaxAge <= watermark` → `EndedAt = started +
  MaxAge`, revision++, `alert.changed{expired}`.

### D5. Watchdog (`DomainWatchdog`, роль `watchdog`, producer команд через outbox)

- Кожні `Correlation:WatchdogInterval`: `SELECT` active треків з `last_seen + window·CloseAfterWindows <= watermark` (watermark = now, або `min(occurred_at)`
  незавершених deliveries `observations.recorded` мінус 30 хв — history-aware, як legacy) → `track.expiry.requested{track_id, expected_revision, expire_at,
  watermark, reason}`; текстові тривоги без кінця старші MaxAge → `alert.expiry.requested`. `event_id` = UUIDv5(`expire:track:{id}:{revision}`) — повторний
  sweep до обробки = той самий event → inbox dedup; `causation_id = last_event_id`, `correlation_id = last_correlation_id`, lane `live`, run = open live run.
  Команди без власного стану — idempotent за revision у власника.
- Expiry race: команда з `expected_revision` n, трек оновлено до n+1 (нове спостереження) до доставки → `noop stale_revision`; дубль команди → inbox noop.

### D6. Cutover і подвійний writer

- Ролі `track-worker`, `alert-worker`, `watchdog` (`Messaging:DomainWriters` через `Worker:BrokerRoles`); Compose `messaging` **не** вмикає їх за
  замовчуванням; cutover = зупинити роль `processing` (legacy loop + `TrackWatchdog`) і увімкнути три ролі. Обидва напрямки захищені: writers → `noop
  legacy_owned` якщо legacy вже записав raw; `RawMessageProcessor` → якщо для raw є `targets.observation_id` (writers записали) — позначає Done без запису
  (лог warning `writers_owned`). `TrackWatchdog` legacy — лише в ролі `processing` (без змін).
- SQL-тригери: ownership = «той, хто вставляє рядок `targets`» (рівно один writer на raw завдяки guard'ам + unique `observation_id`); тригери лишаються в
  fact path у compat window; винесення stats/links у projection consumer — P11/P15 (ADR-0004/0006 open). Hot-row: benchmark нижче вимірює їхню ціну.

### D7. Контракти / topology

- `topology.json` v6: `track-worker`, `alert-worker` → `active` (queues для 3 lanes). `track.changed`/`alert.changed`/`*.expiry.requested` — за наявними
  схемами (без змін; fixtures існують — перевірити наявність, додати за потреби). `partition_key` = category / `alert:{region}`.

### D8. Тести (Messaging.Tests, PostGIS + RabbitMQ; `DomainWriterTests`)

| # | Сценарій | Доказ |
|---|---|---|
| W01 | `observations.recorded` (target) → `targets{observation_id, written_by}` + трек + `track.changed{created}` валідний; повторна доставка/дубль події → noop, 1 трек | idempotency |
| W02 | **Candidate-create race**: 2 репліки track-worker, 2 спостереження однієї категорії/регіону майже одночасно (Gate на Prepare) → 1 трек з 2 targets, `created` + `updated`, revisions 1,2 | lock + re-read |
| W03 | Cross-region / time-window: інший регіон → другий трек; те саме місце через > вікна → новий трек; дублікат (те саме місце/час, інше джерело) → `duplicate_of` + confidence++ | Correlator parity |
| W04 | Alert start/end/cancel: текстова тривога → `air_alerts` + `alert.changed{started}`; текстовий відбій → `ended` + треки регіону `cancelled`; структурований start/end (у зворотному порядку) → один інтервал; повторний старт → `updated` лише при зміні рівня | обидва шляхи |
| W05 | **Expiry race**: watchdog команда (expected_revision 1) після нового спостереження (revision 2) → noop `stale_revision`; актуальна → `expired`; дубль команди → inbox noop; текстова тривога → `expired` після MaxAge | revisions |
| W06 | Legacy parity: 8 повідомлень (корпус S08 + тривоги) через legacy `RawMessageProcessor` vs через writers → канонічні snapshots `targets`/tracks/links/alerts рівні (без id/дат запису) | parity |
| W07 | Подвійний writer: legacy обробив raw → writers `noop legacy_owned`; writers записали → legacy позначає Done без другого запису | cutover guard |
| W08 | **Hot-row benchmark**: 200 спостережень однієї категорії через 2 репліки vs 2 категорії; метрики: throughput, lock wait (Stopwatch навколо advisory lock), Apply/commit ms, тригерна ціна (targets insert) → `P09-hotrow-benchmark.json` | вимір, не assert |
| Unit | `TargetMaterializer` reverse parity; change-type/revision mapping; watchdog event_id детермінований | Processing.Tests |

### D9. Документація

ADR-0004 (Реалізація P09: locks/revisions/commands, W-вікна для агрегатів), ADR-0006 (колонки revisions/observation_id), ADR-0002 (v6), новий
`ADR-0009-aggregate-ownership.md` (partition = category для tracks, region для alerts; lock hierarchy shared/exclusive; watchdog як команди; SQL trigger
ownership; cutover-процедура; crossover-правила поза P09), README контрактів («Runtime (P09)»), `fork-deployment.md` (ролі, cutover, benchmark), plan §17,
handoff §16.4, evidence `P09-writers-evidence.md`, manifest.

## Порядок

1. Міграція + entity. 2. `TargetMaterializer` + unit parity. 3. `TrackWriterHandler` (+ CloseTracks) і `AlertWriterHandler` з переюзом sinks; revisions/events. 4. Guards
двостороннього writer. 5. Watchdog producer + команди. 6. Topology v6, DI, ролі, Compose. 7. Тести W01–W08 + benchmark evidence. 8. Docs, review, handoff, commit.

## Ризики / межі

- Переюз EF-sinks у consumer-tx: `PulujDbContext` на тому самому `NpgsqlConnection`/tx (`db.Database.UseTransaction`) — як у legacy; sinks додають
  `PulujEvent` — конвертуємо у outbox-події замість NOTIFY (NOTIFY лишається legacy-каналом до P11).
- `CorrelationSink.CloseTracksInRegion` для відбою потребує `Target` (`o.TargetId` у revision) — при закритті з alert-факту в track-worker `target_id` = null.
- Category як partition — serial ceiling для домінантної категорії (§7); benchmark це показує; finer partitions — поза P09.
- History/replay lanes: writers обробляють усі lanes; run-ізоляція агрегатів — P14 (документовано).

## Незалежне review плану — p09_review: approve after fixes → внесено

| # | Finding | Рішення в плані |
|---|---|---|
| B1 | `track.changed`/`alert.changed` unroutable (required subs `projection`/`message-analytics` planned → черг немає → relay error-loop) | v6: `archive` додано до required_subscriptions цих подій (archive приймає всі події; ADR-0002 доповнити); тест: relay після W01 без unroutable |
| B2 | Expected set доменних гілок (`by_manifest`) не реалізовано → receipts writers невидимі для completion/reconciliation | `OutboxWriter.EnqueueAsync`: для подій з `conditional_subscriptions.by_manifest` додає expected deliveries за `payload.expected_branches` для active/paused підписок; тест: після W01 delivery `track-worker` expected→completed, alert-only → `noop` |
| B3 | Детермінований `event_id` команд ламає sweep об unique outbox (grace 7 д) | `event_id` = UUIDv5(`expire:{kind}:{id}:{revision}:{watermark за хвилиною}`) + in-memory memo (id, revision) на процес; sweep пропускає вже надіслані; owner порівнює `watermark` з команди; тест «два sweeps → 1 рядок outbox, sweep не падає» |
| B4 | Ownership `targets` для `incident`/`info` | track-worker = fact-writer для **всіх не-alert** фактів (targets рядки), треки — лише для `target`; ADR-0009; W06 корпус з вибухами |
| B5 | Watermark не бачить upstream backlog | watermark = min(`occurred_at`) по (a) unconfirmed outbox envelope, (b) deliveries без outcome через `messaging.events`, (c) legacy raw Pending/InProgress; якщо старше за now−30 хв — це watermark; тест W05c (backlog у черзі → команд немає) |
| B6 | Replay lane → подвійні треки | lanes writers = `[live, history]` (v6); guard `already_written`: для raw уже є `targets.observation_id` не з поточного набору → noop |
| B7 | Guard подвійного writer не серіалізований; reset | writers беруть `pg_advisory_xact_lock_shared(Store)` першим (взаємовиключення з legacy processor/watchdog/reset), далі track(shared|excl) → категорії sorted; raw/source читаються лише у Prepare; guard legacy = `targets` для raw з `observation_id IS NULL`; W07 конкурентний (Gate) |
| N1 | EF на консюмерському з'єднанні | `PulujDbContext.Attach(conn, tx)` через `ConfigureDbContext`-опції + `UseTransactionAsync` |
| N2 | `CorrelationSink` API | рефакторинг: публічні `HandleTargetFactsAsync` (усі target-факти raw одним викликом — `usedTracks` збережено) і `CloseTracksForCancellationAsync`; `OnTargetsAsync` (legacy) викликає їх |
| N3 | NOTIFY після cutover | `PulujEvent` → `DeliveryResult.AfterCommit` NOTIFY (як RawWriter) + outbox-події |
| N4 | Детекція змін | `db.TargetTrackRevisions.Local` / `db.AirAlerts` ChangeTracker; одна подія і revision+1 на агрегат на delivery; таблиця change-type: track `created` (revision 0→1 і TargetCount 1) / `updated` / `closed` (timeout) / `cancelled` (відбій/target.cancelled) / `expired` (команда); alert `started` (новий інтервал, у т.ч. history вже з EndedAt) / `ended` (відбій/structured end) / `updated` (рівень) / `expired` (команда); `cancelled` не використовується |
| N5 | Deadlock `40P01`/`40001` через stats/copies hot rows | consumer: PostgresException 40001/40P01 → як deferred (attempt `superseded`, requeue, не рахується); W08 варіант «2 категорії, одне джерело», лічильник deadlocks; ADR-0009: реальна межа — (source, day) до P11/P15 |
| N6 | Lock upgrade | lock set обчислюється до взяття: якщо потрібен exclusive('track') — тільки він |
| N7 | Метрики §7 | `PulujMetrics`: histograms lock_wait/apply/commit ms per subscription, counters noop reasons/deadlocks |
| N8 | Індекс на великий `targets` | partial unique `observation_id` — `CREATE UNIQUE INDEX CONCURRENTLY` з `suppressTransaction` (як ADR-0006) |
| N9 | Envelope команд для legacy-треків | `causation_id` = `last_event_id` або UUIDv5(`legacy:track:{id}`), `correlation_id` = `last_correlation_id` або UUIDv5(`track:{id}`); README: event_id команд — UUIDv5 |
| N10 | Reverse parity `≡ fact` недосяжна | порівнюваний subset = attributes з `Target` (без `hedged/is_launch/places/language`) + location/confidence/effective_at; W06 виключає `parser_metadata` structured-розбіжності |
| N11 | Structured alerts | alert-worker переюзує `AlertsInUaHandler.HandleAsync(db, raw, source)` (raw у Prepare), `observation_id` ставиться на створений Target |
| N12 | `observations.legacy_target_id` | заповнюється в тій самій tx (UPDATE за observation_id) |
| N13 | Тести | prefetch track-worker=1 у fixture; W01 дубль → `Duplicates`; W05 `SweepAsync` напряму; Gate у `finally`; W08 N=120 завжди + evidence |
| N14 | Cutover-док | `raw_messages.processing_status` лишається Pending після cutover — межа до P14/P16; reset заборонено при активних writers |
| Q1 | `last_event_id` | = event_id емітованої `track.changed`/`alert.changed` (pre-assigned) |
| Q2 | change-type | таблиця у N4; `alert.changed{cancelled}` не використовується |
| Q3 | `written_by` | прибрано (`observation_id IS NULL` = legacy) |
| Q4 | Legacy 0 фактів + LLM-факти finalizer | writers пишуть (legacy targets відсутні) — прийнятна розбіжність, документовано |
| Q5 | `revision` vs legacy зміни під час overlap | overlap заборонений процедурою cutover; revision інкрементують лише writers; документовано |
