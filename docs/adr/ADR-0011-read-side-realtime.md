# ADR-0011 — Read-side incidents, history mode, projection consumer і NOTIFY-backplane

Статус: accepted (P11). Стосується плану §8.5–8.6, §11.4, ADR-0002 (v8), ADR-0005 (generations), ADR-0009/0010 (NOTIFY writers, incidents).

## Контекст

Після P10 incidents існують лише в БД; карта показує вибухи як `targets` (`snapshot.events`, `TargetCreated`). §8.6 вимагає additive контракти
(`IncidentDto`, `GET /api/incidents`, `/{id}`), bounded queries (не вся історія в snapshot), два часових зрізи (§8.5), realtime-міст «на роль, не на браузер»
з broadcast на всі API-репліки, і клієнт, що не покладається на кожен websocket-пакет.

## Рішення

1. **Контракти (additive).** `IncidentDto` (state, `suppressed`, три часи `event_at`/`first_reported_at`/`last_reported_at`, `IncidentLocationDto` з `precision`,
   `confidence`, `source_count`, `revision`, `closure_reason`, `merged_into_incident_id`, `IncidentProvenanceDto`: canonical observation, кількість
   observations, `source_ids`, `policy_version`, `last_event_id`, `generation_id`); `IncidentDetailsDto` (+links з raw через чинний `DtoMapper.RawMessage`,
   +ревізії — публічно `actor` редагується до `system|operator`); `IncidentPageDto`; `SnapshotDto +incidents, +incidents_truncated`; `MapConfigDto
   +incident_hours`; hub `IMapClient +IncidentUpserted(dto)` (revision 1) `+IncidentRevised(dto)` (усе інше — клієнт сам прибирає retracted/suppressed/merged)
   `+Resync(at)`. Legacy `events`/`TargetCreated` лишаються до кінця compatibility window (клієнт ховає legacy-маркери kinds із `creates_incident`,
   коли incident-шар увімкнено — один маркер на вибух).
2. **Precision (§8.5)** — лише з `LocationKind` evidence: `Point → point`; `City → city` (маркер з підписом «населений пункт, не адреса»); `District/Area/Region
   → district/region` (площа: полігон місця, якщо клієнт його має, інакше коло `accuracy_km`; глиф — лише якір з «≈»); `Unknown/DirectionOnly → unknown` (не на
   мапі). Geometry incident'а — центроїд місця, тому малий `accuracy_km` **ніколи** не робить його точкою; `accuracy_km` — лише радіус/підпис.
   Рядки без kind, але з місцем (старі дані): рівень місця вирішує — область/країна/named area → `region`, район/громада → `district`, місто/селище →
   `city` (named area ніколи не «місто»).
3. **History mode.** `mode=effective` (default): поточний стан кожного incident'а, чиї повідомлення потрапляють у вікно — «реконструкція за всіма відомими
   даними». `mode=recorded&asOf=T`: «що система знала на T» — остання ревізія з `recorded_at ≤ T` зі snapshot (`incident_revisions.snapshot`), вікно — за
   `last_reported_at` snapshot'а; `GET /api/incidents/{id}?revision=N` — стан ревізії N. `/api/snapshot?at=` для incidents — `recorded`. У as-of DTO
   `policy_version`/`last_event_id` = null, назви kind/region — з поточного каталогу, links details — поточні. `recorded_at` — годинник writer'а до commit
   (наближення в межах tx).
4. **Query budget.** Вікно ≤ `Map:IncidentMaxWindowDays` (7; ширше → 400), default `Map:IncidentHours` (24); keyset `(last_reported_at DESC, incident_id DESC)`
   через індекс `ix_incidents_read_keyset` (partial `NOT suppressed`; R03: план — Index Scan, без Seq Scan/Sort); page ≤ 500 (default 200); список без links
   (source ids — один агрегований запит); лише active generation(s) (`processing.generations.is_active`, §11.4); публічний список ніколи не містить
   suppressed (admin має свій). Оновлений incident може «перестрибнути» курсор (skip, не дублікат) — push/reload покривають. Payload: `IncidentDto` без
   полігонів (≤ ~1 КБ; сторінка 500 ≤ 500 КБ; snapshot incidents ≤ `Map:IncidentSnapshotLimit` = 1000, `incidents_truncated`), полігони — з кешованих
   `/api/places/*`. `GET /{id}` теж лише active generation і не suppressed (404 інакше). **Recorded mode** не має серверного cap на кандидатів: усі ревізії
   (зі snapshot) кандидатів вікна ≤ 7 діб читаються й пагінуються в пам'яті; `IncidentPageDto.truncated` зарезервовано (завжди false) — cap кандидатів і
   checkpoint-таблиця — P14; клієнт throttle'ить history-reload до 3 с (replay-тики/скраб не запускають запит щосекунди). Публічний `IncidentRevisionDto.reason`
   (нотатки операторів) лишається видимим — це частина audit trail для читача; `actor` редагується.
