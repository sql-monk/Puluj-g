# План: публічна сторінка «Статистика» (графіки по цілях, тривогах, джерелах)

Стан на 2026-09-15. Мета — третя вкладка публічного SPA (`web/`, віддається `Puluj.Api`, :5257) із графіками по
всіх наявних даних основної схеми: що і скільки летіло, звідки й куди, коли (година/день тижня), по яких областях,
тривоги по областях і тривалості, активність і якість джерел. Фільтр періоду: 24 год / 7 днів / 30 днів / довільний.

Не дублюємо тему «хто кого копіює» (рейтинг джерел, `source_rating_daily`, `SourceRatingPanel` в адмінці та окремий
сервіс аналітики, який будує інший агент). Не чіпаємо `Processing/**`, `Worker/**`, `RawMessage`, міграції
`PulujDbContext`, `deploy/docker-compose.yml`.

## 1. Що є в даних (перевірено в БД 2026-09-15)

| Таблиця | Рядків | Діапазон | Примітки |
|---|---|---|---|
| `raw_messages` | 398 835 | 2022-02 … 2026-09 | 9 джерел; **234 761 Pending** — БД посеред перебудови, `targets` заповнені лише до 2025-04 і за вересень 2026 (~180) |
| `targets` | 204 577 | 2022-02 … 2026-09 | `event_type`: 1 TargetObserved 151k, 10 AirRaidAlert 20k, 11 AlertCancelled 17k, 21 AirDefense 7.7k, 20 Explosion 6.3k, 12 TargetCancelled 1.7k |
| | | | категорії: UAV 73k, MISSILE 51k, AIRCRAFT 32k, GUIDED_BOMB 4.5k, UNKNOWN 1.1k, без категорії 43k (події тривог/вибухів) |
| | | | класи: STRIKE_UAV 39k, TACTICAL_AIRCRAFT 14k, BALLISTIC 13k, CRUISE 12k, STRATEGIC_AIRCRAFT 7.5k, GLIDE_BOMB 4.5k, RECON_UAV 2.8k, AEROBALLISTIC 1.4k, HELICOPTER 594, HYPERSONIC 170, DECOY_UAV 141 |
| | | | `location_kind`: Unknown 74k, Region 53k, City 45k, DirectionOnly 23k, District 6.7k, Area 3.2k; `location_place_id` є у 112k |
| | | | `origin_place_id` 15.6k, `destination_place_id` 30.4k, `duplicate_of_target_id` 12.5k, `object_count` 34k, напрямок 40k |
| | | | `identification_method`: Rule 204k, Structured 45 (LLM 0 — баланс API вичерпано) |
| `air_alerts` | 3 832 | 2022-03 … 2026-09 | рівень місця: Region 1 744, Village 1 669, City 242, Hromada 70, District 54 …; `alert_type` майже завжди AirRaid; `level` майже завжди Unknown |
| `target_tracks` | 49 136 | | `first_seen_at`, `target_category_id` — «окремі об'єкти» |

Висновки для дизайну:
- «Скільки чого запущено» чесно рахуємо **треками** (окремі об'єкти) по категоріях/класах; `object_count` (сума
  заявлених у повідомленнях кількостей) показуємо як окрему цифру з підписом «за повідомленнями», без дублікатів.
- «Звідки → куди» — по парах `origin_place_id`/`destination_place_id`, зведених до області (`ReferenceCache.RegionOf`).
- Тривоги «по областях» — лише записи, чиє місце має рівень Region або City без батька (Київ): підсумовувати години
  по громадах однієї області не можна (накладання).
- У пресетах 24 год/7 днів зараз цілей мало (перебудова) — сторінка мусить чесно показувати «немає даних» і мати
  довільний період, яким можна відкрити, наприклад, березень 2025.

## 2. Бекенд: один агрегуючий ендпоїнт

