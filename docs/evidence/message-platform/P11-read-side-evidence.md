# P11 — read-side/API, incident DTO, SignalR bridge/backplane: evidence

Джерела: `Puluj.Api.Tests/IncidentReadSideTests.cs` (R01, R02, R06, R07 — unit), `Puluj.Integration.Tests/IncidentReadSideTests.cs` (R03, R04 — Testcontainers
PostGIS), `Puluj.Messaging.Tests/Integration/ProjectionTests.cs` (R05 — PostGIS + RabbitMQ; підсумок у [`messaging-crash-evidence.json`](messaging-crash-evidence.json)
`P11-R05`), `web/src/{catalog/catalog,store/useIncidentStore,map/incidentLayer}.test.ts` (R08 — vitest), `Puluj.Messaging.Contracts.Tests` + unit
`TopologyRegistryTests` (R09 — topology v8). TRX — `test-results/p11-*.trx`; план запиту R03 — `p11-r03-plan.json` (у `bin` тестів, витяг нижче).

## Gate issue #11

| Gate | Тест | Підсумок |
|---|---|---|
| **API/SignalR contract** | R07: `SnapshotDto` серіалізується camelCase з GeoJSON `point`, `incidents`/`incidentsTruncated` additive (без incidents — старий JSON байт-у-байт), null-поля опущені; `IMapClient` = 7 методів по одному DTO (`IncidentUpserted`, `IncidentRevised`, `Resync` додані); R01: precision лише з `LocationKind` (city з малим радіусом — `city`, не `point`; region/district — area; unknown/direction-only — `unknown`), R02: cursor round-trip/invalid → 400, вікно > 7 діб → 400, `limit` clamp 1..500, default states без `retracted`, `recorded` без `asOf` → 400, `RedactActor` | ✅ 4/4 |
| **Query budget** | R03: 10 000 incidents (3 kinds × 5 областей × 48 год, з geometry центроїда області; 1/7 — shadow generation, 1/13 — suppressed, 1/11 — resolved) → сторінки по 500 через keyset: **7 912 = усі видимі live-рядки рівно по одному разу**, без дублікатів/пропусків, shadow generation не потрапляє, `Point` SRID 4326; найбільша сторінка ≤ 500 КБ; `EXPLAIN (FORMAT JSON)` **того самого SQL, що генерує EF** (`ListSql` → `ToQueryString`, параметри з header'а) — `"Index Name": "ix_incidents_read_keyset"`, без `Seq Scan` і `Sort` (Nested Loop + Memoize з `generations`); snapshot cap 1000 найновіших + `Truncated`, ≤ 1 МБ; вікно 30 діб → `QueryException` «7 days» | ✅ |
| **History semantics (§8.5)** | R04: дві ревізії (`created` t1, `updated/confirmed` t2): `?revision=1` = `reported`, 1 джерело; `?revision=9` → null; suppressed або shadow-generation incident → null (404) навіть за id; `mode=recorded&asOf` між ревізіями → `reported`, після → `confirmed`, до першої → порожньо; `effective` → поточний `confirmed` rev 2; ревізії публічно `system`/`operator` (за префіксом producer'а `incident-worker`) | ✅ |
| **Broadcast на кілька API instances** | R05: 2 «репліки» (`PgNotifyListener` кожна) + projection-консюмер: `incident.changed` created/updated → обидві отримали `IncidentChanged{1,1}`,`{1,2}`, deliveries `completed` 2; **redelivery того самого event_id → inbox duplicate, NOTIFY не повторився** (at-most-once); `track.changed` → `noop writer_notifies`; неактивна generation → `noop not_live`; history lane → push обом; `pg_terminate_backend` LISTEN-з'єднання → рівно один маркер `ListenerReconnected` (→ `Resync`), обидві знову слухають; **burst 1 000** `incident.changed` (100 incidents × 10 ревізій) → обидві репліки отримали всі 1 000 (unique (id, rev) = 1000) за < 120 с | ✅ |
| **Reconnect / out-of-order** | R08 store: revision ≤ відомої ігнорується (пізній rev 2 після rev 3, дубль rev 3 з другої репліки), retracted/suppressed/merged прибираються і знімають вибір, 1 000 push → **один** `applyMany` (batch 300 мс), prune за `mapLifetime` kind'а, `reloadWindow` замінює набір і ставить checkpoint, `reloadDelta` стартує з checkpoint − 5 хв; **binding**: `reconnecting → connected` → рівно один reload, 10 replay-тиків `at` у history → ≤ 3 recorded-reload (throttle 300 мс у тесті / 3 с у коді), delta-таймер → fetch від checkpoint; **stale reload** (повільна відповідь після швидшої) відкидається, push під час reload зберігає вищу revision, `Resync` → reload лише в межах jitter-вікна; R06 адаптер: rev 1 → `IncidentUpserted`, інші → `IncidentRevised` | ✅ 7 + 1 |
| Symbol-шари | layer specs: кожен symbol-шар з `text-field` називає `TEXT_FONT` стилю (без цього кластерний підпис блокував би весь source incidents — review B2); порядок шарів під `hover-region-fill` | ✅ |
| Catalog adapter / precision на клієнті | R08 catalog: колір/іконка→форма/renderMode/`mapLifetime` з каталогу, fallback за категорією (форма + підпис завжди), catalog `mapVisible` ≠ user filter, легенда за `sortOrder` лише incident kinds, TimeSpan parse; layer: city → глиф у точці з `precision city`, region → полігон місця + якір «≈», district без полігону → коло похибки (64 вершини, радіус = accuracy) і запит полігону, без локації/unknown → нічого, lifetime/hidden/enabled, legacy-маркери kinds із `createsIncident` приховані при увімкненому шарі | ✅ 5 + 6 |
| Topology v8 | Contracts 61/0 (`projection` active, `emits []`), unit: `track.changed`/`incident.changed` live → `archive, projection`; replay → без projection | ✅ |
| Web build | `tsc --noEmit` 0 помилок; `vite build` (у scratch outDir — `wwwroot` не чіпався через паралельну роботу) ✓; lint — 0 warnings у файлах P11 | ✅ |

## Витяг плану R03 (`EXPLAIN (FORMAT JSON)`)

```
Limit → Nested Loop (Inner Unique) →
  Index Scan using ix_incidents_read_keyset on incidents i
    Index Cond: last_reported_at >= $from AND last_reported_at <= $to
    Filter: state = ANY('{reported,confirmed,resolved}')
  Index Scan on processing.generations (generation_id = i.generation_id) Filter: is_active
```

## Запуски

| Файл | Результат | Примітка |
|---|---|---|
| `p11-api-run1.trx` | **4 / 0** | R01/R02/R06/R07 |
| `p11-readside-run1.trx` → `-run2` → `-run3` → `-run4` | 0/2 → 1/1 → 2/0 → **2 / 0** | run1: очікувана кількість (7 912) і `created_at` seed-рядка > `asOf`; run2: планувальник без `ANALYZE` після COPY обрав інший індекс → `ANALYZE` перед EXPLAIN; run4 (після review N8/N1): план з реального EF SQL + індекс за назвою, geometry у seed, suppressed/shadow details → null |
| `p11-projection-run1.trx` | **1 / 0** | R05 з першого разу, 20 с |
| vitest (P11 suites) | 16 / 16 → **19 / 19** | catalog 5, store 5 → 7 (binding/throttle, stale reload/resync), layer 6 → 7 (symbol font); повний `npm test`: падіння `src/public/query.test.ts` належить паралельній задачі public UI, не P11 |
| `p11-Puluj.Api.Tests.trx` → `-run2` | 34 / 0 → **34 / 0** | +4; run2 після review (precision arm order, RedactActor за префіксом) |
| `p11-Puluj.Messaging.Contracts.Tests.trx` | 61 / 0 | v8 |
| `p11-Puluj.Processing.Tests.trx` / Admin / Analytics | 134 / 0, 64 / 0, 33 / 0 | регресія |
| `p11-Puluj.Messaging.Tests.trx` → `-run2` | 84/0/1 → **86 / 0 / 1** | повний проєкт (+R05, +2 unit `ProjectionRegistrationTests` після review B1; skip — P04-C06 W1c) |
| `p11-Puluj.Integration.Tests.trx` → `-run2` | 39/0/1 → **39 / 0 / 1** | +R03/R04; `MapSnapshotTests` з новим конструктором `SnapshotService` |

## Що не покрито (свідомо)

- HTTP-рівень `/api/incidents` (WebApplicationFactory) — endpoints тонкі над `IncidentQueries` (R03/R04 на реальній БД); NotifyBridge → SignalR — через
  адаптер `IncidentPush` (R06) і listener (R05), без реального hub-з'єднання.
- Benchmark N реплік × 50 подій/с на production-shaped даних — після cutover; R05 — 2 репліки, 1 000 подій на Testcontainers.
- Keyboard-навігація canvas, mobile-layout легенди, feed incidents — P12 (DOM-легенда/список — мінімум).
- `npm run build` у `wwwroot` не запускався (паралельна робота іншого агента над `web/`; bundle перевірено у scratch outDir).
