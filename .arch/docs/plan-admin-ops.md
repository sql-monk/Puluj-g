# План: керування, онлайн-моніторинг і статистика в адмін-панелі; нові «Статистика» та «Аналітика»

Стан на 2026-09-15. Стек у Docker (`deploy/docker-compose.yml`, проєкт `puluj`): `postgis`, одноразовий `migrate`,
`collector-telegram`, `collector-alerts`, `processor` × 2 (репліки, `Processing__Concurrency=2`), `api` (:8080),
`admin` (:8081), `analytics`. Локально `scripts/dev-run.ps1` запускає ті самі сервіси процесами без Docker.

Замовлення: (1) з адмінки додавати/прибирати процесори повідомлень і зупиняти/перезапускати контейнери (крім
`admin` і `postgis`); (2) деталі по кожному запущеному процесору; (3) загалом скільки чого звідки зібрано, оброблено,
скільки що займає часу; (4) публічну сторінку «Статистика» (`#/stats`) видалити і зробити заново; (5) розділ
«Аналітика» в адмінці видалити і зробити заново. Керування контейнерами — через Docker socket у контейнері `admin`
(рішення користувача).

## 1. Що є зараз і чого бракує

- **Стан інстансів**: лише heartbeat `Runtime:Worker:{name}:Heartbeat` (раз на 30 с, `WorkerHeartbeat`,
  `AnalyticsHeartbeat`). Ані версії, ані ролей, ані швидкості, ані таймінгів — `PulujMetrics` пише в OTel, який
  ніхто не читає. Панель «Стан» показує лише «heartbeat свіжий/застарів».
- **Статистика обробки** (`/api/admin/ops/processing`, `ProcessingPanel`): 24 години по годинах, без розбивки за
  джерелами, без часу обробки (у БД він не зберігається — лише в логах `parse P ms, lock wait L ms, store S ms`).
  `CollectorsPanel` — по джерелах, але лише «прийнято за 24 год».
- **Керування**: нічого, крім `POST /ops/reprocess` і налаштувань через `app_settings`. Кількість реплік — лише
  `docker compose up --scale` з консолі.
- **Публічна «Статистика»** (`web/src/stats/*`, `Puluj.Api/Services/StatsService.cs` + `StatsAggregator.cs`,
  `GET /api/stats`): одна довга сторінка з 12 картками, саморобні графіки без осей і сітки; замовлено зробити заново.
- **Адмін «Аналітика»** (`web/src/components/analytics/AnalyticsPanel.tsx`, 609 рядків; сервіс `Puluj.Analytics`,
  ендпоїнти `/api/admin/analytics/{status,report,recent,reset}`): усе на одному екрані, стан сервісу впереміш із
  матрицею копій; замовлено зробити заново. Сам сервіс аналітики (схема `analytics`, `AnalyticsReportService`) —
  лишається, змінюється лише подача та кілька додаткових вибірок.

## 2. Архітектура змін

### 2.1. Телеметрія інстансів: `Runtime:Worker:{name}:Status`

Кожен процес Worker (і Analytics) раз на 10 с пише в `app_settings` JSON `WorkerStatusDto` (Contracts, спільний для
писача і читача) під ключем `Runtime:Worker:{InstanceName}:Status`; heartbeat-ключ лишається як є (його читають
`OpsEndpoints.WorkerHeartbeats` і панель). Ключ видаляється при чистій зупинці разом із heartbeat.

```
WorkerStatusDto(Instance, Host, Roles[], Version, StartedAt, At, Pid, WorkingSetBytes, CpuPercent, Threads,
                Processing?: ProcessingStatusDto, Llm?: LlmStatusDto, Paused?: string)
ProcessingStatusDto(Concurrency, Processed, Skipped, Failed, Retried, RetriedTransient,
                    PerMinute1, PerMinute5, Parse/Lock/Store/Total: StageTimingDto, LastProcessedAt?, LastRawMessageId?, Claims[]: ClaimDto)
StageTimingDto(Samples, MeanMs, P50Ms, P90Ms, MaxMs)   — за останні 5 хв (кільцевий буфер 2 000 вимірів)
ClaimDto(RawMessageId, Since)                          — що саме зараз у роботі в цьому інстансі
LlmStatusDto(Enabled, Model, PausedUntil?, PauseReason?, Calls, Failures)
```

