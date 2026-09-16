# ADR-0009 — Володіння агрегатами: track/alert writers, locks, revisions, watchdog-команди, cutover

Статус: accepted (P09). Стосується плану §7 (конкурентність), §8.1, ADR-0002 (events vs commands), ADR-0004 §6.2, ADR-0006.

## Контекст

До P09 єдиним writer'ом доменного стану (`targets`, `target_tracks`, `air_alerts`) був legacy `RawMessageProcessor` з sinks під одним глобальним
`AdvisoryLocks.Store` — коректно, але serial ceiling для всіх повідомлень з фактами. Платформа (P03–P08) доводить факти до `observations.recorded`,
але не пише домен. P00 показав, що просте розбиття lock'а без карти writers дає дедлоки (SQL-тригери `puluj_on_target_insert`, stats hot rows).

## Рішення

1. **Власники.** `track-worker` — fact writer усіх не-alert observations (`targets`, compat projection, рівно один рядок на `observation_id`) і владник
   агрегату track. `alert-worker` — fact writer alert-observations і владник інтервалів `air_alerts`. Обидва — subscriptions `observations.recorded`
   (lanes `live`, `history`; replay — P14) і власних expiry-команд. `incident-worker` (P10, ADR-0010) — fact writer incident-observations і owner агрегату incident (lock `incident:kind:{id}`); track-worker пропускає
   incident-факти, коли подія називає гілку `incident-worker` у `expected_branches` (старі v6-події дописує сам); `info` — track-worker.
2. **Partition і lock hierarchy** (у кожній delivery-tx, детермінований порядок): `pg_advisory_xact_lock_shared(Store)` (взаємовиключення з legacy
   processor/watchdog/reset, паралельність writers між собою) → `track` shared (або exclusive, якщо відбій без категорії) → `track:cat:{category}` exclusive
   (sorted). Alerts: `alert:region:{regionPlaceId}` exclusive (sorted; `alert:unknown` без місця). Категорія — консервативна межа треків (§7); реальна межа
   паралельності всередині процесу до P11/P15 — рядок `source_daily_stats (source, day)`, який тримає тригер insert.
3. **Candidate-create race.** Кандидати треків читаються **під** lock (Prepare лише матеріалізує факти, без стану БД); дублікати між джерелами
   (`FindDuplicateAsync`) бачать уже закомічений оригінал. Тест W02: два репліки на бар'єрі перед tx → один трек.
4. **Parity by construction.** Writers переюзують legacy sinks (`CorrelationSink`, `TextAlertSink`, `AlertsInUaHandler`) через EF-контекст над
   консюмерським з'єднанням (`ConsumerDbContext.AttachAsync`); `TargetMaterializer` відновлює `Target` з `attributes` факту. Порядок обробки —
   частина семантики (напрямок дубліката, склад треку): відтворюваність вимагає впорядкованого input (§7); parity-тест W06 подає повідомлення по одному.
5. **Revisions і події.** `target_tracks.revision`/`air_alerts.revision` інкрементують лише writers (одна `track.changed`/`alert.changed` на агрегат
   на delivery, `aggregate_revision` = revision, `last_event_id`/`last_correlation_id` = емітована подія). Change-type: track `created` (трек відкрито
   цією доставкою) | `updated` | `cancelled` (відбій/`target.cancelled`) | `expired` (команда; `closed` reserved); alert — порівняння pre/post-image
   інтервалу в tx: `started` | `ended` | `updated` (рівень, start після end, тихе закриття за MaxAge) | `expired`; `cancelled` не використовується.
   `category` події = код таксономії. Events routable через `archive` (required) до появи `projection`. NOTIFY `TargetCreated`/`TrackUpserted`/
   `TrackClosed`/`AlertChanged` — з writers після commit (стрічка мапи через `NotifyBridge` до P11).
6. **Watchdog як команди.** `DomainWatchdog` (роль `watchdog`) не володіє станом: `track.expiry.requested`/`alert.expiry.requested` з
   `expected_revision`, `expire_at`, `watermark`; owner перевіряє revision (`stale_revision` → noop), стан, час; `event_id` = UUIDv5 (aggregate,
   revision, хвилина watermark) + memo на процес (заповнюється після commit); `causation_id` = `last_event_id` (або UUIDv5 `legacy:` для агрегатів
   legacy). Watermark — найстаріший `occurred_at` серед unconfirmed outbox і message-scoped deliveries без receipt (через `messaging.events`,
   включно з `ingress.received`), якщо старший за 30 хв; `raw_messages.processing_status` **не** враховується (після cutover raw-writer лишає Pending
   назавжди). Межа: «вічна» expected delivery (paused підписка, загублена подія до reconciliation) пінить watermark — reconciliation/waive (P13) знімає.
7. **Подвійний writer виключено двічі:** worker відмовляється запускати `processing` разом із писачами; writers → `noop legacy_owned` (для raw є
   `targets` без `observation_id`), `noop already_written` (інший набір observations того самого raw — інший run/history reload); legacy
   `RawMessageProcessor` → бачить `observation_id` → позначає Processed без запису (`writers_owned`). Обидві перевірки — під Store. «Захищено» =
   коректність, не відсутність дедлоків: FK `targets → raw_messages` бере KEY SHARE на raw, а legacy/reset тримають raw `FOR UPDATE`/`LOCK TABLE` до
   Store — при перекритті сервер розв'язує цикл `40P01`, consumer робить requeue.
8. **SQL-тригери.** Ownership = «хто вставляє рядок `targets`» (один writer на raw); `trg_targets_insert_kinematics`/`trg_targets_duplicate`
   лишаються у fact path у compat window; винесення stats/links у projection consumer — P11/P15. Дедлоки `40P01`/`40001` між writers — consumer
   робить requeue без лічильника спроб (W08: 0 за 120 спостережень).
9. **Метрики §7.** `puluj.writer.stage{subscription, stage=lock_wait|apply}`, `puluj.writer.outcomes{subscription, outcome}`; benchmark W08 —
   `P09-writers-evidence.md`.

## Cutover

1. Розгорнути збірку (міграція `AddAggregateRevisions`; індекс `ux_targets_observation_id` будується CONCURRENTLY).
2. Зупинити роль `processing` (legacy loop + `TrackWatchdog`); `ReprocessService.ResetAsync` під час роботи writers заборонено.
3. Увімкнути ролі `track-worker,alert-worker,watchdog,incident-worker` (P10) у сервісі `messaging` (профіль `broker`); backlog `observations.recorded` доробиться.
4. Після cutover `raw_messages.processing_status` для нових raw лишається Pending (admin/backlog семантика — P14/P16); NOTIFY для API живе
   через `AfterCommit` writers до P11.
Rollback: зупинити чотири ролі, повернути `processing`; рядки з `observation_id` legacy не чіпає (guard), треки/інтервали спільні; incidents лишаються
(legacy їх не знає — карта показує `targets`).

## Межі

Category — serial ceiling для домінантної категорії; crossover partitions (регіон/час) — після вимірів. Overlap legacy/writers на одному raw
не гарантує parity (Q4: legacy 0 фактів + LLM-факти → writers пишуть). `revision` під час overlap не інкрементується legacy-змінами (overlap заборонено).
