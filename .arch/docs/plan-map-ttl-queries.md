# План: обмежити вибірки карти часом життя позначки

## 1. Що відбувається зараз (факти з коду й БД, 2026-09-15)

Стартове завантаження карти (`web/src/App.tsx → loadSnapshot`) робить два запити: `GET /api/snapshot?activeOnly=false`
і `GET /api/targets?since=now−6h&limit=300`, далі підписується на `/hubs/map`. Виміри проти БД у `puluj-postgis-1`
(399 тис. raw_messages, 221 тис. targets, 53.5 тис. треків, 172 тис. ревізій, 4050 тривог):

| Запит | Час | Розмір | Що повертає |
|---|---|---|---|
| `/api/snapshot?activeOnly=false` | 640 мс | 253 КБ | 229 треків (227 Active, найстаріший `lastSeenAt` 27.05.2025) + 29 тривог (найстаріша відкрита з 04.04.2022) |
| `/api/snapshot?activeOnly=true` | 480 мс | 268 КБ | те саме без 2 Cancelled |
| `/api/targets?since=6h&limit=300` | 78 мс | 371 КБ | 224 повідомлення за 6 год — нормально |
| `/api/places/regions` | 100 мс | 2.4 МБ | полігони (кешується браузером 1 год) — поза цим планом |

Корені проблеми:

1. **`SnapshotService.LiveAsync` не обмежує Active-треки часом.** Умова
   `activeOnly ? Status == Active : LastSeenAt >= now−3h || Status == Active` віддає *всі* Active-треки, скільки б їм
   не було (390 у БД, частина — з травня 2025, які так і не закрились). (Тривоги `EndedAt == null` — ~30 рядків,
   серед них законно безперервні з 2022 року; це не джерело навантаження, див. §2.)
   Клієнт (`visibleTracks`) усе одно відкидає все старше `filters.lifetimeMinutes` (5…120 хв), тобто > 90 %
   снапшоту завантажується, серіалізується й парситься даремно.
2. **На кожен трек снапшоту — 2 окремі запити до БД** (`FixesAsync → ChainAsync`: `puluj_target_chain(...)` +
   вибірка targets), тобто ~460 round-trip на 229 треків. Це основна частина 640 мс.
3. **`NotifyBridge` пушить у SignalR *кожну* подію без огляду на її вік.** Саме зараз іде перебудова
   (219 тис. Pending, 150 тис. оброблено за останню годину ≈ 42 повідомлення/с): кожне історичне повідомлення 2022 року
   породжує `TargetCreated` + `TrackUpserted`, Api робить по 4 запити до БД на подію, а всі підключені клієнти
   отримують ~40–80 подій/с.
4. **Клієнт приймає все, що прийшло, і ніколи не чистить.** `upsertTrack` кладе трек у `tracks` без перевірки віку,
   `addTarget` — у стрічку; `tracks` росте без обмеження на весь час сесії; кожна подія — окремий `set()` у zustand,
   а ефект у `MapView`/`KyivMapView` перебудовує *всі* GeoJSON-шари при кожній зміні `tracks`/`alerts`. Під час
   перебудови це десятки повних перебудов на секунду — те саме «ковбасить».
5. Немає єдиного джерела правди для TTL: список `[5…120]` хв зашитий у `FilterPanel.tsx`, `RecentWindow = 3h` —
   у `SnapshotService`, 6 год стрічки — у `client.ts` і в ендпоїнті.

Індекси, що вже є і покривають нові умови: `ix_target_tracks_last_seen_at`, `ix_target_tracks_status_last_seen_at`,
`ix_air_alerts_place_id WHERE ended_at IS NULL` (відкритих тривог ~30, повний перегляд цього partial-індексу дешевий),
`ix_targets_observed_at` (BRIN), `ix_track_targets_target_id`. Нових індексів/міграцій не потрібно.

## 2. Єдине джерело правди для TTL — `MapOptions` в Api