Джерело чисел — новий `ProcessingStats` (singleton у `Puluj.Processing`, потокобезпечні лічильники + кільцеві буфери
таймінгів + словник поточних claim-ів), який `RawMessageProcessor` оновлює поряд із `PulujMetrics` (`ProcessingStage`,
`RawProcessed`), а `ProcessingLoop` — при claim/release. `LlmBreaker` віддає `PausedUntil`/причину. CPU — різниця
`Process.TotalProcessorTime` між двома записами / кількість ядер. `Version` — `AssemblyInformationalVersion`
(Docker-збірка копіює лише `src/`, без `.git`, тож `+<sha>` там не буде) плюс `BuiltAt` — час запису
`Puluj.Worker.dll`: саме він відрізняє репліки різних збірок (причина дедлоків 15.09 — змішані версії) і
показується в панелі поряд із версією.

Писач — `WorkerStatusReporter` (`Puluj.Worker/Hosting`), як `WorkerHeartbeat`; для Analytics — той самий DTO без
`Processing`/`Llm` (`AnalyticsHeartbeat` доповнюється версією/uptime у `Status`-ключ; runs і backlog там уже є в
`AnalyticsReportService.StatusAsync`). `Puluj.Analytics.Worker` не посилається на `Puluj.Contracts` — додати
`ProjectReference` (лише DTO, без залежностей).

### 2.2. Час обробки у БД: `raw_messages.processing_ms`

Одна nullable-колонка `int` (`RawMessage.ProcessingMs`, міграція `AddRawMessageProcessingMs`; для 400 тис. рядків —
миттєво, без rewrite). `RawMessageProcessor` записує час успішної обробки до коміту (parse + lock + store, без самого коміту й
NOTIFY — на кілька мс менше за число в лозі) у тій самій транзакції, у фінальному `SaveChangesAsync`. Дає p50/p90 за джерелом/годиною/інстансом у SQL (`percentile_cont`) без
таблиць-журналів; розбивка на стадії лишається в статусі інстансу (§2.1) і в логах.

### 2.3. Docker з контейнера `admin`

- `deploy/Dockerfile.admin`: у фінальний образ — `docker-ce-cli` + `docker-compose-plugin` з apt-репозиторію Docker
  (Debian bookworm, ~90 МБ). Ніякого демона всередині: CLI ходить у прокинутий сокет.
- `docker-compose.yml`, сервіс `admin`: `volumes: /var/run/docker.sock:/var/run/docker.sock`, `./:/deploy:ro`
  (compose-файли + `.env` + `docker-compose.override.yml`, якщо є); `environment: Docker__Enabled: "true"`,
  `Docker__ComposeDir: /deploy`, `Docker__Project: puluj`. Docker Desktop (WSL2) віддає сокет у Linux-контейнери
  штатно; образ `aspnet` працює від root, тож прав на сокет вистачає.
- Новий `DockerService` (`Puluj.Admin/Docker/`): запускає процес `docker …` з таймаутом 60 с, парсить
  `--format '{{json .}}'`. Опції `DockerOptions` (`Docker:Enabled` false за замовчуванням, `Command` = `docker`,
  `ComposeDir`, `Project`, `ComposeFiles` — за замовчуванням `docker-compose.yml` + `docker-compose.override.yml`,
  якщо файл існує, `ProtectedServices` = `admin, postgis, migrate`). Поза Docker (`dev-run.ps1`) — `Enabled=false`,
  панель показує «керування контейнерами недоступне: панель запущена не в Docker».