`GET /api/stats?from=<iso>&to=<iso>` (обидва необов'язкові; без них — останні 24 год). Один запит — усі секції,
однакові для всіх користувачів, без per-user логіки (пам'ять: per-user рахунок лише на клієнті).

Обмеження: `to ≤ now` (сервер обрізає `to` до `now`, округленого вниз до хвилини), `to − from ≤ 366 днів`,
мінімум 1 година. Крок гістограм (bucket) обирає сервер: `≤ 3 дні → hour`, `≤ 120 днів → day`, інакше `week`
(понеділок). Часова зона бакетів і «години доби» — `Europe/Kyiv` (як у `SnapshotService.SourceRatingAsync`);
у SQL — триаргументний `date_trunc(unit, ts, 'Europe/Kyiv')` (PG ≥ 14, перевірено на PG17). Список початків
бакетів будує `StatsAggregator` тими ж правилами (локальна арифметика дат через `TimeZoneInfo`, тому доба переходу
на літній час має 23/25 год так само, як у `date_trunc`); рядок SQL, чий ключ не збігся з жодним початком (край DST),
падає в найближчий попередній бакет, а не губиться.

Кеш: `IMemoryCache` (`services.AddMemoryCache()`, `SizeLimit = 64`, кожен запис `Size = 1`). Ключ — `from|to`,
де для пресетів клієнт округлює `to` до 5 хв (тому 24h/7d/30d від різних користувачів потрапляють в один запис).
TTL: 2 хв (`AbsoluteExpirationRelativeToNow`) — Api читає роллю `puluj_reader`, дані лише читаються.
Одночасні промахи кешу з одним ключем — `Lazy<Task<StatsDto>>`/`GetOrCreateAsync` достатньо (кілька паралельних
обчислень не фатальні).

Усі агрегати — SQL `GROUP BY` через `db.Database.SqlQuery<Row>($"...")` з параметрами (FormattableString) —
на клієнт .NET приходять лише згруповані рядки (сотні), не 200k цілей. Дрібні довідники (назви класів/категорій,
область місця, рівень місця) підтягуються з `ReferenceCache` у пам'яті. Правила для рядків `SqlQuery` (як у
`SnapshotService`): записи `private sealed record XRow(...)`, колонки в SQL іменуються snake_case від властивостей
(`bucket_at`, `category_id`, `n`); `count(*)` → `long`; `timestamptz` → `DateTimeOffset`; `extract` → `CAST(... AS int)`;
`sum(object_count)` → `long?`; `percentile_cont` → `double?`.

### 2.1 Запити (усі з `from`, `to`; фільтр цілей = `event_type = 1 AND duplicate_of_target_id IS NULL` — «факти» без повторів)

| # | Дані | SQL (суть) | Секція DTO |
|---|---|---|---|
| Q1 | цілі за бакет × категорія | `date_trunc({unit}, observed_at, 'Europe/Kyiv')`, `target_category_id`, `count(*)` | `timeline[].targets[]` (масив у порядку `categories`) |
| Q2 | треки відкриті за бакет × категорія | `target_tracks.first_seen_at`, `target_category_id` | `timeline[].tracks[]` |
| Q3 | цілі за класом | `target_class_id`, `count(*)`, `sum(object_count)` | `byClass[]` (code, name, category, targets, objectsDeclared) |
| Q4 | треки за категорією/класом | `target_tracks` за `first_seen_at` | `byClass[].tracks`, KPI `tracks` |
| Q5 | цілі за місцем локації | `location_place_id`, `count(*)` (NULL — окремо) → зводимо до області в пам'яті | `byRegion[]` (regionId, name, targets) — топ 20 + «інші» |
| Q6 | маршрути | `origin_place_id`, `destination_place_id`, `count(*)` (обидва не NULL) → області в пам'яті, пари підсумовуються | `routes[]` (fromId, fromName, toId, toName, count) — **усі** пари на рівні областей (≤ ~400), за спаданням; матрицю топ-8 × топ-8 (за сумами рядків/стовпців) і топ-10 будує клієнт |
| Q7 | година × день тижня | `CAST(extract(dow FROM observed_at AT TIME ZONE 'Europe/Kyiv') AS int)`, так само `hour` (PG14+ `extract` повертає numeric — обов'язково CAST) | `hourWeekday[7][24]`, індекс 0 = понеділок (dow 0 = неділя переставляє сервер) |
| Q8 | зрізи-розподіли | по `event_type` (усі події, не лише факти), `identification_method`, `confidence`, `location_kind` (факти) | `eventTypes[]`, `methods[]`, `confidence[]`, `locationKinds[]` |
| Q9 | інтервали тривог | **один** запит: `place_id, started_at, ended_at` з `air_alerts` де `started_at < {to} AND (ended_at IS NULL OR ended_at > {from})` (тисячі рядків, не агрегати) → усе далі рахує `StatsAggregator` в пам'яті: фільтр рівня Region / Київ (City без батька) через `ReferenceCache`, обрізання інтервалів межами періоду, **по областях** (к-сть, години), **гістограма тривалості** завершених (`<30 хв / 30–60 / 1–2 год / 2–4 / 4–8 / >8`), **за бакетами** — к-сть оголошених у бакеті та години під тривогою з розрізанням інтервалу по межах бакетів | `alertsByRegion[]`, `alertDurations[]`, `timeline[].alerts`, `timeline[].alertHours` |
| Q12 | джерела | `raw_messages` за `published_at`: `count(*)`, `count(*) FILTER (processing_status=1)`, `count(*) FILTER (EXISTS (SELECT 1 FROM targets t WHERE t.raw_message_id = r.raw_message_id))` (є `ix_targets_raw_message_id`, план перевірено: ~280 мс на 10k повідомлень), `percentile_cont(0.5)` затримки `received_at − published_at` з фільтром `< 6 год` (історичний бекфіл має затримку у роки — виключаємо). Частка «з фактами» на клієнті = `withTargets / processed` (Pending не рахуємо в знаменник) | `sources[]` (id, code, name, messages, processed, withTargets, medianLagSeconds) |
| Q13 | джерела за бакет | `source_id`, bucket, `count(*)` | `sources[].series[]` (спарклайн) |
| Q14 | факти за джерелом | цілі (факти) `source_id`, `count(*)` | `sources[].targets` |
| Q15 | KPI | сума з Q1/Q2/Q9/Q12; `objectsDeclared` = `sum(object_count)` фактів | `totals` |

Індекси, що є (перевірено `pg_indexes`): `ix_targets_observed_at` і `ix_raw_messages_published_at` — **BRIN**
(lossy bitmap; місяць цілей — ~66 мс, місяць повідомлень з EXISTS — ~280 мс), `ix_targets_target_category_id_observed_at`,
`ix_targets_raw_message_id`, `ix_air_alerts_started_at` (BRIN). За 366 днів увесь набір запитів — одиниці секунд у
найгіршому разі, з кешем 2 хв на однакові ключі цього досить. Матеріалізовані view не потрібні (і міграції зараз
заборонені). Серед фактів (`event_type = 1`, не дублікат) цілей без категорії немає (перевірено), але агрегатор
все одно зводить `NULL` до `UNKNOWN`.

### 2.2 DTO (`src/Puluj.Contracts/Dtos.cs`, додати в кінець)

```csharp
public sealed record StatsTotalsDto(int Targets, int Tracks, int ObjectsDeclared, int Alerts, double AlertHours, int Messages, int MessagesWithTargets, int ActiveSources);
public sealed record StatsCategoryDto(string Code, string Name);
public sealed record StatsBucketDto(DateTimeOffset At, IReadOnlyList<int> Targets, IReadOnlyList<int> Tracks, int Alerts, double AlertHours); // Targets/Tracks — у порядку Categories
public sealed record StatsClassDto(string Code, string Name, string CategoryCode, int Targets, int Tracks, int ObjectsDeclared);
public sealed record StatsRegionDto(int? Id, string Name, int Targets);
public sealed record StatsRouteDto(int FromId, string FromName, int ToId, string ToName, int Count);
public sealed record StatsSliceDto(string Key, string Label, int Count);
public sealed record StatsAlertRegionDto(int Id, string Name, int Count, double Hours);
public sealed record StatsSourceDto(int Id, string Code, string Name, int Messages, int Processed, int WithTargets, int Targets, double? MedianLagSeconds, IReadOnlyList<int> Series);
public sealed record StatsDto(DateTimeOffset From, DateTimeOffset To, string Bucket, IReadOnlyList<DateTimeOffset> BucketStarts,
    StatsTotalsDto Totals, IReadOnlyList<StatsCategoryDto> Categories, IReadOnlyList<StatsBucketDto> Timeline,
    IReadOnlyList<StatsClassDto> ByClass, IReadOnlyList<StatsRegionDto> ByRegion, IReadOnlyList<StatsRouteDto> Routes,
    IReadOnlyList<IReadOnlyList<int>> HourWeekday, IReadOnlyList<StatsSliceDto> EventTypes, IReadOnlyList<StatsSliceDto> Methods,
    IReadOnlyList<StatsSliceDto> Confidence, IReadOnlyList<StatsSliceDto> LocationKinds,
    IReadOnlyList<StatsAlertRegionDto> AlertsByRegion, IReadOnlyList<StatsSliceDto> AlertDurations, IReadOnlyList<StatsSourceDto> Sources);
```

`BucketStarts` — повний список початків бакетів (без дірок), щоб клієнт не добудовував вісь; `Timeline` — по одному
рядку на бакет у тому ж порядку (нулі заповнює сервер). Джерела `Series` — вирівняні з `BucketStarts`. `Totals.ActiveSources` —
джерела з ≥ 1 повідомленням за період. Масиви замість словників: менше байтів і простіший клієнт (`timeline[i].targets[j]`
для `categories[j]`).

### 2.3 Файли бекенду

- `src/Puluj.Api/Services/StatsService.cs` — запити + збирання DTO + кеш (sealed, primary constructor, як `SnapshotService`).
- `src/Puluj.Api/Services/StatsAggregator.cs` — **чиста** частина без БД: бакетування (список початків бакетів між
  from/to у Kyiv), зведення місць до областей, top-N + «інші», згортання пар місць у пари областей, тривоги (обрізання,
  гістограма тривалості, розрізання по бакетах). Порядок `Categories` фіксований константою на сервері
  (UAV, MISSILE, GUIDED_BOMB, AIRCRAFT, UNKNOWN) — той самий, що й у палітрі клієнта. Саме її тестуємо.
- `src/Puluj.Api/Endpoints/StatsEndpoints.cs` — `MapStatsEndpoints(this IEndpointRouteBuilder api)`; у
  `EndpointRouteBuilderExtensions.MapPulujEndpoints` **один рядок** `api.MapStatsEndpoints();` (перечитати файл перед правкою).
- `src/Puluj.Api/ApiDependencyInjection.cs` — два рядки: `services.AddMemoryCache(o => o.SizeLimit = 64);`,
  `services.AddSingleton<StatsService>();`.
- Валідація параметрів → `400 ProblemDetails` (`Results.ValidationProblem`).

## 3. Фронтенд

### 3.1 Бібліотека графіків — ні, SVG вручну

У `package.json` графічних бібліотек немає; `SourceRatingPanel` уже малює лінійний графік чистим SVG. Потрібні форми —
стовпчики (стекові), горизонтальні смуги, теплова карта, спарклайн, плитки — це десятки рядків SVG кожна. Переваги:
нуль залежностей і розміру бандла, кольори з CSS-змінних тем (`currentColor`, `--color-slate-*` вже перевизначені
в `index.css` на тему), повний контроль над тултипом і dark/light. Sankey/chord для «звідки → куди» **не робимо**:
при 25 областях × 25 він нечитабельний; замість нього матриця-теплова карта топ-8 × топ-8 + таблиця топ-маршрутів.
Хороплет по областях теж не робимо — це дублює карту; горизонтальні смуги по областях читаються точніше.

### 3.2 Графіки (за `dataviz`: форма → колір → валідація → марки → ховер → таблиця)

Фільтр періоду — один рядок над усім: пресети «24 год · 7 днів · 30 днів · Довільний» (+ два `datetime-local` при
довільному). Усі секції рендеряться з одного `StatsDto`. При перезавантаженні попередній рендер лишається з
`opacity-60` (без скелетонів). Помилка — червоний рядок як у `TopBar`. Порожній період — підпис «за період даних немає».

| # | Блок | Форма | Колір | Навіщо |
|---|---|---|---|---|
| 1 | KPI-рядок | 6 плиток: факти, окремі об'єкти (треки), заявлено об'єктів, тривоги (к-сть · годин), повідомлень (частка з розпізнаними фактами), активних джерел | текст | заголовок дашборда |
| 2 | Динаміка «що летіло» | стекові стовпчики по бакетах, серії = категорії (UAV, MISSILE, GUIDED_BOMB, AIRCRAFT, UNKNOWN) ≤ 5; перемикач «факти / об'єкти (треки)» | категоріальна, фіксований порядок | головний графік; тренд + склад |
| 3 | Тривоги в часі | стовпчики: тривог оголошено за бакет; підпис — сумарні години | 1 відтінок (sequential 450) | окремим графіком, не другою віссю на №2 |
| 4 | За класом | горизонтальні смуги, відсортовано; значення на кінці; трек-лічильник у тултипі й таблиці | 1 відтінок; смужка категорії ліворуч як ключ | «скільки чого»: Shahed vs ракети vs КАБи |
| 5 | По областях | горизонтальні смуги топ-15 + «інші» | 1 відтінок | де фіксують |
| 6 | Звідки → куди | теплова матриця топ-8 origin × топ-8 destination + список топ-10 маршрутів | sequential blue | «хто куди летить» |
| 7 | Година × день тижня | теплова карта 7 × 24 (Kyiv) | sequential blue | коли летять |
| 8 | Зрізи | 4 малі горизонтальні смуги: типи подій, метод ідентифікації, впевненість, вид локації | 1 відтінок кожен | розподіл усіх наявних полів |
| 9 | Тривоги по областях | горизонтальні смуги: годин під тривогою (топ-15), к-сть у підписі | 1 відтінок | де довше сидять у тривозі |
| 10 | Тривалість тривог | стовпчики по 6 бакетах | 1 відтінок (ординальний) | типова тривалість |
| 11 | Джерела | таблиця: джерело · повідомлень · спарклайн · оброблено % · з фактами % · фактів · медіана затримки | спарклайн у de-emphasis сірому | активність, обсяг, «корисність», затримка |

Кожен графік: заголовок + підзаголовок з одиницями; тултип на марці (`pointermove`/`focus`, `textContent`);
кнопка «таблиця» перемикає SVG на `<table>` (WCAG-двійник); `role="img"` + `aria-label`; висота контейнера включає
вісь X; смуги ≤ 24 px, 2 px проміжок між стеками (stroke кольору поверхні), заокруглений кінець 4 px, сітка —
hairline `currentColor` з `opacity .12`, текст — токени тексту (ніколи колір серії).

Палітра категорій (перевірено `scripts/validate_palette.js`, усі перевірки PASS):
- світлі теми (light, sepia; поверхня `#ffffff`): UAV `#15803d`, MISSILE `#2563eb`, GUIDED_BOMB `#ea580c`, AIRCRAFT `#7c3aed`, UNKNOWN `#b45309`;
- темні (dark, midnight, olive, graphite; поверхня `#0f172a`): `#16a34a`, `#3b82f6`, `#ea580c`, `#9085e9`, `#d97706`.
Зелений для БпЛА і синій для ракет повторюють читання карти (`map/palette.ts`: uav зелений, cruise синій). Sequential —
синій рамп 100…700 з `palette.md`, у темних темах — той самий рамп зі світлим кінцем як «максимум».
Категорії йдуть у стеку **завжди в цьому порядку** й кольори прив'язані до коду, не до рангу.

### 3.3 Файли фронтенду

- `web/src/stats/StatsPage.tsx` — сторінка-оверлей (`absolute inset-0 top-12 z-10 overflow-y-auto`), фільтр, сітка секцій.
- `web/src/stats/useStats.ts` — стан періоду (пресет ↔ from/to, `to` округлене до 5 хв), fetch із скасуванням, «попередній рендер при перезавантаженні». Період живе в хеші: `#/stats` (24 год), `#/stats?p=7d|30d`, `#/stats?from=…&to=…` — посилання на довільний період можна передати; довільний період застосовується кнопкою «Показати», не на кожен ввід.
- `web/src/stats/period.ts` — чисті функції періоду/бакетів/форматування підписів (vitest).
- `web/src/stats/palette.ts` — кольори категорій light/dark, sequential рамп, `seqColor(v, max, dark)`.
- `web/src/stats/charts/{StackedColumns,Bars,Heatmap,Sparkline,StatTile,ChartCard,Tooltip}.tsx`.
- `web/src/stats/SourcesTable.tsx`.
- `web/src/api/client.ts` — один рядок `stats: (from, to) => get<StatsDto>(...)`; `web/src/api/types.ts` — типи `Stats*`.
- `web/src/components/TopBar.tsx` — `Page` отримує `'stats'`, у перемикачі третя кнопка «Статистика».
- `web/src/App.tsx` — хеш `#/stats…` (розбір `startsWith('#/stats')`, бо є query); на цій сторінці карта лишається
  змонтованою під оверлеєм (стан карти й SignalR не втрачаються), панелі фільтрів/стрічки/деталей, кнопка «☰ Фільтри»
  і тост «Трек більше не відображається» не рендеряться; `goPage('stats')` → `#/stats`.

## 4. Тести

- `tests/Puluj.Api.Tests/StatsAggregatorTests.cs` (новий xunit-проєкт як `Puluj.Processing.Tests` + `<FrameworkReference
  Include="Microsoft.AspNetCore.App" />`, бо посилається на Web-проєкт; `dotnet sln add`): бакети між from/to у Kyiv
  (hour/day/week, доба переходу на літній час = 23/25 год), вибір кроку за довжиною періоду, ключ поза списком → попередній
  бакет, top-N + «інші», зведення місць до областей (фейковий `RegionOf`), згортання маршрутів у пари областей, гістограма тривалості,
  обрізання тривог межами періоду і розрізання по бакетах, фільтр рівня Region/Київ.
- `web/src/stats/period.test.ts` (vitest): пресети → from/to, округлення `to`, підписи бакетів, `seqColor` монотонний.
- Інтеграційний smoke `/api/stats` без окремого хоста не робимо (немає тест-проєкту з `WebApplicationFactory`; ручна
  перевірка в браузері + `curl`).

## 5. Документація

`docs/README.md`: рядок у таблиці API (`GET /api/stats?from&to`) і абзац у «Карта і ETA» → «Статистика».

## 6. Список файлів

Нові: `src/Puluj.Api/Services/StatsService.cs`, `StatsAggregator.cs`, `src/Puluj.Api/Endpoints/StatsEndpoints.cs`,
`tests/Puluj.Api.Tests/*`, `web/src/stats/**`.
Правки (по одному-двох рядках, перечитати перед правкою): `src/Puluj.Contracts/Dtos.cs`,
`src/Puluj.Api/ApiDependencyInjection.cs`, `src/Puluj.Api/Endpoints/EndpointRouteBuilderExtensions.cs`, `Puluj.sln`,
`web/src/api/client.ts`, `web/src/api/types.ts`, `web/src/components/TopBar.tsx`, `web/src/App.tsx`, `docs/README.md`.

## Ревʼю 1 (проти БД і коду)

1. `date_trunc(unit, ts AT TIME ZONE …) AT TIME ZONE …` — зайве: PG17 має триаргументний `date_trunc(unit, timestamptz, tz)` (перевірено). **Виправлено.**
2. Початки бакетів на сервері та ключі з SQL можуть розійтися на межі DST — додано правило «непізнаний ключ → попередній бакет» і тест. **Виправлено.**
3. Q9–Q11 (тривоги) як три SQL-агрегати важко тестувати й неможливо коректно порізати години по бакетах у SQL без generate_series — замінено на один вибір інтервалів + агрегація в пам'яті (`StatsAggregator`, тестується). **Виправлено.**
4. Тест-проєкт, що посилається на Web SDK проєкт, не збереться без `FrameworkReference Microsoft.AspNetCore.App`. **Виправлено.**
5. Словники в `StatsBucketDto` → масиви в порядку `Categories`. **Виправлено.**
6. Крок бакетів: 7 днів по годинах — 168 стовпчиків, забагато; 48 год межа лишала 3-денний довільний період з 3 стовпчиками — межі `≤ 3 дні → hour`, `≤ 120 днів → day`. **Виправлено.**
7. Частка повідомлень з фактами мусить ділитись на оброблені, не на всі (234k Pending зараз). **Виправлено.**
8. Індекси на `observed_at`/`published_at` — BRIN, не btree: плани перевірено, час прийнятний; у план внесено фактичні цифри. **Виправлено.**
9. Палітру перевірено також на поверхнях sepia (`#fffaf0`) і graphite (`#30363f`) — PASS. Без змін.
10. Серед фактів немає цілей без категорії — «OTHER» не потрібен, лишається захисне зведення до `UNKNOWN`. **Виправлено.**

## Ревʼю 2

1. Q8 згадував `direction_kind`, якого немає ні в DTO, ні в графіках — прибрано. **Виправлено.**
2. `routeMatrix` у таблиці запитів, але не в DTO; топ-30 маршрутів не дає побудувати матрицю — сервер віддає всі пари на рівні областей, матрицю й топ будує клієнт. **Виправлено.**
3. `extract()` у PG14+ повертає `numeric` — без `CAST` `SqlQuery<Row>` з `int` впаде; `count(*)` — `bigint`. Додано правила типів і snake_case-колонок для `SqlQuery`. **Виправлено.**
4. Орієнтація `hourWeekday` не була задана (dow 0 = неділя в PG) — зафіксовано «індекс 0 = понеділок». **Виправлено.**
5. Період не був deep-linkable, а довільний період перезавантажував би дані на кожен ввід — хеш із параметрами й кнопка «Показати». **Виправлено.**
6. Роутер в `App.tsx` порівнює хеш на рівність — для `#/stats?…` потрібен `startsWith`; також треба сховати кнопку «☰ Фільтри» і тост. **Виправлено.**

## Ревʼю 3

1. У 2.3 і в тестах лишалась «матриця маршрутів» на сервері після рішення ревʼю 2 — узгоджено. **Виправлено (косметика).**
2. Порядок `Categories` не був закріплений за сервером — закріплено константою. **Виправлено (косметика).**

Суттєвих зауважень немає.

## Ревʼю 4: зауважень немає
