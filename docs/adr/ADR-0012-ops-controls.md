# ADR-0012 — Ops metrics/health, runtime controls per lane, message explorer

Статус: accepted (P13). Стосується плану §9, §8.7, ADR-0002 (expected set, waiver), ADR-0004 (consumer, quarantine), ADR-0007 (SLO), ADR-0008 (audit-патерн).

## Контекст

До P13 моніторинг шини складався з логів reconciliation (`ALARM …`), OTel-метрик і панелі «Воркери» (heartbeat/статус процесу). Не було
per-subscription×lane лічильників, alarms у панелі, runtime-контролів з точним scope (`SetStatusAsync` записував статус підписки, але
консюмер його не читав), audit-історії команд (одне jsonb `waiver` — лише остання дія) і однієї картки lifecycle повідомлення.
Admin-процес не посилається на `Puluj.Messaging` і не тримає зʼєднання з брокером — моніторинг має бути незалежним від шини (§9.2).

## Рішення

1. **Джерело правди — квитанції в БД.** `OpsSnapshotService` (`Puluj.Infrastructure/Messaging/Ops`) рахує snapshot з
   `processing.deliveries` (+нові колонки `lane`, `occurred_at`), `processing.attempts`, `processing.quarantine`, `messaging.outbox/inbox`,
   статус-документів воркерів (`Runtime:Worker:{name}:Status`, тепер з `Consumers[]` і `Broker`) і звіту reconciliation
   (`Runtime:Reconciliation:Report`, пише `messaging`-процес після кожного проходу). Per subscription × lane: `pending` (deliveries без outcome),
   `inFlight` (attempts running), `retryHour` (attempts failed|interrupted), `adminRetryHour` (з `retry_of_attempt_id`), `quarantined`,
   `oldestPendingAge`, `eventTimeLag` (по `occurred_at`), `oldestRunningAttemptAge`, wait p50/p95/p99 (перший attempt − expected_at),
   processing p50/p95/p99, expected/completed/noop/quarantined за годину, expected/completed за 5 хв, живі консюмери lane'а (зі статусів воркерів).
   Окремо **roots** (§9.1): raw-повідомлення за годину / завершені / у роботі / потребують уваги — не stage jobs. Outbox: unconfirmed, найстаріший вік,
   relay retries, unroutable, confirm-латентність p50/p95; inbox: рядки/год, придушені дублікати (attempts `superseded`), `processing`.
   Read-only tx, `statement_timeout 10s`, кеш ≥5 с (`Ops:Slo:SnapshotCacheSeconds`). Індекси: `(subscription_id, lane) WHERE outcome IS NULL`,
   BRIN на `deliveries.completed_at`/`expected_at`. Рядки deliveries до міграції мають `lane = NULL` — їх pending додається до першого lane підписки.
2. **Management API брокера — опційне друге джерело** (`Messaging:Broker:ManagementUrl`, `ManagementUser/Password` — за замовчуванням AMQP-креденшали;
   користувачу потрібен tag `management`). Дає `ready`/`unacked`/`consumers` per queue і `mem/disk alarm`/`running` per node. Недоступний або не заданий →
   `broker.management = null|{available:false, reason}`; кожен рядок позначає `source: db|management`. Нічого не вигадується.
3. **Alarms — чисті правила** (`AlarmRules.Evaluate(snapshot, slo)`, stateless, unit-tested): `backlog_growing` (pending>0, expected5m>0, completed5m=0),
   `oldest_age_slo` (per lane, `Ops:Slo:OldestAgeSeconds{live 300, history 3600, replay 3600}`; required → error, optional → warn),
   `required_consumer_missing` (active lane, pending старше `RequiredConsumerMissingSeconds`, 0 живих консюмерів), `dlq`, `inflight_stuck`
   (in-flight>0 і найстаріший running attempt > `InflightStuckSeconds` — «черга не порожня», навіть якщо lane при цьому завершує інше; замість неможливого
   «ready 0 при unacked > prefetch×consumers»), `stale_heartbeat_with_jobs` (heartbeat > `StaleHeartbeatSeconds` = 90 і running attempts),
   `llm_paused`, `broker_disconnected` (живий воркер без зʼєднання), `broker_blocked` (node mem/disk alarm або not running), `outbox_stuck`
   (warn > 60 с, error > 300 с), `outbox_unroutable`, `reconciliation_mismatch` (overdue/unknown/declare failed), `roots_need_attention`.
   **Навмисно призупинена/зливна lane ніколи не мовчить і ніколи не є помилкою:** `lane_paused` (info, actor + reason + pending); `oldest_age_slo` на ній —
   info; `backlog_growing`/`required_consumer_missing` не спрацьовують. Severity — словом у UI (`ПОМИЛКА/УВАГА/ІНФО`), не лише кольором.
