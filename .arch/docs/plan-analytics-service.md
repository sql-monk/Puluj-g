# План: сервіс аналітики джерел (хто кого копіює)

## 1. Мета

Окремий сервіс в окремому контейнері, який читає `raw_messages` і будує **контентну** картину джерел:
хто кого передруковує (дослівно / майже дослівно / переслані пости), з якою затримкою, хто першоджерело,
скільки унікального контенту дає кожне джерело, коли джерела активні, і хто перший бачить цілі кожної категорії.
Плюс сторінка в адмін-панелі з результатами та станом самого сервісу.

Чим це відрізняється від наявного «Рейтингу джерел» (`source_copies`, `source_rating_daily`): той рахує повтор
**фактів** (дублікат цілі за ±3 хв, тригер у БД, залежить від парсера). Тут — порівняння **текстів** повідомлень
незалежно від парсера: повідомлення, які правила не розібрали, теж беруть участь; видно дослівні передруки,
пересилання Telegram (`forwardedFrom`) і затримки в годинах, а не лише в 3-хвилинному вікні дублів.

## 2. Архітектура

```
raw_messages (public) ──read──▶ Puluj.Analytics.Worker (контейнер analytics) ──write──▶ схема analytics
                                                                                             │
Puluj.Admin (/api/admin/analytics/*) ◀──read (роль puluj_admin)──────────────────────────────┘
web/admin.html → розділ «Аналітика» (React)
```

- **`src/Puluj.Analytics`** (бібліотека): `AnalyticsDbContext` (схема `analytics`, власна історія міграцій
  `analytics.__EFMigrationsHistory`), сутності, міграції, алгоритм схожості (`Text/`), `AnalysisRunner`
  (інкрементальний прогін), `AnalyticsReportService` (запити для UI), DTO (`Contracts/`), `AnalyticsOptions`.
- **`src/Puluj.Analytics.Worker`** (хост): мінімальний `WebApplication` (Serilog, OpenTelemetry як у Worker-і):
  `AnalyticsInitializer` (міграції схеми `analytics` при старті), `AnalysisLoop` (BackgroundService з періодом
  `Analytics:Interval`), `AnalyticsHeartbeat` (`Runtime:Worker:analytics:Heartbeat` в `app_settings` — панель
  «Стан» побачить його автоматично), HTTP: `GET /health`, `GET /api/analytics/status`, `GET /api/analytics/report`.
  Аргумент `--healthcheck` — процес робить GET на власний `/health` і виходить з кодом 0/1 (для compose
  healthcheck: в образі aspnet немає curl/wget).
- **Читання сирих даних** — SQL через з'єднання того самого `AnalyticsDbContext` (`Database.SqlQuery<T>`), а не
  через `PulujDbContext`. Обґрунтування: (1) нуль залежностей від `Puluj.Infrastructure`/`Puluj.Domain`, які зараз
  паралельно змінює інший агент (нові колонки `raw_messages`, DI); (2) сервісу потрібні лише 6 колонок
  `raw_messages` + `raw_payload->>'forwardedFrom'`/`'channelId'` і кілька агрегатів по `targets`/`track_targets`/
  `target_tracks` — проєкція в SQL дешевша за завантаження сутностей з jsonb payload; (3) heartbeat — один upsert
  в `app_settings`. Незалежна кодова база = справжній окремий сервіс.
- **Роль БД**: контейнер підключається як власник `puluj` (йому потрібен DDL для власних міграцій), як і
  `migrate`/Worker. Міграція створює схему `analytics` і видає `puluj_admin` `USAGE` + `SELECT/INSERT/UPDATE/DELETE`
  (DELETE потрібен для «скинути й перебудувати» з панелі) та `puluj_reader` `SELECT`, з `ALTER DEFAULT PRIVILEGES`
  для наступних міграцій.
- **Адмін-панель** читає схему `analytics` напряму (як і всі інші `ops/*` — панель уже читає БД для всього);
  `Puluj.Admin` посилається на бібліотеку `Puluj.Analytics` заради `AnalyticsReportService` і DTO. Якщо схеми ще
  немає (сервіс ще не запускали) — ендпоїнти повертають `{ initialized: false }`, сторінка це пояснює.

## 3. Інкрементальний прогін (`AnalysisRunner`)

- **Курсор**: `analytics.state.watermark` = останній оброблений `raw_message_id`. Порядок за `raw_message_id`
  гарантує, що жодне повідомлення не пропускається (при дочитуванні історії Telegram `published_at` приходить не
  монотонно, курсор за часом їх би загубив). Пари шукаються симетрично (див. §4), тому порядок надходження не
  впливає на результат: хто раніше **опублікував**, той оригінал.
