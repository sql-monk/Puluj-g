# Puluj-G: ізольований запуск

`Puluj-G` — робочий форк `Puluj`. Кодові namespace-и, назва бази даних і ролі PostgreSQL навмисно лишилися сумісними з застосунком; ізоляцію забезпечують окремі Docker-проєкт, мережа, томи, образи, локальні порти та mutex збірки.

| Ресурс | Puluj-G |
|---|---|
| Compose project | `puluj-g` |
| Контейнери | `puluj-g-postgis-1`, `puluj-g-api-1`, `puluj-g-admin-1`, `puluj-g-processor-*`, … |
| Образ Worker | `puluj-g-worker` |
| Docker network / managed volumes | `puluj-g_default`, `puluj-g_pgdata`, `puluj-g_logs`, `puluj-g_tgsession` |
| PostgreSQL host port | `5442` → container `5432` |
| Map host port | `8090` → container `8080` |
| Admin host port | `8091` → container `8081` |
| Local API / Admin / Analytics | `5267` / `5268` / `5269` |
| Vite map / admin | `5183` / `5184` |
| Build mutex | `Global\PulujG.Build` |

Запуск Docker:

```powershell
Copy-Item .env.example deploy/.env
pwsh scripts/deploy.ps1 -InitializeDatabase # лише для першої, порожньої інсталяції
```

Для перебудови й перевірок використовуйте `pwsh scripts/deploy.ps1`. За замовчуванням він використовує наявний том `puluj-g-pgdata`, застосовує до нього лише потрібні EF-міграції та не перезаписує налаштування в БД. Якщо том розміщено під іншим іменем, передайте `-DatabaseVolume <ім’я>`. Новий порожній том створюється тільки з `-InitializeDatabase`. Скрипт визначає контейнерні ID через Compose, а не припускає конкретні суфікси контейнерів; тому коректно працює і при іншій кількості реплік `processor`.

`deploy/docker-compose.override.yml` у цьому робочому дереві вказує окремий том `puluj-g-pgdata`. Не підміняйте його томом `Puluj`: це змішає дані двох інсталяцій.

## Профіль `broker` (P02/P03)

`docker compose --profile broker up -d` додає RabbitMQ (`rabbitmq:4.3-management`, single node) і воркер `messaging` (ролі `relay,archive`:
declare topology, outbox relay, reconciliation/cleanup, архів `messaging.events`, DLQ consumer). Default deploy без профілю не змінюється.
`MESSAGING_OUTBOX_ENABLED=true` у `deploy/.env` вмикає DB-first bridge у collectors: `raw.stored` комітиться в `messaging.outbox` разом із raw
(plan §11); без запущеного `messaging` outbox лише росте, reconciliation пише alarm. Rollback: `MESSAGING_OUTBOX_ENABLED=false` і зупинити
профіль; схеми `messaging`/`processing` additive і не читаються legacy-шляхом.

### Єдиний ingress (P04)

`MESSAGING_INGRESS_ENABLED=true` у `deploy/.env` переводить collectors на producer outbox: `ingress.received` + checkpoint джерела в одній
транзакції, raw пише воркер `messaging` (роль `raw-writer`, у профілі `broker`). Потребує запущених `relay` і `raw-writer`; без них нічого не
потрапляє в `raw_messages` (reconciliation пише alarm про outbox/overdue). Міграція `AddRawMessageIdentity` додає `source_message_key`/`source_revision`
(backfill з `source_message_id`) і знімає унікальність `hash`; її `Down` не відновлює unique hash. **Відкат образу до P04 — лише після `Down`**:
нові колонки без default, старий writer падає на NOT NULL (гучно, `collector_states.last_error`), а не губить пости. Відкат поведінки: `MESSAGING_INGRESS_ENABLED=false`
(collectors знову пишуть raw напряму; bridge `MESSAGING_OUTBOX_ENABLED` — окремий fallback). Зміна `topology.json` без bump `topology_version`
зупиняє ingest/migrate — це навмисно (ADR-0002).

### Стадії normalizer/parser (P05)

Воркер `messaging` (профіль `broker`) з ролями `normalizer,parser` виконує `raw.stored` → `message.normalized` → `parse.completed`/`llm.requested` і
пише `processing.stage_results`; `targets`/`air_alerts`/`processing_status` далі пише legacy `processor` (shadow-режим до cutover). Черги `finalizer`
і `llm-worker` існують (paused, P06) і накопичують backlog — reconciliation показує їх як overdue; це очікувано до P06. Вимкнення стадій — прибрати
ролі з `Worker__Roles` сервісу `messaging` (черги лишаються, backlog чекає).