- Операції (усі лише над контейнерами з міткою `com.docker.compose.project=<Project>`):
  - `List`: `docker ps -a --filter label=com.docker.compose.project=puluj --format '{{json .}}'` +
    `docker stats --no-stream --format '{{json .}}'` (CPU %, memory) → `ContainerDto[]`; сервіс і номер репліки — з
    міток `com.docker.compose.service`, `com.docker.compose.container-number`.
  - `Restart/Stop/Start <container>`: `docker restart|stop|start <id>` — лише якщо контейнер у списку проєкту і його
    сервіс не в `ProtectedServices` (інакше 403 ще до запуску процесу). `stop` для `collector-*`/`analytics`/`api`
    дозволений (користувач просив), у UI — підтвердження з поясненням наслідку (`api` → карта недоступна).
  - `Scale processor N` (0…8): `docker compose -p puluj -f … --env-file .env up -d --no-build --no-deps --no-recreate
    --scale processor=N processor`. `--no-deps` не зачіпає `postgis`/`migrate`; `--no-recreate` не перестворює
    наявні репліки навіть якщо compose-файл у томі відрізняється від того, з яким піднімали стек; зменшення —
    compose сам зупиняє і видаляє зайві репліки (старші номери). Це стандартний спосіб compose і єдиний, за якого
    `docker compose ps` після цього лишається консистентним (Engine API створення контейнера з compose-мітками
    вручну — крихко). `0` дозволено з попередженням «нічого не обробляється».
  - Логи контейнера тут не потрібні: усі сервіси пишуть у спільний том `logs`, який панель уже читає.
- Кожна дія — INF-лог `Docker: {Action} {Target} by {RemoteIp}` + результат (stdout/stderr, обрізані до 4 КБ) у
  відповіді `ContainerActionResultDto`. Помилка `docker` (немає сокета, невідомий контейнер) → 502 з текстом stderr.

### 2.4. Нові ендпоїнти адмінки (`/api/admin/ops/*`, фільтр `AuthorizeAsync`)

| Метод | Що |
|---|---|
| `GET /ops/workers` | `WorkerInstanceDto[]`: усі інстанси за heartbeat-ключами (як `WorkerHeartbeats`) + розпарсений `Status` + з БД `processed24h`, `inProgress` (за `claimed_by`), + контейнер (за суфіксом імені: `processor-616c2ab99756` ↔ id контейнера `616c2ab99756…`; для `collector-*`/`analytics` — за сервісом), якщо Docker доступний |
| `GET /ops/containers` | `ContainersDto`: доступність, проєкт, список, поточна кількість реплік `processor` |
| `POST /ops/containers/{id}/{restart\|stop\|start}` | дія; 403 для захищених сервісів |
| `POST /ops/processors/scale` `{replicas}` | scale `processor`; відповідь — результат compose і новий список |
| `GET /ops/pipeline?hours=24\|168\|720` | `PipelineReportDto`: усього / за джерелами / за бакетами / за інстансами / черга / помилки за етапами / останні 50 помилок. Замінює `/ops/processing` (видаляється разом із `ProcessingReportDto`, `HourlyProcessingDto`) |

`PipelineReportDto` (бакет: `hour` для 24 год, `day` для 7/30 днів; час — `Europe/Kyiv` як у публічній статистиці):
- `Totals`: received (за `received_at`), processed/skipped/failed (за `processed_at`), pending, inProgress (поточні),
  targets і duplicates (`observed_at` у періоді; конвеєр рахує все, що виробив), tracks (`first_seen_at`), errors,
  p50/p90/mean `processing_ms`.
- `Sources[]`: те саме на джерело + `withTargets` (є ціль), `tracks` — треки, чия перша ціль (`track_targets.sequence = 1`)
  належить джерелу (у `target_tracks` немає `source_id`), `medianLagSeconds` (`received_at − published_at`, фільтр
  `< 6 год` — бекфіл має затримку в роки), `series[]` (received за бакет).