Новий клас `src/Puluj.Api/Services/MapOptions.cs` (секція `Map` у `appsettings.json`, усі значення мають дефолти;
порожній або від'ємний список опцій / нульові вікна відкидаються на користь дефолтів у самих властивостях, щоб
помилка конфігурації не дала порожню карту):

| Параметр | Дефолт | Сенс |
|---|---|---|
| `LifetimeOptionsMinutes` | `[5,10,15,20,30,45,60,120]` | варіанти «Часу життя позначки» на панелі |
| `MaxLifetime` (обчислюване) | 120 хв | вікно живого снапшоту треків: `lastSeenAt ≥ now − MaxLifetime` |
| `FeedHours` | 6 | вікно стрічки повідомлень (`/api/targets` без `until`) |
| `SnapshotCacheSeconds` | 5 | час життя серверного кешу живого снапшоту |

Тривоги вікном за віком **не** обмежуються: відкрита тривога — це стан, а не подія (Луганська область і АР Крим
під тривогою безперервно з 2022 року, і це правильно); відкритих тривог ~30, partial-індекс `WHERE ended_at IS NULL`
робить вибірку копійчаною. Тривоги без відбою (Вовчанськ 2024, Чернігів/Київ 2025-07-08) — проблема даних колектора,
не вибірки.

Клієнт отримує ці значення з нового `GET /api/map/config` → `MapConfigDto(LifetimeOptionsMinutes, MaxLifetimeMinutes,
FeedHours)` (`src/Puluj.Contracts/Dtos.cs`). Інтервал пакетування подій хаба (300 мс) — константа клієнта, серверу
про неї знати нема чого. У сторі є такі самі дефолти на випадок, якщо
запит не вдався, — числа однакові, щоб поведінка до/після завантаження конфігу не відрізнялась. Збережене в
`localStorage` значення `lifetimeMinutes`, якого немає серед опцій, підтягується до найближчої опції.

Усі часи — `DateTimeOffset` в UTC (`TimeProvider.GetUtcNow()`); БД зберігає `timestamptz`; клієнт порівнює `Date.getTime()`.

## 3. Сервер

### 3.1 `SnapshotService.LiveAsync`
- Треки: `LastSeenAt >= now − MaxLifetime` для обох значень `activeOnly`; `activeOnly=true` додає `Status == Active`.
  Аргумент лишається для сумісності контракту. Зникає `RecentWindow` для живого режиму (3 год > 2 год: клієнт усе одно
  не показував нічого старшого за 120 хв).
- Тривоги: як і зараз, `EndedAt == null` (див. §2 — без вікна за віком).
- **Кеш**: один спільний результат на значення `activeOnly` з TTL `SnapshotCacheSeconds`; паралельні запити під час
  побудови чекають на одну й ту саму `Task` (`Lazy<Task<SnapshotDto>>`), тож N клієнтів після реконекту = 1 набір
  запитів до БД. Кеш — у `SnapshotService` (singleton), без нових пакетів. Спільна `Task` будується з
  `CancellationToken.None` (скасування одного HTTP-запиту не має валити відповідь іншим); той, хто чекає, приєднує свій
  токен через `WaitAsync(ct)`. Невдала побудова з кешу викидається одразу, щоб помилка не «зависла» на 5 с.
- **Ланцюжки одним запитом**: `FixesAsync` замість циклу `ChainAsync` на трек робить один SQL
  `SELECT h.track_id, c.step, c.target_id, c.probability FROM unnest({trackIds}, {headIds}) AS h(track_id, target_id)
  CROSS JOIN LATERAL puluj_target_chain(h.target_id, {MaxFixes−1}) c` і одну вибірку `targets` по всіх id.
  `ChainAsync` (один трек) лишається для `TrackAsync`/`TrackDetailsAsync`.
- `AtAsync` / `ReplayAsync` (історія) не змінюються — там вікно `RecentWindow` (3 год до `at`) і `MaxReplayWindow`
  (36 год) уже обмежують вибірку; `RecentWindow` перейменовується в `HistoryWindow`, щоб було видно, що це історія.