5. **Projection consumer (роль `projection`, topology v8, lanes live/history; власна реєстрація `AddPulujProjection` — працює без domain writers).** `incident.changed` → після commit NOTIFY `IncidentChanged{id, rev}`
   (`PulujEvent.Revision`); `track.changed`/`alert.changed` → `noop writer_notifies` (NOTIFY для них емітують writers після commit — ADR-0009; перенесення в
   projection — P12/P16, тоді прямий NOTIFY writers відходить); replay lane / неактивна generation → `noop not_live` (§11.4). Роль увімкнена за замовчуванням
   (Compose/deploy.ps1): без domain writers подій немає, з ними — receipt на кожну aggregate-подію (retention ADR-0007).
6. **Backplane = Postgres NOTIFY.** Durable subscription — одна на логічну роль (competing consumers у Worker); кожна API-репліка тримає власний LISTEN і
   штовхає DTO своїм SignalR-клієнтам (`NotifyBridge` → `IncidentQueries.OneAsync` → `IncidentUpserted|IncidentRevised`; за вікном `IncidentHours` —
   skip). Payload NOTIFY — лише id/revision (ліміт 8 КБ не досягається); N реплік × 1 запит на подію — бюджет ≤ 10 реплік, ≤ 50 подій/с (R05: burst 1000
   подій — обидві репліки отримали всі). **NOTIFY — at-most-once**: reconnect LISTEN, crash між commit і `AfterCommit`, redelivery (inbox duplicate без
   повторного NOTIFY) — усе губить push. Recovery — reload, не replay пакетів: `PgNotifyListener` після кожного повторного підключення видає маркер
   `ListenerReconnected` → `NotifyBridge` шле `Resync(at)` усім клієнтам репліки → клієнт перезавантажує вікно з випадковою затримкою 0–5 с (не herd);
   клієнт також перезавантажує вікно на власний reconnect і робить delta-reload кожні 60 с від checkpoint (`IncidentPageDto.to` − 5 хв); відповідь
   застарілого reload ніколи не перекриває новіший (sequence guard), а push, що прийшов під час reload, зберігає вищу revision. Rolling deploy: listener старої збірки ігнорує невідомий
   `type` (debug-лог), не warning на подію. Redis/інший SignalR backplane не вводиться — поріг перегляду: > 10 реплік або > 50 подій/с.
7. **Клієнт.** `useIncidentStore`: DTO з `revision ≤` відомої ігнорується (out-of-order/дубль з двох реплік), retracted/suppressed/merged прибираються, push
   батчиться (300 мс); catalog adapter (`/api/event-kinds`): колір/форма/іконка/lifetime з каталогу, сенс не лише кольором (форма + текстова легенда);
   catalog `map_visible` і фільтр користувача — окремі; видимість на мапі — за `map_lifetime` kind'а (`IncidentHours` лише bounds запитів/push); один шар для
   `MapView` і `KyivMapView` (parity by construction; Kyiv — полігони районів з `/api/places/regions`); шари incidents сидять під треками (`hover-region-fill`),
   symbol-шари називають шрифт стилю (symbol без glyphs блокує весь source); DOM-легенда + список видимих incidents (кнопки, `aria-label`, Esc) — мінімум
   доступності; повна keyboard-навігація canvas — P12.

## Альтернативи

- SignalR Redis backplane — відкинуто до порогу з п.6: ще один stateful сервіс заради fan-out, який NOTIFY дає безкоштовно.
- Projection як власна read-таблиця — відкинуто: `incidents` уже є read model owner'а; projection лише push-adapter + receipts (checkpoint-таблиця — P14).
- NOTIFY з повним DTO — відкинуто: 8 КБ ліміт, N реплік і так читають з БД; DTO з БД — завжди найновіша revision.

## Наслідки

- `projection` required у v8: completion/reconciliation очікують його receipts для track/alert/incident.changed — роль має працювати там, де є writers.
- Клієнт без `/api/incidents` (старий) бачить `snapshot.incidents` і ігнорує; старий hub-клієнт ігнорує нові методи.
- ADR-0002 open items P11: «retire NOTIFY bridge» — після перенесення track/alert push у projection (P12/P16); history queue projection — bound, push поза
  вікном відкидає бридж.

## Відкрите

Checkpoint-таблиця projection і delta за `recorded_at` — P14; перенесення track/alert push — P12/P16; feed окремих incidents (не лише мапа) — P12;
keyboard-навігація мапи, mobile-layout легенди — P12; benchmark N реплік × 50 подій/с — після cutover.

## Перевірка

`IncidentReadSideTests` (Api: precision, cursor/validation, push adapter, wire contract; Integration: 10k incidents keyset без пропусків/дублікатів, план з
`ix_incidents_read_keyset`, сторінка ≤ 500 КБ, snapshot cap, as-of/recorded), `ProjectionTests` R05 (2 репліки, redelivery без NOTIFY, noop track/shadow,
history lane, reconnect → resync, burst 1000), vitest (catalog, store revision guard/batch/reload, layer precision/legacy filter), `P11-read-side-evidence.md`.