### LLM worker і finalizer (P06)

Ролі `llm-worker`, `finalizer` сервісу `messaging`. Finalizer працює без ключа; llm-worker викликає модель лише з `Llm__Enabled=true` і
`ANTHROPIC_API_KEY`/`Llm__ApiKey` (без ключа — `llm.failed{no_api_key}` → analysis `failed`, видимо). Бюджет: `Llm__MaxCallsPerMinute` і breaker — на
репліку (N реплік × ліміт), `Llm__MaxAttempts` (3) спроб провайдера на запит, lease `Llm__LeaseSeconds` (90 с) > `Llm__TimeoutSeconds`. Кожен оплачений
виклик — рядок `llm_requests` з `request_id`/`fencing_token`/`provider_request_id`; пізній результат після takeover — `outcome = late`; `answered` без
`applied/late` — результат втрачено з crash'ем holder'а (оплачено, повтор після `LeaseSeconds`). Відкритий breaker (429/4xx) — worker чекає паузу в
межах `deadline_at` команди, інакше `llm.failed{budget_unavailable}`.
Міграція `AddExtractions` додає `processing.extractions`/`observations` і колонки `llm_requests`; rollback — `Down` (дані extractions втрачаються,
`targets` legacy не залежать).

### Правила видів подій (P08)

Перший старт після міграції `AddEventKindRules` створює v1 з `data/taxonomy/event-rules.json` (parity з попереднім матчером) — далі правила
живуть у БД і змінюються лише через `/api/admin/rulesets` (draft → validate → preview/corpus → shadow → publish; rollback = активація старішої
версії). `Seed__SeedEventKindRules=false` вимикає bootstrap (тоді `ruleset_id = builtin`). `Parsing__RulesetPin=<n>` — canary-пін версії на репліку
(неопублікована — warning і active, якщо не `Parsing__RulesetPinAllowDraft=true`); `Parsing__ShadowEnabled` (true), `Parsing__ShadowMaxRowsPerHour` (5000),
`Parsing__RulesetPollSeconds` (30 — лаг publish/rollback). Rollback міграції — `Down` (таблиці правил зникають, resolver повертається до builtin;
`parse.completed`/`stage_results` з `ruleset_id = v{n}` лишаються як історія). Admin-сервіс для preview вантажить індекси парсера на запит (TTL 10 хв; вказівники active/shadow — на кожен запит) і містить `data/corpus/` в образі
(`Dockerfile.admin`) для `POST /rulesets/{v}/corpus`; без файлу — 404, порожній корпус — 400 (ніколи «accuracy 1.0 на нулі»).

### Projection (P11)

Роль `projection` увімкнена за замовчуванням у сервісі `messaging` (Compose/`deploy.ps1`): вона лише перетворює `incident.changed` на NOTIFY для API-реплік
(backplane, ADR-0011) і лишає receipts для `track/alert.changed`. Кілька реплік API не потребують Redis: кожна тримає власний LISTEN. Міграція
`AddIncidentReadIndexes` (індекс keyset для `/api/incidents`) — additive. Налаштування API: `Map:IncidentHours` (24), `Map:IncidentSnapshotLimit` (1000),
`Map:IncidentMaxWindowDays` (7). Rolling deploy: старий API ігнорує NOTIFY невідомого типу.

### Replay runs і generations (P14)

Міграція `AddReplayRuns` — additive (індекси `ux_processing_runs_open_replay`, `ix_messaging_events_run`); `Down` дропає їх. Topology **v9** (incident-worker
+ replay lane): деплой worker'а реєструє v9 поруч із v8 (registrar відмовляє лише тій самій версії з іншим hash); consumer v8, що відстає, не читає
чергу `puluj.incident-worker.replay` — replay-доставки чекають, verify лишається червоним (безпечно, без хибних ефектів). Порядок: `messaging` (relay/
declarer + incident-worker), `admin`, `api` — з одного образу v9.
Роль `replay` (`ReplayPublisher`) — у дефолтних ролях сервісу `messaging`; без відкритого replay run вона простоює (poll `Replay:PollInterval`). Опції
`Replay:BatchSize` (200), `PollInterval` (2 с), `MaxInFlight` (1000 pending deliveries lane replay), `WatermarkLag` (60 с), `MaxBatchFailures` (5);
`Messaging:Consumer:PrefetchByLane:replay` (2). Процедура: панель «Replay» → створити run (scope) → старт → (catchup за потреби) → verify → promote;
rollback — кнопка «відкотити» (active generation повертається за одну tx). Catchup — лише до promote (після нього generation — live-ова,
а replay lane не має projection): останній catchup робити безпосередньо перед promote; raw, оброблені live між ними, лишаються в попередній generation.
Після promote нові live incidents ідуть у promoted generation; після rollback incidents вікна `[promoted_at, rolled_back_at]` невидимі до повторного
replay. Partial scope: promote відмовляє при `active_incidents_outside_scope > 0` без `force` — на практиці replay всієї історії або свідомий force. Tracks/alerts replay не будує (ADR-0005 «Межі P14»). Під час replay
`ReprocessService.ResetAsync`/legacy `processing` не запускати (ADR-0009 cutover).