### 3.2 `RecentTargetsAsync` (`/api/targets`)
- Живий режим (`until == null`): `since = max(since, now − FeedHours)`.
- Історія (`until != null`): `since = max(since, until − MaxReplayWindow)`.
- `limit` уже обмежено 1…5000.

### 3.3 `NotifyBridge` — не пушити те, чого клієнт усе одно не покаже
Правило за типом події (перевірка — у тому самому запиті, що будує DTO, див. нижче):
- `TrackUpserted` / `TrackClosed`: `LastSeenAt < now − MaxLifetime` → пропустити.
- `TargetCreated`: `ObservedAt < now − FeedHours` → пропустити.
- `AlertChanged`: пропустити лише вже закриту тривогу з `EndedAt < now − MaxLifetime` (історичне завантаження тривог
  `AlertsInUaHistoryCollector`): відкриті тривоги будь-якого віку і свіжі відбої проходять.
Реалізація — параметр `notBefore` у `SnapshotService.TrackAsync/TargetAsync/AlertAsync`, вкладений у наявний запит
(`Where(x => x.Id == id && (notBefore == null || x.LastSeenAt >= notBefore))`): жодного додаткового round-trip, для
старого запису повертається `null`, і три супутні вибірки (джерела, сліди, повідомлення) не виконуються. Пропущені події рахуються, раз на хвилину —
один рядок `Information` «Skipped N stale push events (older than …)», щоб перебудова була видима в логах, але не
засмічувала їх. Контракт хаба (`IMapClient`) не змінюється.

### 3.4 Ендпоїнт `GET /api/map/config`
Повертає `MapConfigDto` з `IOptions<MapOptions>`, `Cache-Control: public, max-age=300`.

### 3.5 `Puluj.Admin`
Реєструє `SnapshotService` сам (`Program.cs:38`), без `AddPulujApi`. Новий параметр конструктора `IOptions<MapOptions>`
резолвиться там із дефолтами (хост реєструє `AddOptions()`), тож Admin не змінюється і не ламається.

## 4. Клієнт

### 4.1 Стор (`web/src/store/useStore.ts`) + чиста логіка в `web/src/store/liveWindow.ts`
- `mapConfig: MapConfig` (дефолти = серверним), `setMapConfig`.
- Чисті функції (тестуються vitest без DOM): `isTrackLive(t, now, maxLifetimeMinutes)`,
  `isTargetFresh(o, now, feedHours)`, `pruneTracks(tracks, now, cfg)`, `mergeTracks(tracks, incoming, now, cfg)`,
  `mergeTargets(list, incoming, now, cfg, cap=500)`. Тривоги не чистяться за віком (§2); `upsertAlert` як і зараз
  видаляє тривогу з `endedAt`.
- `upsertTracks(list)`, `upsertAlerts(list)`, `addTargets(list)` — пакетні версії (одне `set()` на пакет); одиничні
  `upsertTrack/upsertAlert/addTarget` лишаються як обгортки. Прострочене ігнорується на вході.
- `tick()` (кожні 15 с) окрім `now` викидає з `tracks` треки старші за `MaxLifetime` (тільки в live-режимі; у
  history стор заморожений на `at`). Це замінює «ніколи не чистимо».
- `setSnapshot` без змін (сервер уже віддає обмежене вікно).

### 4.2 Пакетування подій хаба (`web/src/App.tsx`)
Обробники `connectMapHub` не пишуть у стор напряму, а складають події у буфер (`Map<id, TrackDto>`, `Map<id, AlertDto>`,
`TargetDto[]`); `setTimeout(flush, EVENT_BATCH_MS = 300)` зливає буфер трьома пакетними викликами. Один рендер на
300 мс замість одного на подію. Буфер скидається при `stop()` з'єднання.