- `Timeline[]`: received, processed, targets, errors, transient, p50/p90 `processing_ms`.
- `Instances[]`: за `claimed_by` — processed, p50/p90, lastAt (хто скільки зробив).
- `Queue`: статуси як зараз; `ErrorsByStage`; `RecentErrors` (50).

SQL — `db.Database.SqlQuery<Row>($"…")` з параметрами, правила іменування як у `StatsService` (snake_case,
`count(*)` → `long`, `percentile_cont` → `double?`). Один запит на групу, без циклів по джерелах.

### 2.5. Адмін-UI: група «Моніторинг»

Навігація: **Стан · Воркери · Колектори · Конвеєр · База даних · Логи** («Обробка» зникає — усе її тримає «Конвеєр»;
`ProcessingPanel` видаляється).

- **Воркери** (`WorkersPanel.tsx`, опитування 5 с): картка на інстанс — ім'я, тип (processor / collector-telegram /
  collector-alerts / analytics / worker), стан (heartbeat ≤ 90 с), версія, uptime, ролі, PID/CPU/RSS; для процесорів —
  воркерів (Concurrency), оброблено за годину і за 24 год, повідомлень/хв (1 і 5 хв), таймінги parse / lock / store /
  total (p50/p90, стовпчики-міні), що зараз у роботі (id + скільки секунд), LLM (увімкнено, пауза до …, викликів);
  для контейнера — стан, CPU %, пам'ять, кнопки **Перезапустити** / **Зупинити** (з підтвердженням) — сховані, якщо
  Docker недоступний або сервіс захищений. Угорі — блок **Процесори повідомлень**: «зараз N реплік × M воркерів»,
  поле кількості реплік і кнопка «Застосувати» (підтвердження; при 0 — попередження), результат compose під полем.
  Нижче — таблиця всіх контейнерів проєкту (`api`, `collector-*`, `analytics`, `admin`, `postgis`, `migrate`) з тими
  самими кнопками; для `admin`/`postgis`/`migrate` — лише перегляд і підпис «керується лише з консолі».
- **Конвеєр** (`PipelinePanel.tsx`): перемикач 24 год / 7 д / 30 д; KPI (прийнято, оброблено, з фактами %, цілей,
  треків, у черзі, помилок, p50/p90 мс); графік за бакетами (прийнято/оброблено/цілей + лінія p90 мс); таблиця за
  джерелами (усі колонки `PipelineSourceDto`, sortable, спарклайн); таблиця «за інстансами»; помилки за етапами і
  список останніх помилок (перенесені з `ProcessingPanel`).
- «Стан» (`OverviewPanel`) — як є; до `OpsOverviewDto` додається `ProcessorCount` (живі heartbeat-и `processor-*`),
  таблиця компонентів отримує рядок «Процесори повідомлень: N реплік».
- Спільні дрібні компоненти (`usePolled`, `Bars`, `Stat`, `fmt*`, `ago`) винести з `OpsPanels.tsx` у
  `web/src/admin/shared.tsx` (агент B). Нова «Аналітика» (агент D) працює паралельно і не може на це покладатись —
  тримає власний `admin/analytics/format.ts`; злиття дублікатів — окремим дрібним кроком після обох.

### 2.6. Публічна «Статистика» — заново

Видаляються: `web/src/stats/*` (усе), `Puluj.Api/Services/StatsService.cs`, `StatsAggregator.cs`,
`Endpoints/StatsEndpoints.cs`, `Stats*Dto` у `Puluj.Contracts/Dtos.cs`, `api.stats` у `web/src/api/client.ts`,
`tests/Puluj.Api.Tests/StatsAggregatorTests.cs`. Точка входу `#/stats` в `App.tsx` і сам компонент `StatsPage`
(default export з `web/src/stats/StatsPage.tsx`) лишаються за іменем, щоб `App.tsx` не змінювати.

