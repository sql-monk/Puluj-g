# P12 — map quality blockers, main/Kyiv parity, catalog UI, incident popup/filters, E2E harness — план

Task: [P12 / issue #13](https://github.com/sql-monk/Puluj-g/issues/13). Залежності: P00 (blocker audit), P11 (повний зріз read-side) — done. Base commit: `4917327`.
Gate: E2E/visual — точність геометрії, provenance, history, mobile/accessibility, 1k/10k workload.

## Стан коду на старті (звірено)

- P11 дав incident-шар для обох мап (precision → глиф/полігон/коло, clustering, legacy-маркери incident-kinds приховані), `IncidentPopup`, DOM-легенду зі
  списком, store з revision guard/reload. Немає: E2E/visual harness (§16.1: «Playwright немає — додати в P12»), catalog UI (§8.7), черги ревʼю incidents з
  merge/split/resolve/suppress + preview, mobile-layout легенди (P11 Q5), кольори legacy event-маркерів досі з enum у `geojson.ts` (§8.6: «дублювання
  enum/colors»), `EventPopup` без source permalink.
- Admin API (P10): `/api/admin/incidents` list/get + команди; каталог kinds — лише seed (`EventKindSeeder`: presentation оновлюється при новішому `policyVersion`,
  зберігається лише `Enabled`), `/api/event-kinds` read-only.
- **Паралельна робота в `web/`** (public UI U01–U12: `App.tsx`, `useStore.ts`, `api/{client,types}.ts`, `components/{FilterPanel,KyivPanel,TopBar,ReplayBar,
  DataFilterControls}`, `stats/*`, `public/*`). P12 не торкається цих файлів: фільтри incidents лишаються в `IncidentLegend` (P11), інтеграція у `FilterPanel` —
  за U-задачами. Admin (`web/src/admin/*`, `Puluj.Admin`) поза їхнім diff.
- Playwright: `@playwright/test` 1.58 + Chromium встановлено (P12 крок 0); dev-сервер Vite :5183 проксіює `/api`,`/hubs` на :5267.

## Рішення

### D1. E2E/visual harness (Playwright, `web/e2e/`)

- `web/playwright.config.ts`: `webServer: vite --port 5183` (без .NET), Chromium, проєкти `desktop` (1280×800) і `mobile` (390×844, touch); `npm run e2e`.
  API повністю мокається через `page.route` з детермінованими fixtures (`web/e2e/fixtures/*.json`: map config, regions (2 oblast + 1 raion полігони + Kyiv),
  event-kinds (реальний seed), snapshot, `/api/incidents` сторінки, details, places geometry); SignalR: `/hubs/map/negotiate` → 404 (клієнт лишається
  `disconnected`, incidents вантажаться через store). Реальні PostGIS/RabbitMQ для UI-E2E не потрібні — це UI-контракт; API-контракт покривають P11 тести.
- DEV-хук `window.__incidents = useIncidentStore` (як `window.__map`) — асерти на стан/шари через `page.evaluate`; visual — `toHaveScreenshot` попапу/легенди
  зі стабільними fixtures (маска мапи-підкладки: `stylePath` замінюємо порожнім style у E2E через `VITE_E2E=1` → `STYLE_*` без тайлів — інакше знімки
  нестабільні).
- Сценарії:

| # | Сценарій | Доказ |
|---|---|---|
| E01 | **Precision**: 5 incidents (point/city/district-без-полігону/region-з-полігоном/без локації) → source `incident-points` містить 4, `incident-areas` 2 (полігон області + коло району з 65 вершин), без локації відсутній; попап region показує «область — приблизна область (±N км)», city — «населений пункт (маркер, не адреса)» | геометрія §8.5 |
| E02 | **Provenance popup**: клік по глифу → state, обидві шкали часу, джерела/кількість, revision + policy_version, текст raw, permalink `href` = `rawMessage.url`, ревізії з `system`/`operator`; Esc закриває; legacy `EventPopup` має permalink | provenance |
| E03 | **History**: перемикання в history (`#/map/history` або store `setMode('history', at)`) → запит `/api/incidents?...mode=recorded&asOf=`, snapshot `?at=`; replay-тики (10 × `setMode`) → ≤ 2 recorded-запити (throttle); назад у live → `effective` | history |
| E04 | **Main/Kyiv parity**: той самий fixture; Kyiv incident (district Печерський, полігон з regions) видимий на обох мапах з однаковими properties; legacy explosion-маркер прихований на обох | parity |
| E05 | **Filters/catalog**: легенда з каталогу (порядок `sortOrder`, підписи, `mapVisible=false` kind не в легенді); toggle kind → шар без нього; catalog visibility ≠ user filter; `filters.events` off → шар порожній, legacy-маркери повертаються | filters |
| E06 | **Mobile/accessibility** (проєкт mobile): легенда згорнута, розкривається, не перекриває ScaleControl/attribution; список видимих — `<button>` з текстом, Tab досяжність, Enter відкриває попап (`role=dialog`, `aria-label`), Esc закриває; кольори не єдиний носій (shape-підпис у легенді); axe-core (`@axe-core/playwright`) без `serious/critical` на мапі з відкритим попапом і легендою | a11y |
| E07 | **Workload 1k/10k**: `/api/incidents` → 10 000 items у 20 сторінках по 500 (fixture генерується детерміновано) → store ≤ 10 000, час до `incident-points` з даними ≤ 5 с, кластери на zoom 5 (≥ 1 feature `point_count`), пам'ять не вимірюємо; 1k push-burst через `window.__incidents.applyMany` → один render (перевіряємо `byId` size і `<= 1` s) | budget |
| E08 | **Reconnect/resync**: `window.__incidents.resync(0)` → один новий `/api/incidents` запит; stale-guard: два reload підряд → останній виграє (зафіксовано у vitest P11, тут — лише запит) | reconnect |

### D2. Catalog UI (§8.7, Admin)

- API `Puluj.Admin` `CatalogEndpoints`: `GET /api/admin/event-kinds` (усі, включно з disabled, + `presentation`/`metadata`), `PUT /api/admin/event-kinds/{code}`
  (`{nameUk?, mapVisible?, mapColor?, mapIcon?, mapLifetime?, renderMode?, sortOrder?, enabled?, requiresLocationForMap?, actor, reason}` — лише presentation/
  policy-поля; `code`, `category`, `createsIncident`, `dedupPolicy`, `stateModel` — не редагуються (владник — seed/ADR-0008/0010), 400 без actor/reason, 404,
  422 при невалідному кольорі (`#rrggbb`)/іконці (перелік з каталогу)/lifetime), `GET /api/admin/event-kinds/{code}/audit`. Аудит — нова таблиця
  `event_kind_audit` (`audit_id, event_kind_id, action (updated|enabled|disabled), actor, reason, at, before jsonb, after jsonb`), міграція `AddEventKindAudit`.
- Seeder: рядок з `presentation.adminOverride = true` (ставить PUT) **не** перезаписується новішим seed (ADR-0008 доповнення: admin-owned presentation);
  `dedupPolicy`/`metadata` — далі з seed.
- Кеші: `ReferenceCache` API (10 хв) і `IndexProvider` worker — без змін (poll); Admin після PUT інвалідує свої `AdminIndexes`.
- UI: `web/src/admin/CatalogPanel.tsx` (секція «Каталог подій», група «Налаштування»): таблиця kinds (code, назва, категорія, колір-свотч + shape, іконка, lifetime,
  visible/enabled, sortOrder), inline-редагування presentation з обов'язковими actor/reason, історія аудиту; source text не задіяний; secrets відсутні.

### D3. Черга ревʼю incidents (§8.7, Admin)

- API: `GET /api/admin/incidents/review?hours=` — incidents з links `relation in (ambiguous)` або `decision_reason.near_candidates`, не suppressed, за
  `last_reported_at desc`, з кандидатами (ids/score) для швидкого merge; `GET /api/admin/incidents/{id}/merge-preview?targetId=` — наслідки merge без запису:
  links, які перенесуться, `source_count`/локація/state після, 409-причини (різні kinds/generation, retracted).
- UI: `web/src/admin/IncidentsPanel.tsx` (секція «Інциденти», група «Дані»): список ревʼю + фільтр state/kind, картка incident (links з relation/score/текстом
  raw — **escaped** React-текстом, без dangerouslySetInnerHTML), дії `merge` (preview → підтвердження), `split` (вибір observations), `resolve/retract/confirm/
  suppress/unsuppress` з обов'язковими actor/reason; результати команд — з `IncidentEndpoints` (P10).

### D4. Map quality (публічна мапа, файли поза паралельним diff)

- `geojson.ts buildEventLayer(events, now, filters, colorOf?)`: колір legacy-маркера з catalog adapter за `legacyEventType` (fallback — старі константи) —
  усуває дублювання enum/colors; `MapView` передає `catalog.colorOfLegacy`.
- `EventPopup`: permalink джерела (`rawMessage.url`) + `locationKind` precision-підпис (як у `IncidentPopup`).
- `IncidentLegend`: позиція `bottom-right` над attribution на desktop, `bottom-left` над ScaleControl не перекриваючи (відступ), mobile — згорнута за
  замовчуванням, `max-height` 40vh; підпис форми українською (`burst → «зірка»` тощо); `aria-expanded`.
- `useIncidentLayer`: `window.__incidents` у DEV/E2E.

### D5. Тести

Playwright E01–E08 (desktop + mobile для E06/E01), vitest: catalog shape labels, `buildEventLayer` з `colorOf`; Admin.Tests: `CatalogEndpoints` validation
(400/404/422), merge-preview mapping; Integration.Tests: `AddEventKindAudit` + seeder override (PUT → seed з новішим policyVersion не перезаписує), review
query; регресія .NET + vitest + tsc + `vite build` (scratch outDir).

### D6. Документація

ADR-0008 (admin-owned presentation, `event_kind_audit`), ADR-0011 (легенда/mobile, E2E harness як UI-контракт), `docs/README.md` (admin API catalog/review,
`npm run e2e`), plan §16.1 (точна команда Playwright — §16.1 вимагає), §17; evidence `P12-ui-evidence.md` (+скриншоти E2E у `docs/evidence/message-platform/
e2e/`), handoff, manifest.

## Порядок

1. Playwright config + fixtures + DEV-хук; E01/E04/E05/E07 (шар), E02/E06 (попап/легенда), E03/E08 (store). 2. D4. 3. D2 (міграція, endpoints, seeder, UI, тести).
4. D3. 5. Регресія, docs, review, handoff, commit (явний перелік файлів), push.

## Ризики / межі

- E2E без .NET: UI-контракт над fixtures; live smoke з реальним API — окремо (P16 release gate). Скриншоти — лише DOM-елементи (попап/легенда) без тайлів.
- Паралельний diff у `web/`: `FilterPanel`-інтеграція фільтрів incidents і `App.tsx` — не в P12 (U-задачі); при конфлікті в спільних файлах — лише свої hunks.
- axe-core додає devDependency; якщо мережа/пакет недоступні — E06 без axe, лише ручні a11y-асерти (зафіксувати в evidence).

## Незалежне review плану — p12_review: approve after fixes → внесено

| # | Finding | Рішення в плані |
|---|---|---|
| B1 | Seeder `Apply` перезаписує все, зокрема `presentation` jsonb, де планувався прапорець override; «не перезаписується» читалось як skip усього рядка (drift `dedup_policy` vs `policy_version`) | Розділ полів: **admin-owned presentation** = `name_uk, map_visible, map_color, map_icon, map_lifetime, render_mode, sort_order, requires_location_for_map, enabled`; **завжди з seed** = `category, default_severity, state_model, creates_incident, dedup_policy, metadata, presentation(json), policy_version`. Override — окрема колонка `presentation_overridden_at timestamptz NULL` (та сама міграція, що й `event_kind_audit`); seed з новішим policyVersion оновлює лише seed-owned поля для overridden рядків. `SeedAsync(db, file, ct)` overload для тесту: PUT → seed v+1 → `map_color` збережено, `dedup_policy`/`policy_version` оновлено |
| B2 | Fixture `NOW` фіксований → через день усе prune'иться | `NOW = new Date()` на старті прогону (ages — хвилини до старту); скриншоти — лише DOM без відносного часу (маска `time`); команда відтворювана будь-якого дня |
| B3 | `incidentsApi.window` maxPages 10 → E07 доводив 5k; burst через `applyMany` оминає батчер | `maxPages` 20 (7 діб × cap); E07: `byId` = 10 000, час до даних ≤ 5 с, кластери; burst — через `createPushBatcher` (DEV-хук `window.__incidentsPush`), лічильник `subscribe`-оновлень store = 1 |
| B4 | `window.__map` лише в `MapView` | Хуки `__map`/`__incidents` виставляє `useIncidentLayer` (обидві мапи) |
| N1 | Немокнуті `/api/**` → proxy ECONNREFUSED; U-маршрути дрейфують | catch-all `**/api/**` → 404 із записом URL (fail-fast у assert); history — через `window.__store.setMode('history', at)`; hash-маршрути — лише smoke |
| N2 | E03 лічба | рівно 2 recorded-запити у burst, 3 після `HISTORY_RELOAD_MS`; `/api/snapshot?at=` не рахується |
| N3 | Merge-preview не має дублювати `MergeAsync` | pure `MergePlan(source, target, links)` у `IncidentStateWriter`, спільний для `MergeAsync` і preview (Admin.Tests unit); preview повертає `revision` target'а, UI відмовляє при зміні |
| N4 | Review-запит без індексу | `hours` clamp 1..720, `limit` ≤ 500, через keyset-індекс incidents + PK links; `EXPLAIN (ANALYZE, BUFFERS)` у evidence; стан кандидатів (retracted/merged) резолвиться у відповіді |
| N5 | Admin UI без E2E; href | A01 (catalog edit: PUT з actor/reason, 422) і A02 (review → preview → merge) через `vite.admin.config.ts` webServer; href лише `http(s):`; RBAC = `X-Admin-Token` (існуючий), `actor` — вільний текст, ролі — P13 |
| N6 | Картка без raw-тексту | `GET /api/admin/incidents/{id}` +`segment_text`/`raw text`/`url` per link (admin-only) |
| N7 | Легенда: позиція | bottom-left над ScaleControl, desktop `max-height` + scroll, mobile згорнута; E06 — жоден `pointer-events-auto` не перекриває scale/attribution |
| N8 | `web/e2e` поза tsc; snapshots; PBF | `tsconfig.e2e.json` (у `tsconfig.json` references); §16.1: `cd web; npx playwright install chromium; npx playwright test [--project=desktop|mobile]`; snapshots win32-only (без CI), `--update-snapshots` лише свідомо; PBF — OpenFreeMap Noto Sans (OFL), примітка у fixtures |
| N9 | `enabled=false` для legacy-mapped kind зупиняє правила P08; `mapIcon` без словника | 409 для kinds з `metadata.legacyEventType` без `force: true`; `GET /api/admin/event-kinds` повертає `iconVocabulary` (сервер — джерело), PUT валідує за ним |
| N10 | Формат/межі PUT; `AdminIndexes` без invalidate | `mapLifetime` `hh:mm:ss`/`d.hh:mm:ss`, 1 хв..7 діб; `mapColor` `#rrggbb`; статичний `CatalogEndpoints.Validate(req) → IResult?` (Admin.Tests); `AdminIndexes.Invalidate()` |
| Q1 | Лаг кешів 10 хв | підпис у панелі («на мапі — до 10 хв, після оновлення сторінки»), ADR-0008/README; NOTIFY catalog.changed — не в P12 |
| Q2 | Що поза P12 | **Поза P12**: rules UI, RBAC-ролі, права raw/LLM-audit → P13; baseline kind/source/month → P15; release gate/golden corpus/API budget/track-alert push у projection → P16; canvas keyboard nav → після P13. **У P12**: incidents без локації — секція «Без локації» в `IncidentLegend` (список, попап без якоря) — §8.5 «у стрічці/admin» |
| Q3 | `FeedPanel` кольори legacy | лишається (поза P12, U-задачі) — зафіксувати в evidence |
| Q4 | Concurrency PUT | `before` читається в тій самій tx, що й update; `If-Match` — не в P12 (документовано) |
| Q5 | Нова таблиця audit | так, `event_kind_audit` (`actor 128`, `reason 1000`, `before/after` jsonb, індекс `(event_kind_id, at DESC)`) |
| B3 (уточнення) | E07 «час до даних ≤ 5 с» | у harness час включає завантаження сторінки dev-сервером + 20 сторінок + перший draw → межа 10 с (факт ~7 с); 5 с — лише для даних без page load |