- **Пакет**: `SELECT … FROM raw_messages WHERE raw_message_id > @w AND received_at < now() − @SafetyLag ORDER BY
  raw_message_id LIMIT @BatchSize` (500). `SafetyLag` (30 с) — щоб курсор не проскочив рядок з меншим id, транзакція
  якого ще не закомічена (паралельні колектори). Поки пакет повний — наступний одразу; коли порожній — сон
  `Analytics:Interval` (60 с). Курсор читається з БД на початку **кожного** пакета (не кешується в пам'яті) — після
  `reset` з панелі наступний пакет починається з 0. Колонки в SQL — з аліасами в лапках (`AS "RawMessageId"`), бо
  `SqlQuery<T>` для незареєстрованих типів зіставляє за іменем властивості (як `OpsEndpoints`).
- **Транзакція на пакет**: рядки `analytics.messages` (`ON CONFLICT DO NOTHING`), пари (`ON CONFLICT DO UPDATE`),
  курсор — в одній транзакції; крах посеред пакета = повтор пакета без наслідків (ідемпотентність).
- **Прогін** (`analytics.runs`): один рядок на цикл «прокинувся → обробив усе, що є → заснув»: `started_at`,
  `finished_at`, `status` (running/ok/failed), `watermark_from/to`, `messages_scanned`, `messages_fingerprinted`,
  `pairs_found`, `error`, `instance`, `updated_at` (оновлюється після кожного пакета — heartbeat прогону).
  Рядок `running`, чий `updated_at` старший за 10 хв, при старті позначається `failed` («перервано») — так видно
  крах контейнера; перший прогін над 400 тис. повідомлень (20–30 хв) при цьому не хибно «перерваний».
- **Один інстанс**: сервіс розрахований на один контейнер (курсор один). Захист від двох випадкових інстансів —
  `pg_try_advisory_lock(hashtext('puluj.analytics'))` на **окремому відкритому з'єднанні**, яке живе весь
  прогін (лок сесійний; з'єднання з пулу його б «понесло» далі), `pg_advisory_unlock` у `finally`; другий
  інстанс пише в лог і чекає наступного циклу.
- **Після пакетів** (раз на прогін, якщо було щось нове або минуло > 10 хв): перерахунок `analytics.track_firsts`
  за останні `TrackFirstsDays` (30) днів одним SQL (DELETE діапазону + INSERT … SELECT) — див. §5.
- **Скидання** (`POST /api/admin/analytics/reset`): панель робить `TRUNCATE analytics.messages, analytics.copies,
  analytics.track_firsts` + `watermark = 0` (роль `puluj_admin` має TRUNCATE? — ні; тому DELETE, як робить
  `ReprocessService`). Сервіс на наступному циклі перебудовує все з нуля (~400 тис. повідомлень ≈ 10–20 хв).

## 4. Алгоритм виявлення копіювання

1. **Які повідомлення**: лише з `raw_text` (alerts.in.ua — структуровані, без тексту — не індексуються, але і
   не потрібні). Редагування поста зберігаються окремими `RawMessage` з `source_message_id = "{id}:e{ts}"` —
   логічний пост = `(source_id, post_key)`, де `post_key` = `source_message_id` без суфікса `:e\d+`. Редагування
   індексується (може змінити текст), але **пара рахується один раз на логічну пару постів** (унікальність у §5).
2. **Нормалізація** (`TextNormalizer`): NFC → нижній регістр → прибрати URL, `@згадки`, `#хештеги`, емодзі та
   будь-які не-літерно-цифрові символи (крім пробілу) → рядки, які після цього порожні, відкидаються (підписи
   каналів «підписатися», емодзі-рядки) → пробіли згорнути. Результат — «канонічний текст».
3. **Шинґли**: символьні 4-грами канонічного тексту (з пробілами), хешовані FNV-1a 64. Символьні, а не словесні:
   медіанна довжина тексту 58 символів — словесні біграми дали б 5–8 елементів і нестійкий Жаккар; символьні
   стійкі до відмінків і дрібних правок. Мінімум `MinTextLength` = 40 канонічних символів (коротші —
   «Відбій тривоги» тощо — не є копіюванням, це шаблон; у `messages` вони є, але без відбитка).
4. **MinHash** (`MinHasher`): 64 хеш-функції виду `(a·x + b) mod p` (p = 2^61−1), сигнатура 64×uint32 → `bytea`
   256 Б. **LSH**: 16 смуг × 4 значення → 16 ключів `bigint` у `bands bigint[]` + GIN-індекс; ключ смуги =
   hash(номер смуги, v1..v4), щоб однакові значення в різних смугах не давали хибного збігу. Ймовірність
   стати кандидатом при J = 0.5: 1−(1−0.5⁴)^16 ≈ 0.64; при J = 0.7: ≈ 0.99; при J = 0.3: ≈ 0.12 (потім
   відсіюється точною перевіркою; для J = 0.9 — ≈ 1.0). Обсяг: ~400 тис. × ~450 Б ≈ 180 МБ з індексами при базі 2.1 ГБ — прийнятно;
   зберігаємо всі, щоб `reset` з іншими порогами не вимагав переобчислення відбитків (пороги застосовуються при
   перевірці, а не при індексації).
5. **Кандидати** (SQL): `bands && @bands AND source_id <> @src AND published_at BETWEEN @t − PairWindow AND
   @t + PairWindow` (PairWindow = 6 год; симетрично, бо новий рядок може виявитись **оригіналом** для вже
   проіндексованого пізнішого поста — так буває при дочитуванні історії). Вибірка до 200 найближчих у часі
   (`ORDER BY abs(extract(epoch from published_at − @t)) LIMIT 200`), у C# — сортування за кількістю спільних
   смуг, точна перевірка для топ-`CandidateLimit` (50).
6. **Точна перевірка**: тексти кандидатів дочитуються з `raw_messages` (≤ 50 рядків), шинґли перераховуються,
   J = |A∩B|/|A∪B|, containment C = |A∩B|/min(|A|,|B|). Пара приймається, якщо `J ≥ JaccardThreshold` (0.7)
   **або** `C ≥ ContainmentThreshold` (0.85) і коротший текст ≥ `ContainmentMinLength` (80) — containment ловить
   «передрук + свій коментар», а обмеження довжини відсікає шаблонні короткі рядки (див. ревʼю 4).
7. **Вид пари** (`kind`): `forward` — у payload копії `forwardedFrom` має форму `"channel {id}"` (regex
   `^channel (\d+)$`; інша форма — ім'я каналу, наприклад «Приймальня єРадару», — лише зовнішній ref) і цей
   `channelId` належить джерелу оригіналу (мапа `channelId → source_id` збирається з `raw_payload->>'channelId'`
   проіндексованих повідомлень: `SELECT DISTINCT source_id, channel_id FROM analytics.messages` при старті +
   поповнення в пам'яті); `verbatim` — J ≥ 0.9; інакше `near`.
8. **Напрямок**: оригінал = менший `published_at` (при рівності — менший `raw_message_id`); затримка =
   різниця. `is_primary` — серед усіх оригіналів одного логічного поста-копії той, що опублікований найраніше
   (перший у ланцюжку A→B→C отримує кредит як першоджерело; проміжні ланки лишаються в таблиці як
   `is_primary = false` — «у кого читає»). Прапорець перераховується для **кожного `copy_*` логічного поста**,
   якого торкнувся upsert, з якого б боку (копія чи оригінал) пара не прийшла.
9. **Пересилання з-поза системи**: `forwardedFrom` є, але канал не наш → у `messages.forwarded_external = true`
   і `forwarded_from` (текстовий ref) — дає таблицю «звідки джерела пересилають найчастіше».

Що свідомо не робиться: порівняння в межах одного джерела (редагування, повтори того ж каналу), медіа без тексту,
LLM-семантика. Той самий шаблонний текст у двох офіційних каналів (наприклад «Відбій тривоги в Київській
області») довжиною ≥ 40 символів **буде** парою — це чесна «копія» на рівні контенту; сторінка показує
частку `verbatim`, тож видно, що це шаблон, а не аналітика.

## 5. Модель даних (схема `analytics`)

| Таблиця | Колонки | Індекси |
|---|---|---|
| `state` | `key pk`, `value text`, `updated_at` — `watermark` | — |
| `runs` | `run_id identity pk`, `instance`, `started_at`, `finished_at?`, `status smallint`, `watermark_from`, `watermark_to`, `messages_scanned`, `messages_fingerprinted`, `pairs_found`, `error?` | `(started_at desc)` |
| `messages` | `raw_message_id pk`, `source_id`, `post_key varchar(256)`, `is_edit bool`, `published_at`, `channel_id bigint?`, `forwarded_from varchar(128)?`, `forwarded_source_id int?`, `forwarded_external bool`, `text_length int` (канонічна), `shingle_count int`, `minhash bytea?`, `bands bigint[]?`, `indexed_at` | `(published_at)`, GIN `(bands)` where not null, `(source_id, published_at)`, `(source_id, channel_id)` |
| `copies` | **pk** `(copy_source_id, copy_post_key, original_source_id, original_post_key)` — одна пара на логічну пару постів (редагування оновлюють рядок); `copy_raw_message_id`, `original_raw_message_id` (останні версії), `copy_published_at`, `original_published_at`, `delay_seconds double`, `jaccard real`, `containment real`, `kind smallint` (1 near, 2 verbatim, 3 forward), `is_primary bool`, `found_at` | `(copy_published_at)`; `(copy_source_id, original_source_id, copy_published_at)`; `(original_raw_message_id)` |
| `track_firsts` | `day date`, `source_id`, `target_category_id`, `category_code varchar(64)`, `firsts int`, `participations int`, `lag_seconds_sum double`, `lag_count int` — pk `(day, source_id, target_category_id)` | — |

`track_firsts`: для кожного `target_track` за день (`first_seen_at`, Europe/Kyiv) — джерело найранішої цілі
(`track_targets` → `targets` за `observed_at, target_id`) отримує `firsts++`; кожне джерело, що є в треку, —
`participations++` і `lag = min(observed_at джерела) − first_seen` у `lag_seconds_sum/lag_count` (для
не-перших). Лише треки з ≥ 2 різних джерел для `lag`, `firsts` — усі. Це «швидкість джерела по категоріях цілей».

Агрегати «джерело за день», «пара за день» **не** зберігаються — рахуються запитом над `messages`/`copies`
(30 днів ≈ 60 тис. рядків, індекси за часом) — жодних лічильників, які розходяться з фактами.

## 6. Ендпоїнти

`Puluj.Admin`, група `/api/admin/analytics` (фільтр `AdminEndpoints.AuthorizeAsync`); `GET status` також містить
`schemaBytes` (розмір схеми `analytics`, бо панель «База даних» показує лише `public`):

| Endpoint | Відповідь |
|---|---|
| `GET status` | `AnalyticsStatusDto`: `initialized`, `watermark`, `latestRawMessageId`, `backlog`, `heartbeatAt`, `lastRun` (`RunDto`), `runs` (останні 20), `messagesIndexed`, `pairsTotal`, `schemaMigrations` |
| `GET report?days=14` | `AnalyticsReportDto`: `days[]`, `sources[]` (`SourceAnalyticsDto`: id, name, posts, edits, copies, copiedBy, uniqueShare, avgCopyDelaySeconds, medianCopyDelaySeconds, avgLeadSeconds, verbatimShare, forwardsInternal, forwardsExternal, perHour[24] (Kyiv), perDay[] posts/copies/copiedBy), `pairs[]` (`CopyPairDto`: copierId, originalId, count, verbatim, forwards, avgDelay, medianDelay, minDelay), `matrix` (індекси = sources), `externalForwards[]` (sourceId, channelRef, count), `firsts[]` (`TrackFirstDto`: sourceId, categoryCode, firsts, participations, avgLagSeconds) |
| `GET recent?limit=30&sourceId=` | `RecentCopyDto[]`: обидва тексти (обрізані до 300), джерела, затримка, J, kind, URL — для перевірки алгоритму «на око» |
| `POST reset` | видаляє `messages`/`copies`/`track_firsts`, `watermark = 0`; `{ ok: true }` |

`Puluj.Analytics.Worker` (порт 8082 у Docker / 5259 локально): `GET /health` (200 якщо є heartbeat-петля і останній
прогін не `failed` двічі поспіль, інакше 503), `GET /api/analytics/status|report` — ті самі DTO (сервіс можна
опитати й без панелі).

## 7. UI — розділ «Аналітика» в адмін-панелі (`web/src/components/analytics/AnalyticsPanel.tsx`)

Нова група навігації «Аналітика» (додати в масив груп `['Моніторинг', 'Налаштування']` в JSX `AdminApp`, інакше
пункт не відобразиться) → пункт «Джерела: хто кого копіює» (`#/analytics`). `posts` = `count(distinct post_key)`,
`edits` = рядків − постів. Опитування статусу кожні 10 с,
звіту — кожні 60 с або при зміні періоду (7/14/30/60 днів).

1. **Стан сервісу** — бейдж (працює/застарів/не запускався), останній прогін (коли, тривалість, статус, скановано,
   пар), відставання (`backlog` повідомлень до курсора), проіндексовано/пар усього, таблиця останніх прогонів з
   помилками; кнопка «Скинути й перебудувати» з підтвердженням.
2. **Джерела** — таблиця: пости, копій (%), скопійовано іншими, унікальність, середня/медіанна затримка копіювання,
   випередження, частка дослівних, пересилань (своїх/зовнішніх), 24-стовпчикова гістограма активності за годинами
   (Київ; компонент `Bars` з `OpsPanels` винести в спільний `components/Bars.tsx` — або скопіювати, щоб не
   чіпати `OpsPanels`; рішення: винести, бо `OpsPanels` не в зоні іншого агента... ні, він може її торкнутися для
   написів процесорів — тому **скопіювати** маленький компонент у `analytics/`).
3. **Хто кого копіює** — матриця-теплокарта: рядки = копіювальник, стовпці = оригінал, клітинка = кількість (тон за
   часткою від постів копіювальника), tooltip — середня затримка, дослівних, пересилань. Під нею — топ-пар таблиця.
4. **Хто перший бачить** — таблиця джерело × категорія цілей (`firsts`, частка від участей, середнє відставання).
5. **Зовнішні пересилання** — топ каналів поза системою, звідки пересилають (підказка «додати як джерело»).
6. **Останні знахідки** — список пар з обома текстами, затримкою й видом (для перевірки якості порогів).

`web/src/api/admin.ts`: додати `admin.analytics.{status, report, recent, reset}` і типи (у `api/analytics.ts`, щоб
не роздувати `admin.ts`). `OpsPanels.tsx` `SERVICE_LABEL`: додати `'worker:analytics': 'Analytics (аналітика джерел)'`
— одна вставка, перечитати файл перед правкою.

## 8. Розгортання і конфігурація

- `deploy/Dockerfile.analytics` — за зразком `Dockerfile.worker` (publish `Puluj.Analytics.Worker`, база `aspnet:10.0`,
  `EXPOSE 8082`, `HEALTHCHECK` не у Dockerfile, а в compose).
- `deploy/docker-compose.yml` — сервіс `analytics`: `build`, `env_file: .env`, `ConnectionStrings__Puluj` як у
  Worker (власник), `ASPNETCORE_URLS=http://+:8082`, `Serilog__WriteTo__1__Args__path=/app/logs/analytics-.log`,
  `volumes: logs`, `depends_on: postgis healthy, migrate completed`, `healthcheck: ["CMD", "dotnet",
  "Puluj.Analytics.Worker.dll", "--healthcheck"]` interval 30 s, `restart: unless-stopped`. Порт не публікується
  назовні (панель читає БД). Коментар-схему вгорі файлу доповнити рядком `analytics`. Правка мінімальна,
  файл перечитати безпосередньо перед нею.
- `scripts/dev-run.ps1` — запуск `Puluj.Analytics.Worker` (порт 5259, лог `%TEMP%\puluj-analytics.log`) і зупинка
  в `-Stop`; `launchSettings.json` з портом 5259.
- `appsettings.json` секція `Analytics`: `Name` (analytics), `Interval` (00:01:00), `BatchSize` (500), `SafetyLag` (00:00:30),
  `PairWindow` (06:00:00), `MinTextLength` (40), `JaccardThreshold` (0.7), `ContainmentThreshold` (0.85), `ContainmentMinLength` (80),
  `VerbatimThreshold` (0.9), `CandidateLimit` (50), `TrackFirstsDays` (30). Усе перевизначається `Analytics__Key`.
- Метрики (meter `Puluj.Analytics`): `puluj.analytics.messages.indexed`, `puluj.analytics.pairs.found{kind}`,
  `puluj.analytics.run.duration` (histogram, s), `puluj.analytics.backlog` (observable gauge).
- Логи: `logs/analytics-<день>.log` — панель «Логи» підхопить файл автоматично (LogReader читає теку).

## 9. Тести

- `tests/Puluj.Analytics.Tests` (xunit, без БД): `TextNormalizerTests` (емодзі/URL/регістр/підписи каналів),
  `ShinglerTests`, `MinHashTests` (оцінка Жаккара ≈ точній на синтетичних множинах, ±0.15 при 64 хешах;
  детермінованість), `LshTests` (ідентичні тексти → всі смуги збігаються; текст із дописаним реченням → ≥ 1
  спільна смуга; різні тексти → 0), `CopyDetectorTests` (напрямок за `published_at`, `post_key` з редагувань,
  kind forward/verbatim/near).
- Інтеграційний `AnalysisRunnerTests` (Testcontainers PostGIS або `PULUJ_TEST_CONNECTION`, як у
  `PipelineTests`; без Docker — no-op): застосувати міграції `PulujDbContext` (потрібна `raw_messages`) і
  `AnalyticsDbContext`, вставити 2 джерела і 4 повідомлення SQL-ом (оригінал, дослівна копія через 5 хв,
  копія з дописом через 40 хв, незалежний текст), прогнати `RunOnceAsync` двічі → 2 пари, `is_primary` в
  оригіналу, повторний прогін нічого не дублює, курсор = max id; `reset` → повторний прогін відтворює те саме.

## 10. Документація

`docs/README.md`: рядок у таблиці «Як влаштовано» (**Puluj.Analytics**), абзац у «Джерела» або новий підрозділ
«Аналітика джерел» після «Рейтинг джерел», сервіс `analytics` у списку контейнерів, ендпоїнти в таблиці API,
`Analytics__*` у таблиці ключів. `.env.example` — без змін (нових секретів немає).

## 11. Файли

Нові:
- `src/Puluj.Analytics/Puluj.Analytics.csproj`, `AnalyticsOptions.cs`, `DependencyInjection.cs`
- `src/Puluj.Analytics/Persistence/AnalyticsDbContext.cs` (`HasDefaultSchema("analytics")`,
  `UseSnakeCaseNamingConvention`, `MigrationsHistoryTable("__EFMigrationsHistory", "analytics")`), `Entities.cs`,
  `AnalyticsDesignTimeFactory.cs`, `Migrations/…_InitialAnalytics.cs` (+ snapshot; генерується `dotnet ef` з
  `-o Persistence/Migrations`; у кінець `Up()` вручну — `GRANT USAGE ON SCHEMA analytics`, права таблиць і
  `ALTER DEFAULT PRIVILEGES` для `puluj_admin`/`puluj_reader`)
- `src/Puluj.Analytics/Text/TextNormalizer.cs`, `Shingler.cs`, `MinHasher.cs`, `TextFingerprint.cs`
- `src/Puluj.Analytics/Analysis/AnalysisRunner.cs`, `CopyDetector.cs`, `RawMessageReader.cs`, `TrackFirstsBuilder.cs`
- `src/Puluj.Analytics/Reporting/AnalyticsReportService.cs`, `Contracts/AnalyticsDtos.cs`
- `src/Puluj.Analytics.Worker/Puluj.Analytics.Worker.csproj`, `Program.cs`, `AnalysisLoop.cs`, `AnalyticsHeartbeat.cs`,
  `AnalyticsInitializer.cs`, `appsettings*.json`, `Properties/launchSettings.json`
- `src/Puluj.Admin/Endpoints/AnalyticsEndpoints.cs`
- `deploy/Dockerfile.analytics`
- `web/src/api/analytics.ts`, `web/src/components/analytics/AnalyticsPanel.tsx`
- `tests/Puluj.Analytics.Tests/*`
- `scripts/add-analytics-migration.ps1`

Змінені: `Puluj.sln` (`dotnet sln add`), `src/Puluj.Admin/Puluj.Admin.csproj` (посилання), `src/Puluj.Admin/Program.cs`
(`AddPulujAnalyticsReporting` + `MapAnalyticsEndpoints`), `web/src/admin/AdminApp.tsx` (NAV + секція),
`web/src/admin/OpsPanels.tsx` (один рядок `SERVICE_LABEL`), `deploy/docker-compose.yml` (сервіс), `scripts/dev-run.ps1`,
`docs/README.md`.

---

## Ревʼю 1

Знахідки (перевірено проти коду):

1. **`DependencyInjection.AddPulujInfrastructure` реєструє `AddScoped(PulujDbContext)`; `Puluj.Admin` уже має його.**
   Якщо `Puluj.Analytics` зареєструє `AddDbContextFactory<AnalyticsDbContext>` — конфліктів немає (інший тип).
   Але `UseSnakeCaseNamingConvention` треба застосувати і до `AnalyticsDbContext`, інакше EF створить `RawMessageId`
   замість `raw_message_id` — сирі SQL-запити у звіті це припускають. → Додати до плану явно: конвенція snake_case
   та `HasDefaultSchema("analytics")`; **історія міграцій** через `MigrationsHistoryTable("__EFMigrationsHistory",
   "analytics")` — інакше обидва контексти пишуть в `public.__EFMigrationsHistory`, і `PulujDbContext.Migrate()` у
   Worker-і побачить «чужі» міграції (він їх проігнорує, але `GetAppliedMigrations` в панелі «База даних» покаже
   зайве). Виправлено в §2 (уже було), уточнено в §11.
2. **Роль `puluj_admin` і `TRUNCATE`**: `AddDbRoles` дає лише `SELECT, INSERT, UPDATE, DELETE`. План §3 уже каже DELETE.
   Але `DELETE FROM analytics.messages` на 400 тис. рядків — секунди, прийнятно. ОК.
3. **`ALTER DEFAULT PRIVILEGES IN SCHEMA analytics`** діє лише для об'єктів, які створює **та сама роль**, що
   виконала команду (власник `puluj`) — саме вона й мігрує. ОК. Але `GRANT USAGE ON SCHEMA analytics TO
   puluj_admin, puluj_reader` має йти **після** `CREATE SCHEMA` в тій самій міграції — EF `EnsureSchema` генерується
   в `Up()` першим, `migrationBuilder.Sql(...)` — додати в кінець `Up()` вручну. Врахувати в §5/§11.
4. **Watermark за `raw_message_id` і ідентичність без прогалин**: identity може мати «дірки» (відкочені вставки)
   — це не проблема: `> @w` пропускає дірки. Але є ризик **невидимих ще закомічених** рядків: транзакція колектора
   з меншим id може закомітитись пізніше за транзакцію з більшим id; якщо прогін встиг узяти більший id як курсор,
   менший загубиться назавжди. `RawMessageIngestor` вставляє по одному повідомленню в короткій транзакції, але
   при кількох колекторах паралельно вікно є. → Мітигація: брати лише рядки з `received_at < now() − 30 s`
   (`SafetyLag`), тобто курсор ніколи не проходить повз рядок, транзакція якого ще може бути відкрита. Додано
   до §3 і конфігурації (`SafetyLag` = 00:00:30).
5. **Симетричний пошук і `is_primary`**: коли новий рядок виявляється оригіналом для вже проіндексованої копії,
   треба перерахувати `is_primary` **у тієї копії** (у неї з'явився раніший оригінал). План §4.8 каже «для копії
   при кожному upsert» — ОК, але треба явно: перерахунок для `copy_raw_message_id` кожної вставленої/оновленої
   пари, незалежно від того, з якого боку прийшли. Уточнено.
6. **Редагування і унікальність `(copy_source_id, copy_post_key, original_source_id, original_post_key)`**: PK
   `(copy_raw_message_id, original_raw_message_id)` + цей unique — при редагуванні копії другий рядок з іншим
   `copy_raw_message_id` порушить unique → потрібен `ON CONFLICT (copy_source_id, copy_post_key,
   original_source_id, original_post_key) DO UPDATE` (а не по PK). PK тоді зайвий → зробити unique-ключ **первинним**,
   а `raw_message_id`-и — звичайними колонками (оновлюються на новіші `raw_message_id` при редагуванні). Виправлено
   в §5.
7. **Redagування оригіналу**: копія могла збігтися з відредагованою версією оригіналу (`original_post_key` той
   самий) — той самий upsert, ОК. Але **лічильник постів** джерела має рахувати логічні пости: `count(distinct
   post_key)` — уточнено в §7 (`posts` = distinct post_key, `edits` = рядки − пости).
8. **`bands && @bands` з GIN на `bigint[]`**: оператор `&&` для масивів підтримується GIN (`array_ops`). Часткова
   умова `WHERE bands IS NOT NULL` в індексі — ОК для планувальника, бо запит теж не матиме null. Але ключ смуги має
   включати **номер смуги**, інакше однакові 4 значення у різних смугах дадуть хибний збіг: ключ = hash(bandIndex,
   v1..v4). Уточнено в §4.4.
9. **Колізії кандидатів на шаблонні тексти**: в `war_monitor`/`eRadarrua` десятки тисяч постів «БпЛА курсом на X»
   довжиною > 40 — довгий список кандидатів у 12-годинному вікні для кожного нового (сотні). Ліміт 50 за
   `published_at` найближчі — ризик втратити справжній оригінал. → Впорядкувати кандидатів **за кількістю спільних
   смуг** у SQL: `cardinality(ARRAY(SELECT unnest(bands) INTERSECT SELECT unnest(@bands)))` — дорого на сотнях
   рядків, але прийнятно (≤ 16 елементів). Альтернатива дешевша: `LIMIT 200` за часом, спільні смуги в C#,
   верифікувати топ-50. Обрано альтернативу. Крім того, цілі повідомлення «Волинь: … Черкащина: …» з 5 рядками у
   `raketa_trevoga` кожні 5 хв — сусідні пости того самого джерела не порівнюються (source_id <>), а з інших джерел
   такі списки різні. ОК.
10. **Healthcheck через `dotnet Puluj.Analytics.Worker.dll --healthcheck`** запускає другий процес .NET кожні
    30 с (~100 МБ RAM, ~1 с) — прийнятно для одного контейнера, так само роблять інші .NET-образи без curl. Але
    хост має **не** запускати Serilog-файл/OTel у режимі healthcheck — перевірка аргументу до побудови хоста.
    Уточнено в §2.
11. **Час доби за Києвом у SQL**: `published_at AT TIME ZONE 'Europe/Kyiv'` — у postgis-образі tzdata є, у
    `SnapshotService` вже використовують `TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")` в C#. У SQL
    використати те саме ім'я. ОК.
12. **`track_firsts` і `first_seen_at` треку**: перебудова (`ReprocessService`) видаляє й перестворює треки з
    новими id — агрегати за днями від цього не залежать (DELETE діапазону днів + INSERT), ОК. Але треки
    **дублікатів** (`duplicate_of_target_id`) — дубль прив'язаний до треку оригіналу через provenance, тобто
    `track_targets` містить і дубль → джерело-дубль коректно отримує `participation` з lag > 0. ОК.
13. **Тести інтеграційні** потребують міграцій `PulujDbContext` → посилання на `Puluj.Infrastructure` з тестового
    проєкту; якщо збірка Infrastructure тимчасово зламана іншим агентом, тест не збереться — це лише тести, і
    план передбачає повтор збірки. Прийнятно.
14. **`AdminApp.tsx`**: `SectionId` — union-тип і `NAV` з групами `['Моніторинг', 'Налаштування']` захардкоджені
    в JSX — треба додати третю групу `'Аналітика'` у цей масив, інакше пункт не відобразиться. Уточнено в §7.
15. **Пропозиція**: у `report` додати `chains` (ланцюжки A→B→C за не-primary парами) — відкладено, матриця +
    `is_primary=false` пари вже дають «у кого читає». Не робимо.

Правки внесено в план вище (§2, §3, §4, §5, §7). Повертаюсь до ревʼю.

## Ревʼю 2

1. **`SafetyLag` і backfill**: історичні повідомлення мають `received_at` = момент дочитування (свіжий), тому
   30-секундний лаг їх не затримує надовго. ОК.
2. **Мапа `channelId → source_id`**: `forwardedFrom` у payload має форму `"channel 1463721328"` або ім'я
   («Приймальня єРадару»), а `channelId` джерела — число. Парсер: `^channel (\d+)$` → число; інакше — зовнішній
   ref як є. Відображення на джерело можливе лише для числової форми. Уточнено в §4.7/4.9.
3. **Кандидати без відбитка**: `messages` з `bands IS NULL` (короткі) в запит не потрапляють. ОК.
4. **Перший запуск на 400 тис. повідомлень**: 500 у пакеті × (1 SQL кандидатів + 1 SQL текстів + upsert) —
   ~3–5 мс на повідомлення → 20–30 хв. Прогін «running» довше 1 год позначався б `failed` при рестарті — ліміт
   підняти до 6 год, або краще: heartbeat прогону (`runs.updated_at` оновлюється на кожному пакеті), «перервано»
   = `updated_at` старше 10 хв. Виправлено в §3.
5. **Панель «База даних»** показує таблиці лише `schemaname = 'public'` — таблиці `analytics` там не буде; це
   не помилка (їх показує сторінка аналітики: розмір схеми в `status`). Додано `schemaBytes` у `status`.
6. **`GET /api/admin/analytics/recent`** читає `raw_messages.raw_text` — `puluj_admin` має SELECT на public. ОК.
7. **Один advisory lock на сесію**: `pg_try_advisory_lock` прив'язаний до сесії; з пулом Npgsql з'єднання
   повертається в пул і лок «висить» на ньому. → Тримати **окреме відкрите з'єднання** на час прогону
   (`NpgsqlConnection` поза пулом: `Pooling=false` не потрібно — просто не закривати до кінця прогону,
   `pg_advisory_unlock` у `finally`). Уточнено в §3.
8. **Дублювання `Bars`**: копія ~15 рядків — прийнятно, зазначено.

Правки внесено (§3 heartbeat прогону, §4 парсер forwardedFrom, §6 schemaBytes). Повертаюсь до ревʼю.

## Ревʼю 3

1. `AnalyticsDbContext` без сутностей public-схеми, але `Database.SqlQuery<T>` з `record` — властивості мапляться
   за іменами колонок **як є** (без naming convention для keyless типів у SqlQuery) → у SQL використовувати
   `AS "RawMessageId"` тощо, як робить `OpsEndpoints` (`AS "Value"`) — або `record` з властивостями у
   snake_case? EF мапить SqlQuery за `PropertyName` ↔ ім'я колонки з урахуванням naming convention для
   **зареєстрованих** типів; для ad-hoc типів — за іменем властивості. `OpsEndpoints.HourRow` використовує
   `SELECT … AS hour` і властивість `Hour` — Npgsql повертає без лапок нижній регістр, а EF зіставляє без
   урахування регістру. Отже, колонки `raw_message_id` ↔ `RawMessageId` **не** зіставляться. → Використовувати
   `AS raw_message_id`... ні: зіставлення за іменем без регістру означає `rawmessageid` ≠ `raw_message_id`. Отже
   в SQL писати аліаси `AS "RawMessageId"` (у лапках) або `AS rawmessageid`. Рішення: аліаси в лапках у стилі
   `OpsEndpoints` (`AS "Value"`). Зафіксовано в §3.
2. `reset` з панелі + одночасний прогін сервісу: сервіс посеред пакета робить upsert після DELETE → часткові
   дані, а курсор сервісу в пам'яті? Курсор читається з БД **на початку кожного пакета** (не кешується) →
   після reset наступний пакет почнеться з 0; рядки, вставлені пакетом, що йшов під час reset, — коректні
   (вони будуть перевставлені з `ON CONFLICT DO NOTHING`, пари — upsert). Уточнено в §3.
3. Порт 8082 у compose не публікується — `dev-run.ps1` локально 5259. ОК.

Ревʼю 3: суттєвих зауважень немає — правки косметичні (§3 аліаси, курсор з БД щопакета) внесено. Переходжу до
імплементації.

## Ревʼю 4 (під час імплементації, за фактами)

1. **Поправка до ревʼю 3.1**: `SqlQuery<T>` для незареєстрованих типів **застосовує** naming convention контексту
   (`OpsEndpoints.HourRow` ↔ `AS hour`, `RatingRow` ↔ `copied_by`), тож аліаси в SQL пишуться у snake_case
   (`raw_message_id` ↔ `RawMessageId`), а не в лапках PascalCase; лапки лише для скалярів (`AS "Value"`).
   Так і зроблено.
2. **Пробний прогін на 20 тис. реальних повідомлень** показав хибні пари: коротке «Київщина: реактивний БпЛА
   курсом на Обухів» (41 символ) «вкладене» (C = 0.84) у 5-рядковий список raketa_trevoga через 5 годин — шаблон,
   не копія. Тому: `ContainmentMinLength` = 80 (вкладення рахується лише коли коротший текст ≥ 80 символів),
   `JaccardThreshold` 0.5 → 0.7 (при 0.6 «Ракета з акваторії Чорного моря у напрямку Одещини» ще збігалась з «… Миколаївщини»), `ContainmentThreshold` 0.8 → 0.85, `PairWindow` 12 → 6 год; рядки-підписи
   каналу («➡ Підписатися») відкидаються нормалізатором. Пороги лишаються конфігурацією, індекс перебудовується
   кнопкою «Скинути».
3. `INSERT … RETURNING` не можна загорнути в `SqlQuery<T>` (EF робить підзапит) — `PairsFound` рахує upsert-и
   (включно з повторними знахідками після редагувань), а не лише нові рядки. Задокументовано в DTO.
4. Роль `puluj_admin` для `reset` виконує `DELETE` (не `TRUNCATE`) — як і планувалося; на 400 тис. рядків це секунди.

## Статус

Імплементовано повністю (2026-09-15): проєкти `Puluj.Analytics`, `Puluj.Analytics.Worker`, тести `Puluj.Analytics.Tests`
(19 юніт + 1 інтеграційний через Testcontainers), ендпоїнти в `Puluj.Admin`, сторінка «Аналітика → Хто кого копіює»,
`deploy/Dockerfile.analytics`, сервіс `analytics` у compose, запуск у `scripts/dev-run.ps1`, документація в
`docs/README.md` («Аналітика джерел»). Індекс на dev-БД побудовано з порогами 0.7 / 0.85 / 80 / ±6 год.