Нова сторінка — **чотири вкладки** замість стрічки карток, кожна відповідає на одне питання, з власним набором
графіків і таблицею-«під графіком» (кнопка «таблиця»), спільний період (24 год / 7 д / 30 д / 90 д / довільний,
у хеші `#/stats?p=7d`, `?from&to`) і живий рядок угорі («зараз: N активних треків, M місць під тривогою,
K фактів за останню годину» — з того, що store вже тримає для карти: `tracks`, `alerts`, стрічка `feed`; без нового
ендпоїнта):

1. **Цілі** — «що летіло»: stacked-area за категоріями (перемикач факти / об'єкти-треки), класи (горизонтальні
   стовпчики з часткою), області локації (стовпчики, топ-15 + «інші»), маршрути «звідки → куди» (топ-10 списком +
   теплокарта 8 × 8), година × день тижня.
2. **Тривоги** — годин під тривогою за бакет (стовпчики) + кількість оголошених (лінія), по областях (стовпчики годин
   з підписом кількості), тривалість (гістограма), розподіл за годинами доби (коли оголошують), «топ днів».
3. **Джерела** — таблиця (повідомлень, оброблено, з фактами %, цілей, медіанна затримка, спарклайн) і stacked-стовпчики
   повідомлень за бакет за джерелами.
4. **Розпізнавання** — типи подій, метод ідентифікації, впевненість, точність локації (чотири горизонтальні
   розподіли) і частка повідомлень без фактів за бакет.

Графіки — власні SVG-компоненти (`web/src/stats/charts/`: `AreaStack`, `Columns`, `HBars`, `Heatmap`, `Sparkline`,
`Histogram`) з **осями, сіткою, підписами значень, підказкою при наведенні (одна для всього ряду), клавіатурною
навігацією по бакетах**, кольори — з тем (`palette.ts` через CSS-змінні, як мапа), без бібліотек (у `package.json`
графічних немає; додавати не будемо — розмір бандла і теми).

Бекенд — чотири ендпоїнти замість одного: `GET /api/stats/targets|alerts|sources|recognition?from&to` (той самий
контракт періоду: `to ≤ now`, ≤ 366 днів, бакет hour/day/week за довжиною, `Europe/Kyiv`, кеш 2 хв на ключ
`section|from|to`, `IMemoryCache` уже є). Вкладка вантажить лише свій payload — сторінка відкривається за один
запит, а не чекає всі 15. SQL-агрегати за зразком старого `StatsService` (правила `SqlQuery` там задокументовані);
математику інтервалів тривог (обрізання межами періоду, розрізання по бакетах, гістограма тривалості) переписати в
`AlertIntervals` (чиста функція) з unit-тестами — стара `StatsAggregator` була правильна, але зв'язана з
`StatsDto`; нові тести покривають ті самі випадки (інтервал через межу періоду, відкритий інтервал, DST-доба).

### 2.7. Адмін «Аналітика» — заново

Видаляється вміст `web/src/components/analytics/AnalyticsPanel.tsx`; новий код — у `web/src/admin/analytics/*`, а
старий шлях стає реекспортом (`export { default } from '../../admin/analytics/AnalyticsPanel'`), щоб `AdminApp.tsx`
(його править інший агент) не залежав від цієї роботи. Меню: «Аналітика → Хто кого копіює» (`#/analytics`, як є) і
новий пункт «Аналітика → Стан сервісу» (id `analytics-service`, хеш `#/analytics-service`) — обидва рендерять
`<AnalyticsPanel />` без пропів; компонент сам читає `window.location.hash` і відкриває вкладку «Сервіс», коли хеш
`#/analytics-service`. Пункт меню додає агент B, компонент — агент D; контракт між ними — лише рядок хеша.

Вкладки всередині:
1. **Огляд** — KPI періоду (постів, копій, частка унікальних, медіанна затримка копіювання), «хто кого» —
   теплокарта копіювальник × першоджерело з кліком у список пар; графік копій за днями.
2. **Джерела** — картка на джерело: пости/копії/скопійовано за днями (стовпчики), унікальність, затримка копіювання
   (avg/median), години доби (24 смужки), пересилання (внутрішні/зовнішні); сортування за унікальністю/обсягом.