### Ops-контролі, панелі «Черги»/«Повідомлення» (P13)

Міграція `AddMessagingControls` — additive (`messaging.subscription_lanes`, `messaging.control_audit`, `processing.deliveries.lane/occurred_at`, індекси);
`Down` дропає їх без втрати квитанцій. Старий worker з новою БД працює; новий worker зі старою БД — консюмери логують один warning і вважають усі lanes
active. Налаштування Admin (усі опційні): `Messaging:Broker:ManagementUrl` (+`ManagementUser/Password`; користувачу потрібен tag `management`) — тоді
панель показує `ready/unacked` per queue і alarms вузлів; без нього — лише БД. `Ops:Slo:*` — пороги alarms: `OldestAgeSeconds:live|history|replay` (300/3600/3600),
`OutboxUnconfirmedSeconds` (60), `OutboxCriticalSeconds` (300), `StaleHeartbeatSeconds` (90), `InflightStuckSeconds` (300), `RequiredConsumerMissingSeconds` (60),
`SnapshotCacheSeconds` (5). Worker: `Messaging:Consumer:ControlPoll` (5 с) — час реакції на pause/resume/drain. Scale з панелі —
`Docker:ScalableServices` (`processor`, `messaging`; `messaging` під профілем `broker` — compose вмикає його для явно названого сервісу).
Пауза lane'а не змінює expected set: backlog накопичується і видимий, не губиться ([ADR-0012](adr/ADR-0012-ops-controls.md)). Індекси міграції
будуються звичайним `CREATE INDEX` (ShareLock на `processing.deliveries`/`attempts` на час побудови — секунди при поточних обсягах). Rolling deploy:
оновлювати `admin` тим самим образом, що й `messaging` (панель реєструє вбудовану топологію і 500-ить при розбіжності версій).

### Доменні writers і watchdog (P09/P10, cutover)

Ролі `track-worker`, `alert-worker`, `watchdog`, `incident-worker` сервісу `messaging` **вимкнені за замовчуванням**: до cutover домен пише legacy роль `processing`.
Скриптом: `.\scripts\deploy.ps1 -Broker` — профіль `broker` + single ingress (платформа без writers, legacy пише домен);
`.\scripts\deploy.ps1 -Broker -DomainWriters` — cutover: скрипт спершу зупиняє `processor`, потім піднімає `messaging` з ролями writers
(`MESSAGING_WORKER_ROLES`, `PROCESSOR_REPLICAS=0`) і перевіряє, що жоден processor не лишився; rollback — запуск без `-DomainWriters`.
Cutover (ADR-0009): 1) міграція `AddAggregateRevisions` (індекс `ux_targets_observation_id` будується CONCURRENTLY поза транзакцією; перерваний
build лишає INVALID індекс — міграція спершу робить `DROP INDEX CONCURRENTLY IF EXISTS`, повторний `migrate` добудовує); 2) зупинити роль `processing` (loop + `TrackWatchdog`; reset заборонено
під час роботи writers); 3) додати `track-worker,alert-worker,watchdog,incident-worker` до `Worker__Roles` сервісу `messaging` (міграція `AddIncidents` — P10, ADR-0010). Worker відмовляється стартувати з
`processing` і писачами в одному процесі; на різних процесах над однією БД обидва напрямки захищені (writers `noop legacy_owned`, legacy `writers_owned`).
Після cutover `raw_messages.processing_status` нових raw лишається Pending (backlog — outbox/deliveries; admin-семантика — P14/P16); NOTIFY для мапи
йде з writers після commit. Rollback: прибрати чотири ролі, повернути `processing`. Метрики `puluj.writer.stage/outcomes`; benchmark — `P09-writers-evidence.md`.
