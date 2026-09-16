# P11 — read-side/API, incident DTO, SignalR bridge/backplane — план

Task: [P11 / issue #11](https://github.com/sql-monk/Puluj-g/issues/11). Залежності: P09 (track/alert writers, NOTIFY з writers), P10 (incidents, `incident.changed`) — done.
Base commit: `78b3797`. Gate: API/SignalR contract tests; reconnect/out-of-order; broadcast на кілька API instances; query budget.

## Стан коду на старті (звірено)

- Api: `SnapshotDto(At, Historical, Tracks, Alerts, Events)`, `MapHub : Hub<IMapClient>` (`TrackUpserted/TrackClosed/AlertChanged/TargetCreated`), `NotifyBridge`
  (PG NOTIFY `puluj_events` → SignalR `Clients.All`, вікна `MapOptions`), `GET /api/event-kinds` (P07, `EventKindDto`), `SnapshotService`, `ReferenceCache`
  (kinds/sources/places). `IncidentDto` немає; `/api/incidents` немає; snapshot без incidents.
- Realtime: NOTIFY емітують writers (P09/P10) після commit — `TargetCreated`/`TrackUpserted`/`TrackClosed`/`AlertChanged`; для incidents NOTIFY немає.
  `PgNotifyListener` — один listener на процес; кожна API-репліка тримає власне з'єднання LISTEN → NOTIFY вже є fan-out на всі репліки.
- Topology: `projection` planned (bindings `track.changed`, `alert.changed`, `incident.changed`, lanes live/history, «revision-aware upsert»).
- Web: `api/signalr.ts` (4 обробники, `withAutomaticReconnect`, re-fetch snapshot on reconnect — у store), `store/useStore.ts`, `map/{MapView,KyivMapView}.tsx`,
  `map/layers.ts` (кольори/іконки з `palette.ts` і `EventType`), `EventKindDto` є в `api/types.ts`, `client.eventKinds()` є, але каталог у layers не використовується.
- **Паралельна робота в `web/`** (інший агент: public navigation shell — `App.tsx`, `useStore.ts`, `api/{client,types}.ts`, `components/*`, `stats/*`,
  `public/*` незакомічені). P11 не торкається цих файлів: інтеграція UI — нові модулі (`web/src/catalog/`, `web/src/map/incidentLayer.ts`, `web/src/api/incidents.ts`,
  `web/src/store/useIncidentStore.ts`, `components/IncidentPopup.tsx`) + `api/signalr.ts`, `map/{MapView,KyivMapView}.tsx` (не в їхньому diff). Коміт — за явним
  переліком файлів.

## Рішення

### D1. Контракти (additive, `Puluj.Contracts`)

- `IncidentDto(Id, Kind, KindName, Category, State, Suppressed, EventAt, FirstReportedAt, LastReportedAt, Location: IncidentLocationDto?, Confidence, SourceCount,
  Revision, ClosureReason, MergedIntoIncidentId, Provenance: IncidentProvenanceDto)`. `IncidentLocationDto(Kind, PlaceId, PlaceName, RegionId, RegionName, Point?,
  AccuracyKm, Precision)` — `Precision` ∈ `point|city|district|region|unknown` за §8.5: точка лише для `city`-рівня і точніше (`accuracy_km ≤ 15`), інакше
  клієнт малює коло/полігон похибки; без локації → `null` (стрічка, не мапа). `IncidentProvenanceDto(CanonicalObservationId, ObservationCount, SourceIds,
  PolicyVersion, LastEventId, GenerationId)`.
- `IncidentDetailsDto(Incident, Observations: IncidentObservationDto[] (ObservationId, TargetId, SourceId, SourceCode, Relation, Score, EffectiveAt, LinkedAt,
  SegmentText?, RawMessage: RawMessageDto? — через чинний `DtoMapper` (ті самі поля/редакція, що `TargetDto.RawMessage`)), Revisions: IncidentRevisionDto[]
  (Revision, Change, EffectiveAt, RecordedAt, Actor, Reason))`.
- `IncidentPageDto(From, To, Items, NextCursor, Truncated)`; `SnapshotDto` +`Incidents` (additive, default `[]`; `events`/legacy DTO лишаються).
- SignalR `IMapClient` +`IncidentUpserted(IncidentDto)` (change `created`), +`IncidentRevised(IncidentDto)` (усі інші зміни, включно з `suppressed`/`retracted`
  — клієнт прибирає з мапи за state/suppressed, а не за окремим методом). Клієнт ігнорує `revision ≤` відомої.

### D2. Read-side (`SnapshotService`/`IncidentQueries`)

- `GET /api/incidents?from&to&state&kind&category&cursor&limit&mode` — bounded window: default `to = now`, `from = to − IncidentHours` (MapOptions, default 24 год),
  max span 7 діб (400 інакше); `limit` 1..500 (default 200); cursor = base64 `(last_reported_at, incident_id)` keyset desc; `state` multi (`reported,confirmed`), default —
  без `retracted`; `suppressed` виключені, якщо не `includeSuppressed=true`. `mode` — history semantics §8.5: `effective` (default: вікно по `event_at`/`last_reported_at`,
  стан — поточний) | `recorded` з `asOf` (стан «як система знала тоді»: snapshot останньої ревізії з `recorded_at ≤ asOf`, вікно по `recorded_at`). ADR фіксує
  початковий history mode = `effective`.
- `GET /api/incidents/{id}` → details; `?revision=N` → стан зі snapshot ревізії N (as-of). 404.
- Snapshot: `LiveAsync` +incidents з `last_reported_at ≥ now − IncidentHours`, не suppressed/retracted, cap `IncidentSnapshotLimit` (default 1000, `Truncated`
  → у логах/метриці; клієнт дочитує `/api/incidents`); `AtAsync(at)` — стан на момент (`mode=recorded`, `asOf=at`: ревізія з `recorded_at ≤ at`).
- Query budget: індекси P10 `(state, last_reported_at)`, `(event_kind_id, event_at)`; keyset без OFFSET; `Include(Observations)` лише в details; список — проєкція без
  links (`SourceIds` з агрегованого subquery `array_agg(distinct source_id)`). Payload budget: `IncidentDto` без geometry-полігонів (лише point/accuracy; полігон
  місця клієнт бере з кешованого `/api/places/{id}/geometry`).

### D3. Projection consumer + push adapter + backplane

- Роль `projection` (`ProjectionHandler`, subscription `projection`, topology v8 → `active`, lanes live/history): на `incident.changed` → після commit
  NOTIFY `IncidentChanged{id, revision}` (`PulujEvent` +`Revision?`, +`PulujEventType.IncidentChanged`); `track.changed`/`alert.changed` → `noop`
  (NOTIFY для них уже емітують writers після commit — подвійний push не потрібен; консюмер лишає receipt для completion). Delivery — idempotent (noop на
  redelivery не шкодить: NOTIFY без стану). Replay lane не bound (§11 — shadow generation без live push).
- Push adapter у `NotifyBridge`: `IncidentChanged` → `IncidentDto` (вікно `IncidentHours`; за вікном — skip як для інших) → `IncidentUpserted` (revision 1) /
  `IncidentRevised`. `TargetCreated` лишається для feed.
- **Backplane** = PG NOTIFY: durable subscription — одна на логічну роль (`projection`, competing consumers у Worker), розсилка на всі API-репліки — LISTEN у
  кожній (NOTIFY доставляє кожному listener'у). ADR-0011 фіксує: payload NOTIFY — лише ids/revision (8 КБ ліміт NOTIFY не досягається), DTO читається з БД
  кожною реплікою (N реплік × 1 запит на подію — бюджет: ≤ 10 реплік, ≤ 50 подій/с). E2E-тест: 2 listener'и (2 «репліки») отримують один NOTIFY від
  projection-консюмера на реальних PG + RabbitMQ.
- Ролі: `projection` у `WorkerOptions.BrokerRoles`, `StageRoles`, Compose default roles (`projection` **вмикається за замовчуванням** — без domain writers він лише
  отримує `incident.changed`, яких без incident-worker немає; безпечно).

### D4. Web (ізольовано від паралельної роботи)

- `web/src/api/incidents.ts` (типи `IncidentDto`/`IncidentPageDto`/`IncidentDetailsDto` + `fetchIncidents/fetchIncident`), `web/src/store/useIncidentStore.ts`
  (zustand: `byId`, `apply(dto)` ігнорує `revision ≤` відомої, `prune(window)`, `reloadWindow()` на reconnect/checkpoint — не покладається на кожен пакет),
  `api/signalr.ts` +`incidentUpserted/incidentRevised`.
- `web/src/catalog/catalog.ts` — catalog adapter: `EventKindDto[]` → `{ layers, legend, filters, iconOf(kind), colorOf(kind), shapeOf(kind) }` з `mapColor/mapIcon/
  renderMode/mapVisible/category`; сенс не лише кольором — shape/icon + текстова легенда; catalog visibility (`mapVisible`) і user filter — окремі налаштування.
- `web/src/map/incidentLayer.ts` — GeoJSON: `precision point/city` → символ (icon за каталогом, cluster на zoom-out через maplibre `cluster: true`); `district/region`
  → коло похибки (`@turf/circle` — є `@turf/destination`; коло з 64 точок) + маркер з precision label; без локації — не на мапі. `IncidentPopup.tsx` — state, kind,
  часи (report/event), точність, джерела (count), revision, `policy_version`, permalink першого raw (через details).
- Parity: обидва `MapView` і `KyivMapView` додають шар одним хелпером `attachIncidentLayer(map, store)`. Dark/light — кольори з каталогу; keyboard — popup через
  існуючий hit-path (`hit.test.ts` патерн).
- Vitest: catalog adapter (fallbacks, visibility vs filter), store reducer (stale revision ignored, out-of-order, reconnect reload), incidentLayer geometry
  (precision → point vs circle; без локації → нічого).

### D5. Тести

| # | Сценарій | Доказ |
|---|---|---|
| R01 | Api.Tests: `IncidentDto` mapping — precision за kind/accuracy (city ≤ 15 км → point; region → circle, без point-as-precise), без локації → null; `SourceIds`, `Provenance` | unit |
| R02 | Api.Tests: cursor encode/decode round-trip; window validation (span > 7 діб → 400, `limit` clamp) | unit |
| R03 | Integration.Tests (PostGIS): 10 000 incidents (3 kinds, 5 регіонів, 48 год) → `GET /api/incidents` сторінками по 500 — кожна сторінка ≤ limit, без дублікатів/пропусків за cursor, keyset (EXPLAIN не робиться — assert на індекс через `pg_stat_user_indexes`? ні — лише функціональна повнота + час сторінки < 2 с на Testcontainers як smoke); snapshot cap 1000 + `Truncated` | query budget |
| R04 | Integration.Tests: as-of — `?revision=2` == snapshot ревізії 2; `mode=recorded&asOf=T` показує стан на T (до confirm), `effective` — поточний | history semantics |
| R05 | Messaging.Tests: `ProjectionHandler` на `incident.changed` (реальний incident-worker з P10 I01) → NOTIFY `IncidentChanged{id, revision}`; два `PgNotifyListener` (2 репліки) отримують обидва; `track.changed` → `noop`; redelivery → duplicate | backplane E2E |
| R06 | Api.Tests: push adapter — `created` → `IncidentUpserted`, `updated/resolved/suppressed` → `IncidentRevised`, за вікном → skip (fake hub context) | adapter |
| R07 | Api.Tests: SignalR contract — `IMapClient` методи/DTO серіалізуються camelCase+GeoJSON; `SnapshotDto` має `incidents` (additive: старий клієнт ігнорує) | contract |
| R08 | Vitest: store ignores stale revision / out-of-order; reconnect → `reloadWindow` викликано; catalog adapter; incidentLayer precision | client |
| R09 | Contracts.Tests: topology v8 `projection` active, bindings; `PulujEventType.IncidentChanged` | topology |

Notification burst (1k events) — R05 з 200 подіями через projection: усі NOTIFY отримані обома listener'ами (порядок не гарантується; клієнт — за revision).

### D6. Документація

`ADR-0011-read-side-realtime.md` (DTO/precision, history mode `effective` default + `recorded/asOf`, projection consumer, NOTIFY як backplane і його бюджет,
клієнтські правила revision/reconnect, budgets), ADR-0002 (v8), ADR-0009/0010 (NOTIFY incidents через projection), README контрактів («Runtime (P11)»),
`docs/README.md` (API таблиця: `/api/incidents`, hub methods), `fork-deployment.md` (роль `projection`), plan §16.1 (команди R03/R05/vitest) / §17, evidence
`P11-read-side-evidence.md`, handoff, manifest.

## Порядок

1. Contracts + `PulujEvent` + topology v8/роль. 2. `IncidentQueries`/endpoints/snapshot. 3. `ProjectionHandler` + `NotifyBridge` adapter. 4. Web modules + map
attach. 5. Тести R01–R09. 6. Docs, review, handoff, commit (явний перелік файлів), push.

## Ризики / межі

- `web/` паралельна робота: конфлікти уникаються ізоляцією файлів; якщо інший агент змінить `signalr.ts`/`MapView.tsx` — злиття вручну, лише свої hunks.
- NOTIFY-backplane — до ~10 реплік API; Redis backplane для SignalR не вводиться (ADR фіксує поріг перегляду).
- `AtAsync` для incidents — `recorded` mode за snapshot ревізій (не реконструкція links); `effective` реконструкція «за всіма даними» = поточний стан.
- Kyiv parity — той самий шар; фільтр за bbox робить MapLibre.

## Незалежне review плану — p11_review: approve after fixes → внесено

| # | Finding | Рішення в плані |
|---|---|---|
| B1 | LISTEN-gap: NOTIFY at-most-once (reconnect listener'а, crash між commit і AfterCommit; redelivery → inbox duplicate без повторного NOTIFY); браузер лишається підключеним і не перезавантажує вікно | `PgNotifyListener` сигналізує (re)connect → `NotifyBridge` шле additive hub-метод `Resync(at)` усім клієнтам → клієнт `reloadWindow`; клієнт також робить періодичний delta-reload (`from = checkpoint − slack`, 60 с); ADR-0011: «NOTIFY — at-most-once, reload — recovery». Тест: `pg_terminate_backend` LISTEN-з'єднання → сигнал reconnect; R05 — redelivery → inbox duplicate, **рівно один** NOTIFY |
| B2 | Precision із `accuracy_km ≤ 15` хибна: geometry incident'а — завжди центроїд місця, `LocationKind.Point` парсер не продукує | `Precision` лише з `LocationKind`: `Point → point`, `City → city` (маркер + precision label), `District/Area/Region → district/region` (площа: полігон місця з `/api/places/{id}/geometry`/regions-кешу, коло похибки — лише fallback), `Unknown/DirectionOnly → unknown` (не на мапі); R01: центроїд міста ніколи не `point` |
| B3 | Подвійні маркери: legacy `snapshot.events`/`TargetCreated` малюють вибухи поруч з incident-шаром | `MapView`/`KyivMapView`: legacy event-маркери kinds з `createsIncident` приховуються, коли incident-шар увімкнено (`TargetCancelled` лишається); серверний `events` — для старих клієнтів; vitest |
| N2 | Keyset за `(last_reported_at, id)`: оновлений incident може «перестрибнути» курсор; індекс `(state, last_reported_at)` не обслуговує сортування вікна | Міграція `AddIncidentReadIndexes`: `(last_reported_at DESC, incident_id DESC) WHERE NOT suppressed`; R03: `EXPLAIN (FORMAT JSON)` — без Seq Scan; skip-on-update задокументовано (push/reload покривають) |
| N3 | Без фільтра active generation | Запити list/live/one: `generation_id IN (SELECT generation_id FROM processing.generations WHERE is_active)`; `ProjectionHandler` не NOTIFY для неактивної generation (§11.4) |
| N4 | As-of зі snapshot: частина полів відсутня | У `recorded`/`?revision=` `policy_version`/`last_event_id` = null, назви kind/region — з поточного каталогу; observations details — поточні links (переміщені merge'ем — у поточному incident'і); recorded-список: candidate pre-filter `created_at ≤ asOf`, остання ревізія `recorded_at ≤ asOf`, вікно — по snapshot у пам'яті, власний limit |
| N5 | `Truncated` snapshot невидимий; клієнт не читає `snapshot.incidents` | `SnapshotDto.IncidentsTruncated` (additive); cap = найновіші за `last_reported_at`; web incident store завжди вантажить `/api/incidents` (snapshot.incidents — для §8.6/старих клієнтів/Admin) |
| N6 | Витік: `includeSuppressed` публічно, `Actor` ревізій | `includeSuppressed` лише в Admin (`/api/admin/incidents`); публічний `IncidentRevisionDto.Actor` редагується до `system|operator` |
| N7, N8 | `MapEvents` нові члени ламають `App.tsx`; `types.ts` чужий | Нові члени `MapEvents` — опційні; `signalr.ts` реєструє `IncidentUpserted/IncidentRevised/Resync` і диспатчить у `useIncidentStore` (batch 300 мс); reconnect — `useStore.subscribe` на `connection`; `CatalogKindDto` (superset) у `web/src/catalog/` |
| N9 | Keyboard/screen-reader/mobile без забезпечення | Мінімум у P11: DOM-легенда incident-kinds (текстові підписи, `aria-label`), DOM-список видимих incidents з кнопками (відкриває popup), Esc закриває; повна keyboard-навігація мапи — P12 (явно в ADR/evidence) |
| N10 | `deploy.ps1 $defaultMessagingRoles`; overhead receipts; mixed-version NOTIFY | `projection` у `$defaultMessagingRoles`; `PgNotifyListener` ігнорує невідомий `type` (debug, не warning) — rolling deploy без шуму; ADR: receipt на кожен `track/alert.changed` (retention ADR-0007) |
| N11 | ADR-0002 open items P11 | ADR-0011/0002: прямий NOTIFY writers лишається до перенесення track/alert push у projection (P12/P16); history lane projection — bound, push поза вікном відкидається бриджем |
| N12, N13, N14 | Тест-feasibility; burst 1k; числа бюджету | `IncidentPush` — статичний адаптер з fake `IMapClient`; R03 — `IncidentQueries` напряму на `PipelineFixture` (+incidents у TRUNCATE), bulk SQL, розмір сторінки 500 ≤ 500 КБ, snapshot ≤ 1 МБ; R05 — 1000 подій через projection, обидва listener'и отримують 1000; vitest 1k batch; fixture: роль `projection`, purge, counters |
| N15 | TTL мапи = `IncidentHours` ігнорує `mapLifetime` kind'а | Клієнт: видимість/prune за `mapLifetime` з каталогу (adapter), `IncidentHours` лише bounds запитів/push |
| Q1–Q6 | checkpoint; binding track/alert; `recorded_at` межа; хто читає `snapshot.incidents`; Kyiv polygons; `change` у NOTIFY | checkpoint = `IncidentPageDto.To` (delta-reload), таблиці checkpoint немає (P14); binding усіх трьох подій лишається (noop receipts); `recorded_at` — годинник writer'а до commit (наближення в межах tx, ADR); `snapshot.incidents` — §8.6/Admin/старі клієнти; Kyiv — полігони районів з `/api/places/regions`; NOTIFY без `change` — `revision ≤ 1 → Upserted` (split-новий теж revision 1) |