3. **Пари** — таблиця пар з фільтром за копіювальником/першоджерелом, розподіл затримок пари (гістограма з новим
   `GET /analytics/pairs/{copierId}/{originalId}?days` — `DelayHistogramDto` за бакетами 1/5/15/60/360 хв,
   верхні 20 прикладів).
4. **Першоджерела** — хто перший відкриває треки за категоріями (`firsts`), таблиця + стовпчики частки.
5. **Знахідки** — останні пари з обома текстами; фільтри `kind` (near/verbatim/forward), джерело, лише primary;
   спільні слова підсвічені (клієнт: перетин токенів, без нових ендпоїнтів).
6. **Сервіс** — heartbeat/версія (з `Runtime:Worker:analytics:Status`), watermark, backlog, останні запуски
   (таблиця з тривалістю і швидкістю msg/s), розмір схеми, міграції; кнопка «Перебудувати» (`reset`, підтвердження).

Бекенд: наявні `status/report/recent/reset` лишаються; `recent` отримує `kind` і `primaryOnly`; додається
`pairs/{copier}/{original}`; `report` — без змін (усе для вкладок 1, 2, 4 у ньому є).

## 3. Роботи і розбиття на агентів

Файли не перетинаються між агентами; `docs/README.md` не чіпає ніхто — кожен агент віддає текст для README у звіті,
інтеграцію в README робить координатор наприкінці. DTO для §2.1/§2.4 координатор пише **до** запуску агентів у
`src/Puluj.Contracts/OpsDtos.cs`, щоб A і B збиралися незалежно.

**Паралельні збірки.** Чотири агенти в одному робочому дереві (git worktree не підходить: 150 staged-файлів ще не
закомічені) одночасно запускатимуть `dotnet build`/`dotnet test`/`npm run build` — спільні `obj/`, `bin/`,
`tsconfig.tsbuildinfo`, `wwwroot` (Vite чистить теку) зіткнуться. Правило: **кожна** така команда — через
`scripts/with-lock.ps1 <команда…>` (іменований м'ютекс `Global\Puluj.Build`, очікування до 20 хв; пише
координатор до запуску). `vitest run` і `dotnet test --no-build` теж під замком (тести теж збирають). Кожен агент
збирає **лише свої** проєкти (`dotnet build src/…/X.csproj`, `dotnet test tests/…`), не `Puluj.sln`; помилка компіляції
в чужому проєкті, якого агент не чіпав, — ознака чужої правки посеред роботи: почекати хвилину і повторити, не
«чинити». Фінальну збірку всього рішення робить координатор.

### A. Телеметрія Worker (`Puluj.Processing`, `Puluj.Worker`, `Puluj.Analytics.Worker`, `Puluj.Infrastructure` міграція)
1. `Processing/Pipeline/ProcessingStats.cs`: лічильники, кільцеві буфери 4 стадій (2 000 вимірів, з часом запису —
   для вікна 5 хв), claims, `PerMinute(1|5)` за кільцем часових міток; `Snapshot()` → `ProcessingStatusDto`.
   Unit-тести: p50/p90 на відомому наборі, вікно 5 хв, claims add/remove.
2. `RawMessageProcessor`: `stats.Record(parse, lock, store, total)`, `stats.Outcome(...)` поряд із метриками;
   `raw.ProcessingMs = (int)sw.ElapsedMilliseconds` перед фінальним `SaveChangesAsync` (успіх). `ProcessingLoop`:
   `stats.Claimed(id)` / `Released(id)` навколо `ProcessAsync`.
3. `RawMessage.ProcessingMs int?`, `RawMessageConfiguration` (без індексу), міграція `AddRawMessageProcessingMs`
   (`scripts/add-migration.ps1`; Up — одна колонка).
