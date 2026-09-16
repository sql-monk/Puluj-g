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