4. **Runtime-контролі з точним scope.** `messaging.subscription_lanes (subscription_id, lane, state active|paused|draining, reason, actor, changed_at)`
   + `messaging.control_audit (action, subscription_id?, lane?, actor, reason, at, details)`. `SubscriptionAdmin.SetLaneStateAsync` — upsert + audit
   в одній tx; `RetryAsync(id, actor, reason)`, `WaiveAsync`, `SetStatusAsync` теж пишуть `control_audit` (одне джерело аудиту; `subscriptions.waiver`
   лишається для сумісності). Консюмер (`SubscriptionConsumer`) читає стан своїх lanes **до першого** `basic.consume` і далі кожні
   `Messaging:Consumer:ControlPoll` (5 с) у тому самому циклі, що чекає на crash (без окремого таймера): `paused` → `basic.cancel` consumer-tag lane'а
   (канал лишається відкритим — in-flight доробляються, ACK після commit); `active` → `basic.consume` знову на тому ж каналі; `draining` → споживає, поки
   `QueueDeclarePassive` (на короткоживучому каналі) дає 0 повідомлень і власний in-flight = 0 два опитування поспіль, тоді сам ставить `paused`
   (`UPDATE … WHERE state='draining'` — гонка з оператором безпечна; audit `drained`, actor `system`). Кожна репліка знає лише свій in-flight;
   prefetched-but-undispatched доставки в буфері клієнта не рахуються ні як ready, ні як in-flight — хибне «drained» можливе лише в мікросекундній
   щілині між двома handler-викликами і безпечне (вони все одно обробляться і будуть ACK'нуті). БД без
   таблиці (старіша за міграцію) → один warning, усі lanes active. Реакція ≤ poll + in-flight — сказано в підтвердженні UI. Стан lane видно у
   `WorkerStatusDto.Consumers[] {subscription, lane, queue, state, consuming, inFlight, prefetch, consumerTag, delivered, duplicates, requeued}`.
   Expected set **не змінюється** паузою lane'а (ADR-0002: paused підписка лишається обовʼязковою) — backlog видно, а не губиться.
5. **Scale** — `POST /api/admin/ops/messaging/scale {service processor|messaging, replicas, actor, reason}` через `docker compose up --scale`;
   allow-list `Docker:ScalableServices`; поза Docker — 409, нічого не імітується; audit `scale` з результатом.
6. **Message explorer** (`GET /api/admin/messages?q&sourceId&hours&limit`, `GET /api/admin/messages/{rawId}/lifecycle`): пошук за raw id / ключем джерела /
   текстом (ILIKE — лише ≤ 168 год без `sourceId`, `limit ≤ 100`, `statement_timeout 10s`); картка — raw (+текст, url лише `http(s)`), події з
   `messaging.events` за `raw_message_id` (≤ 200; `ingress.received` не має raw id — зʼявляється лише `raw.stored` і похідні), кожна з усіма deliveries
   (outcome/reason/actor + ≤ 20 attempts з повною помилкою), extractions/observations, похідні (tracks через targets, air_alerts, incidents через
   incident_observations), quarantine (envelope ≤ 2 000 символів, повний — у таблиці), summary `waiting/completed/failed` per `subscription/lane` і
   `completion`: `pending` (подій ще немає) | `in_progress` | `needs_attention` (є quarantined) | `completed` (усі зареєстровані expected-гілки
   `completed|noop|waived`). Reader для `completion-manifest.json` не потрібен: «очікувану» гілку зафіксував `OutboxWriter` у deliveries при commit.
7. **RBAC/audit.** Один `X-Admin-Token`; actor — вільний текст (як у catalog audit P12), обовʼязковий разом із reason на кожній команді; ролі — P16.

## Наслідки

- Моніторинг не залежить від брокера: без management API панель показує все з БД (`source: db`); без `messaging`-процесу — «звіту reconciliation ще
  немає», брокер `немає даних` (не «ok»).
- Pause lane'а на N репліках — N незалежних `basic.cancel`; «жорсткий» stop — зупинка контейнера (scale 0).
- Deliveries до міграції без `lane`/`occurred_at`: event-time lag для них недоступний; pending рахується в першому lane підписки.
- Query budget snapshot'а — ~8 запитів на 5 с; 10 переглядачів ділять один розрахунок.
- Admin тепер викликає `TopologyRegistrar.EnsureRegisteredAsync` (реєструє вбудовану топологію, кидає при розбіжності hash): під час rolling-оновлення
  панель «Черги»/explorer відповідають 500, поки admin не на тому ж образі, що й worker.

## Відкладено з §9 (з власником)

| Пункт §9 | Чому не в P13 | Де |
|---|---|---|
| DB pool wait / latency | Npgsql OTel-метрики, не з БД | P16 (OTel dashboard) |
| readiness per worker | потрібен окремий signal у heartbeat | P16 |
| backfill volume/speed/forecast | P13 показує лише джерело/checkpoint/помилки (read-only); replay-контролі — P14 (панель «Replay») | P15 |
| silent source / faulty collector alarm | `/ops/collectors` вже показує помилки; правило «тихе джерело» потребує baseline per source | P14/P15 |
| LLM budget 80 % | `llm_paused` покриває rate limit/пауза breaker'а; бюджет — з `llm_requests` cost | P16 |
| quorum membership / node count | management `/api/queues` members — після появи кластера | P16 |
| `since` для alarms (історія) | stateless-правила; історію дасть OTel/log-aggregation | P16 |
| NOTIFY-пробудження консюмера замість poll | 5 с poll достатньо; `PgNotifyListener` вже є для оптимізації | за потреби |