4. `LlmBreaker`: публічні `PausedUntil`/`Reason`/лічильники (якщо ще немає) → `LlmStatusDto`.
5. `Worker/Hosting/WorkerStatusReporter.cs` (10 с; версія, CPU, RSS, ролі, `Paused` з `ReprocessService.PausedAsync`);
   видалення ключа у `StopAsync`. `AnalyticsHeartbeat` → додатково пише `Status` без `Processing`.
6. Тести: `ProcessingStatsTests`; інтеграційний (`PipelineTests`) — після обробки `processing_ms` не NULL.

### B. Адмін: Docker, воркери, конвеєр (`Puluj.Admin`, `deploy/`, `web/src/admin/*`, `web/src/api/admin.ts`)
1. `Admin/Docker/DockerOptions.cs`, `DockerService.cs` (§2.3), `DockerCommand` — побудова аргументів як чисті
   функції (unit-тести в новому `tests/Puluj.Admin.Tests`: scale-команда, фільтр захищених сервісів, парсинг
   `docker ps`/`docker stats` JSON із зразків).
2. `OpsEndpoints`: `workers`, `containers`, дії, `scale`, `pipeline` (§2.4); видалити `processing` і його DTO з
   `AdminDtos.cs`; `Program.cs` — `AddOptions<DockerOptions>`, `AddSingleton<DockerService>`.
3. `deploy/Dockerfile.admin` (docker-cli + compose plugin), `docker-compose.yml` (сокет, `/deploy:ro`, env).
4. UI: `admin/shared.tsx` (винесені хелпери), `admin/WorkersPanel.tsx`, `admin/PipelinePanel.tsx`, правки
   `OpsPanels.tsx` (видалити `ProcessingPanel`, імпорт shared), `AdminApp.tsx` (навігація: Воркери, Конвеєр; пункт
   «Аналітика → Стан сервісу» на `#/analytics/service`), `api/admin.ts` (типи + виклики).
5. vitest: побудова рядків стану/форматування; ручна перевірка — лише після деплою користувачем (`docker compose up
   -d --build`), бо запуск compose із сесії заблоковано; агент перевіряє `dotnet build`, тести, `npm run build`.

### C. Публічна «Статистика» (`Puluj.Api` stats, `web/src/stats/*`, `web/src/api/client.ts`, `Contracts/Dtos.cs` stats-частина, `tests/Puluj.Api.Tests/StatsAggregatorTests.cs` → `AlertIntervalsTests.cs`)
Повністю за §2.6. Не чіпати `App.tsx` (імена експортів збережено), `useStore`, карту.

### D. Адмін «Аналітика» (`web/src/components/analytics/AnalyticsPanel.tsx` → реекспорт, новий `web/src/admin/analytics/*`, `web/src/api/analytics.ts`, `Puluj.Admin/Endpoints/AnalyticsEndpoints.cs`, `Puluj.Analytics/Reporting/*`, `Contracts/AnalyticsDtos.cs`)
Повністю за §2.7. Не чіпати `AdminApp.tsx`, `OpsEndpoints`, `admin.ts` (крім `adminCall`, який уже експортується).

### Після агентів (координатор)
`docs/README.md`: «Як влаштовано» (admin — керування контейнерами), «Запуск» (опції `Docker__*`, права на сокет),
«Статистика», «Аналітика джерел», «Експлуатація» (Воркери/Конвеєр, `processing_ms`); `dotnet build`, усі тести,
`npm run build`; деплой — користувач: `cd deploy; docker compose up -d --build` (нова міграція, новий образ admin із
docker CLI), потім перевірка розділу «Воркери» і масштабування.

## 4. Ризики і межі

- Сокет Docker в `admin` = root на хості: панель уже має роль `puluj_admin` і токен; тримати `admin` поза
  публічним інтернетом (уже сказано в compose). Захищені сервіси перевіряються на сервері, не лише в UI.
- Compose з іншого робочого каталогу (`/deploy` у контейнері замість `deploy/` на хості): `--no-recreate --no-deps`
  гарантує, що зачепить лише `processor`; `--env-file .env` — той самий `.env`. Файл `docker-compose.override.yml`
  (gitignored) монтується разом з текою, тому конфіг однаковий.