### 4.3 Панель і клієнт API
- `FilterPanel`: опції «Часу життя позначки» — з `mapConfig.lifetimeOptionsMinutes`.
- `api.mapConfig()`; `api.targets()` бере `mapConfig.feedHours` (передається аргументом із `loadSnapshot`).
- `mapConfig` завантажується один раз при старті разом із regions/sources; `loadSnapshot` бере `feedHours` зі стору
  в момент виклику (до відповіді конфігу діють дефолти, які збігаються з серверними); невдача — дефолти.

### 4.4 Історія / replay
Не змінюються: `AtAsync`, `/api/replay`, `ReplayBar`, `targetsBetween`. У `history` події хаба вже ігноруються
(`upsertTrack` перевіряє `mode`), очищення в `tick()` теж лише в live.

## 5. Критерії успіху (вимірювані)
- `/api/snapshot?activeOnly=false` на поточній БД: треків ≤ кількості з `lastSeenAt` за 2 год (зараз 17–20 замість 229),
  розмір ≈ 20–30 КБ замість 253 КБ, час < 100 мс (без кешу) і ~1 мс з кешу.
- Відкриті тривоги у відповіді всі (~30), як і раніше, — вони не є джерелом навантаження.
- Під час перебудови (Pending > 0, історичні повідомлення) клієнт **не** отримує подій хаба — у логах Api рядок
  «Skipped N stale push events» (у браузері — відсутність WS-фреймів у DevTools, якщо є доступ до нього).
- Свіжі повідомлення (ingest із панелі) доходять на карту, як і раніше (~0.6 с).
- Консоль браузера без помилок; карта відкривається без «замерзання».

## 6. Тести
- `tests/Puluj.Integration.Tests/MapSnapshotTests.cs` (колекція `pipeline`, працює через Testcontainers/`PULUJ_TEST_CONNECTION`,
  без БД — no-op як інші): вставляє треки — свіжий Active (−5 хв), старий Active (−3 дні), свіжий Closed (−30 хв),
  старий Closed (−3 дні); тривоги — відкрита свіжа, відкрита стара (−3 дні, має лишитись), закрита. `SnapshotService`
  збирається вручну (`ReferenceCache.RefreshAsync` + `DtoMapper` + `Options.Create(new MapOptions())`). Перевіряє:
  live-снапшот містить тільки свіжі треки (обидва статуси при `activeOnly=false`, тільки Active при `true`); обидві
  відкриті тривоги присутні, закрита — ні; кеш віддає той самий екземпляр протягом TTL; `TrackAsync(id, notBefore)`
  повертає null для старого треку; `AlertAsync(id, notBefore)` повертає стару відкриту тривогу і не повертає давно
  закриту; `RecentTargetsAsync` не повертає нічого старшого за `FeedHours`. Екземпляри `SnapshotService` для
  перевірок вибірок створюються з `SnapshotCacheSeconds = 0` (інакше другий виклик після нової вставки віддасть
  кешований результат); тест кешу — окремий екземпляр з TTL 5 с і `ReferenceEquals` двох відповідей. Свої рядки видаляє у `finally` (PipelineTests
  розраховує на `TargetTracks.SingleAsync()`).
- `web/src/store/liveWindow.test.ts` (vitest): прострочений трек не потрапляє в `mergeTracks`, `pruneTracks` викидає
  старі й лишає свіжі, `mergeTargets` тримає cap, не дублює id і зберігає порядок «новіші перші».

## 7. Файли
Сервер: `src/Puluj.Api/Services/MapOptions.cs` (новий), `SnapshotService.cs`, `NotifyBridge.cs`,
`ApiDependencyInjection.cs` (реєстрація опцій — один рядок), `Endpoints/EndpointRouteBuilderExtensions.cs`
(`/api/map/config`, clamp у `/api/targets`), `appsettings.json` (секція `Map`), `src/Puluj.Contracts/Dtos.cs` (`MapConfigDto`).
Клієнт: `web/src/api/types.ts`, `api/client.ts`, `store/useStore.ts`, `store/liveWindow.ts` (новий), `App.tsx`,
`components/FilterPanel.tsx`.
Тести: `tests/Puluj.Integration.Tests/MapSnapshotTests.cs`, `web/src/store/liveWindow.test.ts`.
Документація: `docs/README.md` — розділ «Карта і ETA» (вікна, конфіг `Map`) і абзац про realtime («події не
буферизуються» → «пакетуються по 300 мс; старші за вікно не пушаться»).
Не чіпаю: Processing/Worker/Domain/міграції, `Puluj.Admin` (посилається на `SnapshotService` через DI — сигнатура
конструктора змінюється сумісно), `StatsEndpoints`, `Puluj.Analytics*`.

