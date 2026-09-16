# P11 — read-side/API, incident DTO, SignalR bridge/backplane

Task: [P11 / issue #11](https://github.com/sql-monk/Puluj-g/issues/11).
Status: **done** — реалізація, тести, документація; незалежне review результату (`p11_review`): approve after fixes → B1 (роль `projection` не стартувала без domain writers), B2 (кластерний symbol без `text-font` блокував source), B3 (history-reload на кожен replay-тик), N1–N13, Q1–Q7 — виправлено/задокументовано → повторний прогін зелений. Rollout не виконувався (default deploy:
роль `projection` увімкнена — без domain writers без ефекту; міграція additive).
Owner: Claude Code (Opus 5), агент `p11`. Reviewer: план — `p11_review` (approve after fixes; B1 at-most-once/`Resync`, B2 precision з `LocationKind`, B3 legacy-маркери,
N1–N15, Q1–Q6 — внесено до старту, `P11-plan.md`); результат — `p11_review` (таблиця нижче).
Base commit: `78b3797` (P10); результуючий commit — цей handoff комітиться разом із кодом (`P11-build-manifest.json` перелічує файли; паралельна робота
іншого агента в `web/` (public UI) до коміту не входить).

## Результат для споживача

- **Контракти (additive, ADR-0011):** `IncidentDto`/`IncidentLocationDto{precision}`/`IncidentProvenanceDto`, `IncidentDetailsDto` (links з raw, ревізії з
  редагованим `actor`), `IncidentPageDto`; `SnapshotDto +incidents +incidentsTruncated`; `MapConfigDto +incidentHours`; hub `IncidentUpserted`/`IncidentRevised`/
  `Resync`. Legacy `events`/`TargetCreated` лишаються.
- **`GET /api/incidents`** — bounded вікно (24 год default, ≤ 7 діб), keyset cursor, фільтри state/kind/category, `mode=effective|recorded&asOf` (§8.5 history
  mode; ADR: default `effective`), лише active generation, без suppressed; **`GET /api/incidents/{id}?revision=`** — details/as-of. Snapshot: incidents вікна
  (cap 1000) / recorded для `at`.
- **Query/payload budget:** індекс `ix_incidents_read_keyset` (міграція `AddIncidentReadIndexes`), план без Seq Scan/Sort, сторінка ≤ 500 КБ, snapshot ≤ 1 МБ
  (R03 на 10k рядків).
- **Роль `projection`** (topology v8): `incident.changed` → NOTIFY `IncidentChanged{id, rev}`; track/alert → noop receipts; replay/неактивна generation → noop.
  **Backplane = NOTIFY** на всі API-репліки (R05: 2 репліки, burst 1000); at-most-once → `ListenerReconnected` → `Resync` → клієнт reload; delta-reload 60 с.
- **Web:** `useIncidentStore` (revision guard, batch 300 мс, reload на reconnect/Resync/зміну режиму, delta від checkpoint), catalog adapter (легенда/іконки/
  lifetime з `/api/event-kinds`, catalog visibility ≠ user filter), incident layer для обох мап (precision → глиф/полігон/коло, clustering, legacy-маркери
  incident-kinds приховані), `IncidentPopup` (state, обидві шкали часу, точність, джерела, revision/policy, permalink raw, ревізії), DOM-легенда + список
  видимих incidents (a11y мінімум).
- Ролі/деплой: `projection` у `WorkerOptions`/`StageRoles`/Compose default/`deploy.ps1`.

Docs: ADR-0011 (новий), ADR-0002 (v8, open items P11 закрито), ADR-0009/0010 (посилання), `docs/adr/README.md`, README контрактів («Runtime (P11)»),
`docs/README.md` (endpoints, hub), `fork-deployment.md` (Projection), plan §16.1 (команди) / §17.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, .NET SDK 10.0.401, Node/vitest 5, Docker 29.8.0, `postgis/postgis:17-3.5`, `rabbitmq:4.3-management` (Testcontainers 4.15.0).
Усі .NET — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p11-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `dotnet test tests/Puluj.Api.Tests --filter IncidentReadSideTests` | 0 | **4/0** |
| `dotnet test tests/Puluj.Integration.Tests --filter IncidentReadSideTests` run1 → run4 | 1 → 0 | 0/2 → 1/1 → 2/0 → **2/0** (run4 після review) |
| `dotnet test tests/Puluj.Messaging.Tests --filter ProjectionTests` | 0 | **1/0** (R05: 2 репліки, redelivery, reconnect, burst 1000) |
| `npx vitest run src/catalog src/store/useIncidentStore.test.ts src/map/incidentLayer.test.ts` → після review | 0 | 16/0 → **19/0** |
| `npx tsc --noEmit -p tsconfig.app.json`; `npx vite build --outDir <scratch>` | 0; 0 | — |
| `dotnet test tests/Puluj.Messaging.Tests` → run2 (після review) | 0 → 0 | 84/0/1 → **86/0/1** |
| `dotnet test tests/Puluj.Integration.Tests` → run2 | 0 → 0 | 39/0/1 → **39/0/1** |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` (v8) | 0 | 61/0/0 |
| `dotnet test tests/Puluj.Processing.Tests` / `Admin` / `Api` (run2) / `Analytics` | 0 | 134, 64, 34, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |
| `npm test` (весь web) | 1 | 130/1 — падіння `src/public/query.test.ts` належить паралельній задачі public UI (не P11) |

## Evidence

[`P11-read-side-evidence.md`](P11-read-side-evidence.md), `messaging-crash-evidence.json` (P11-R05), план R03 у evidence.

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| B1 | `projection` реєструвався лише всередині `AddPulujDomainWriters` → з default-ролями Compose (без writers) консюмер не стартував, required-черга без споживача | окремий `AddPulujProjection(roles)`; Worker викликає його для будь-якого набору broker-ролей; fixture реєструє так само | `ProjectionRegistrationTests` (2), R05 |
| B2 | `incident-cluster-count` без `text-font` → glyph-запит до fallback-шрифту 404 → увесь source incidents не рендериться при кластерах | `text-font: TEXT_FONT`; specs експортовані; шари під `hover-region-fill` | vitest «every symbol layer names the style font» |
| B3 | `bindIncidentRealtime` робив recorded-reload на кожну зміну `at` (replay: щосекунди; скраб — на кожен drag) | trailing throttle 3 с для history, окремий reload лише на зміну режиму; sequence guard проти застарілих відповідей | vitest binding/throttle, stale reload |
| N1 | `DetailsAsync` без generation/suppressed-фільтра — moderated incident читався за id | `Active(db)` + `!Suppressed` → 404 | R04 |
| N2 | Порядок arm'ів `Precision`: named area ставала «містом»; правило «без kind» не в ADR | coarse-рівні першими, місто — лише City/Town/Village; ADR-0011 п.2 | R01 |
| N3 | `AtAsync` обрізався до 500 і не повертав truncation | пагінація до `IncidentSnapshotLimit` + `IncidentsTruncated` у history snapshot | build |
| N4 | Іконки перемальовувались щосекунди (атлас, re-layout symbol-шарів) | іконки лише в install і на зміну catalog/palette | код |
| N5 | `catalogLoaded` не скидався при помилці fetch | скидання у `catch` (повторна спроба) | код |
| N6 | `Resync` → herd | jitter 0–5 с (`resync()`), ADR | vitest resync |
| N7 | `reloadWindow` затирав push'і, що прийшли під час fetch | merge: вища revision зберігається | vitest |
| N8 | План R03 — на ручному SQL, без назви індексу; seed без geometry; тавтологічний SRID | `IncidentQueries.ListSql` (`ToQueryString`), параметри з header'а, assert `"Index Name": "ix_incidents_read_keyset"`, geometry центроїда в seed, SRID 4326 | R03 run4 |
| N9 | Reconnect/Resync шлях клієнта без тестів | vitest: reconnect → 1 reload, throttle, delta-таймер, stale reload, resync jitter | store tests 7 |
| N10 | XML-doc `IncidentLocationDto` зі старим правилом | переписано | — |
| N11 | `RedactActor` за `@` | за префіксом producer'а `incident-worker` (`SystemActorPrefix`) | R02/R04 |
| N12 | `incidentSelect` ref після mount-ефекту (MapView) | перенесено перед ефект, як у Kyiv | tsc |
| N13 | Речення в ADR-0009 п.4 розірване | переписано | docs |
| Q1–Q7 | recorded-mode budget, `truncated` reserved, `reason` публічний, R05 synthetic envelope/oldest LISTEN, легенда над ScaleControl (P12), порядок шарів, `= ANY` translation | ADR-0011 п.4/п.7, evidence | docs |

Reviewer підтвердив: `IsDescending()` = DESC для обох колонок (план — forward Index Scan), zustand 5 `subscribe(state, prev)`, maplibre 6.9 толерантний до `pointerCursor` на ще не доданих шарах, JSON HTTP/SignalR спільний (`ConfigureJson`), R05 доводить 2 listener'и × 1 NOTIFY, redelivery без NOTIFY, reconnect-маркер, burst 1000; migration Up/Down симетрична.

## Відомі обмеження / невиконані перевірки

- HTTP-рівень endpoints без WebApplicationFactory (сервіс покритий на реальній БД); NotifyBridge → hub — через адаптер і listener, без live hub.
- Benchmark N реплік × 50 подій/с — після cutover (поріг перегляду backplane: > 10 реплік / > 50 подій/с).
- Keyboard-навігація canvas, mobile-layout легенди, feed incidents, перенесення track/alert push у projection — P12/P16; checkpoint projection — P14.
- `npm run build` у `wwwroot` не запускався (паралельна робота в `web/`); bundle зібрано у scratch. Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy: міграція `AddIncidentReadIndexes` (additive index), роль `projection` за замовчуванням (безпечно без writers). Rollback: прибрати `projection`
з `Worker__Roles`, стара API-збірка ігнорує нові NOTIFY-типи (debug). Ownership: `Puluj.Api/Services/{IncidentQueries,IncidentPush}`, `Puluj.Processing/Projection`,
`web/src/{catalog,store/useIncidentStore,map/incidentLayer,map/useIncidentLayer}`, topology v8.

## Чекбокси issue #11

- [x] API/SignalR contract tests (R07, R01/R02, Contracts v8); reconnect/out-of-order (R05 reconnect, R08 store); broadcast на кілька API instances (R05, 2 репліки, burst 1000); query budget (R03: 10k, план, payload)
- [x] Тести на актуальній збірці з реальними PostGIS/RabbitMQ; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (додано additive), міграція/rollback, конфігурація (ролі, Compose, deploy.ps1, `Map:Incident*`), документація (ADR-0011, 0002/0009/0010, README, fork-deployment, plan) оновлені

## Наступний task

P12 (issue #13) — UI/catalog/feed, Playwright E2E, keyboard/mobile parity (§8.6 решта, §16.1 harness).