- `docker stats --no-stream` триває ~2 с — кешувати 5 с у `DockerService`, опитування панелі — 5 с.
- Масштабування до 0 = зупинка обробки; UI попереджає. Репліка, зупинена кнопкою, лишає claim-и — їх поверне ліз
  (5 хв) або `ReleaseAsync` при чистому SIGTERM (`docker stop` дає 10 с — досить).
- `processing_ms` записується лише для успіху; для Skipped/Failed — NULL (панель показує «—»).
- Локальний `dev-run.ps1`: `Docker:Enabled=false` → усе, крім контейнерних кнопок, працює однаково.

## Ревʼю 1

1. Чотири агенти в одному дереві збиратимуть код одночасно — `obj/`, `tsbuildinfo`, `wwwroot` зіткнуться; worktree
   неможливий через незакомічений стан. → `scripts/with-lock.ps1` (іменований м'ютекс) для всіх build/test-команд,
   збірка лише своїх проєктів, правило «чужа помилка — почекати». Додано в §3.
2. §2.5 і §2.7 містили чернеткові вагання («… ні: …»). → Прописано однозначно: `ProcessorCount` в `OpsOverviewDto`;
   реекспорт старого шляху `AnalyticsPanel`, другий пункт меню через хеш `#/analytics-service`, без пропів.
3. `AssemblyInformationalVersion` у Docker-збірці без `.git` не дасть sha — репліки різних збірок були б
   нерозрізнювані (саме така ситуація дала дедлоки 15.09). → Додано `BuiltAt` (час запису dll) у `WorkerStatusDto`.
4. `processing_ms` не може містити час коміту, бо пишеться в тій самій транзакції. → Уточнено: час до коміту.
5. «Живий рядок» публічної статистики посилався на «повідомлень за годину», яких store не має. → Факти з `feed`.
6. Спільні хелпери `shared.tsx` створює агент B, тому агент D не може їх імпортувати. → D тримає власний `format.ts`.
Правки внесено.

## Ревʼю 2

1. `PipelineSourceDto.Tracks` «за `first_seen_at`» на джерело неможливий: у `target_tracks` немає `source_id`. →
   Треки джерела = ті, чия перша ціль (`track_targets.sequence = 1`) з цього джерела; `Totals` додатково рахує
   `duplicates` окремо, а не виключає їх (конвеєр звітує про все вироблене).
2. `Puluj.Analytics.Worker` не має посилання на `Puluj.Contracts`, потрібного для `WorkerStatusDto`. → Додати.
3. Перевірено: `IMemoryCache` в Api зареєстровано (`ApiDependencyInjection.cs:33`, `SizeLimit = 64`) — §2.6 коректний;
   `--no-recreate --no-deps` з compose v5.5 підтримуються; `docker stats --no-stream` не потребує TTY.
4. Перевірено відсутність перетинів файлів між A/B/C/D: `Program.cs` Worker — A; `Program.cs` Admin, `Puluj.sln`
   (новий тестовий проєкт) — B; `Contracts/Dtos.cs` (stats) — C; `Contracts/AnalyticsDtos.cs` — D; `OpsDtos.cs` —
   координатор до старту. `web/src/api/types.ts` — лише C; `admin.ts` — лише B; `analytics.ts` — лише D.
Правки внесено.

## Ревʼю 3

Перечитано після правок: контракти між агентами (хеш `#/analytics-service`, `OpsDtos.cs`, реекспорт
`AnalyticsPanel`, `with-lock.ps1`) однозначні; дії над контейнерами перевіряються на сервері; жоден агент не редагує
`README.md`, `App.tsx`, `AdminApp.tsx` крім B. Зауважень немає — до імплементації.

## Статус

План написано 2026-09-15; ревʼю 1–3 внесено; імплементація — чотирма агентами (A–D) паралельно.