## 8. Follow-up (поза планом, потребує міграції)
- Partial-індекс `target_tracks (last_seen_at) WHERE status = 0` не потрібен: `ix_target_tracks_last_seen_at` покриває.
- Треки, що «ніколи не закрились» (390 Active, частина з 2025), — робота `TrackWatchdog` (зона агента (a)); цей план
  лише ховає їх з живої карти. Тривоги без відбою (Вовчанськ 2024, Чернігів/Київ 2025-07-08) — звірка колектора
  alerts.in.ua з актуальним списком активних тривог; окрема задача.

## Ревʼю 1
1. **Помилка**: фільтр тривог за віком (`MaxAlertHours = 36`) сховав би Луганську область і АР Крим, які під тривогою
   безперервно з 2022 року цілком законно. Вік тривоги не є ознакою застарілості. Прибрано з снапшоту, клієнта й
   `NotifyBridge` (там лишився пропуск лише давно закритих тривог з історичного завантаження).
2. **Помилка**: спільна кешована `Task` снапшоту не може залежати від `CancellationToken` першого запиту — інакше
   закрита вкладка одного користувача скасує відповідь усім. Будувати з `CancellationToken.None`, чекати через `WaitAsync(ct)`.
3. **Зауваження**: перевірка `notBefore` окремим запитом додає round-trip на кожну свіжу подію; вкласти у наявний `Where`.
4. **Зауваження**: `EventBatchMs` у серверній конфігурації — зайва звʼязаність; константа клієнта.
5. **Перевірено**: `puluj_target_chain(p_target_id bigint, p_steps integer)` — сигнатура підходить для `unnest … CROSS JOIN LATERAL`;
   `Puluj.Admin` реєструє `SnapshotService` окремо, `IOptions<MapOptions>` там резолвиться з дефолтами — Admin не чіпаємо.
Правки внесено вище.

## Ревʼю 2
1. **Неузгодженість**: §3.3 починався з «дешевий запит одного стовпця», хоча п. 3 ревʼю 1 вклав перевірку в основний
   запит. Виправлено формулювання.
2. **Неузгодженість**: §1 називав відкриті тривоги 2022 року проблемою, §2 — нормою. §1 виправлено.
3. **Помилка в тестах**: кеш снапшоту з TTL 5 с у `SnapshotService` зробив би другий виклик у тесті сліпим до щойно
   вставлених рядків. Тести вибірок — з `SnapshotCacheSeconds = 0`, тест кешу — окремий екземпляр.
4. **Зауваження**: неправильна конфігурація `Map` (порожній список опцій, 0 годин) не має давати порожню карту —
   властивості `MapOptions` повертають дефолт для недопустимих значень.
5. **Зауваження**: у `docs/README.md` є фраза «події не буферизуються» — після пакетування вона стане неправдою; оновити.
6. Перевірено ще раз: `TrackClosed` для видимого треку проходить (його `LastSeenAt` свіжий), тож клієнт бачить
   закриття; клієнтське `tick()` міняє `tracks` лише коли щось справді викинуто, зайвих перебудов шарів не додається;
   `EndpointRouteBuilderExtensions.cs` тим часом змінив агент (c) (`MapStatsEndpoints`) — перед правкою перечитати.
Правки внесено вище.

## Ревʼю 3
Помилок і зауважень по суті немає. Уточнено лише формулювання 4.3 (конфіг вантажиться один раз при старті, не в
`loadSnapshot`) і критерій перевірки подій хаба (через логи Api, DevTools — за можливості). Переходжу до імплементації.
