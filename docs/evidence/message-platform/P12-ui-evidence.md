# P12 — map quality, main/Kyiv parity, catalog UI, incident popup/filters, E2E harness: evidence

Джерела: `web/e2e/*.e2e.ts` (Playwright 1.58, Chromium headless shell 145; API замокано з `web/e2e/fixtures/api.ts`, dev-сервери Vite :5183/:5184 без .NET/БД),
`web/src/{catalog,store,map}/*.test.ts` (vitest), `Puluj.Admin.Tests/CatalogEndpointsTests.cs`, `Puluj.Integration.Tests/CatalogAdminTests.cs` (Testcontainers PostGIS).
TRX — `test-results/p12-*.trx`; Playwright-звіт — `web/e2e-report` (не в git), знімки-еталони — `web/e2e/*-snapshots/*-win32.png`.

## Gate issue #13: E2E/visual — точність геометрії, provenance, history, mobile/accessibility, 1k/10k workload

| Gate | Сценарій | Підсумок |
|---|---|---|
| **Точність геометрії** (§8.5) | E01 (desktop + mobile): 7 fixture-incidents → `incident-points` = {1 point, 2 city, 3 district, 4 region, 7 Kyiv district}; `incident-areas` = {3 коло 65 вершин (район без полігону), 4 полігон області, 7 полігон Печерського}; 5 без локації і 6 (catalog `mapVisible=false`) — не на мапі; попап region: «область — приблизна область (±128 км)», city: «населений пункт (маркер, не адреса) (±15 км)» | ✅ |
| **Provenance** | E02: попап — `aria-label` «kind: місце», state словом, обидві шкали часу (`Подія`/`Останнє`), `2 · 2 повідомл.`, `incident-1/p2`, текст raw, permalink `href` = `rawMessage.url` з `rel=noreferrer`, ревізії `#1 created`/`#2 updated` з `operator` (без `@`), Esc закриває; DOM-знімок `incident-popup-desktop-win32.png` (часи замасковано); legacy `TargetCancelled` лишається маркером з попапом і permalink `оригінал`; вибух-маркер прихований за своїм incident (один попап) | ✅ |
| **History** (§8.5) | E03: `setMode('history', at)` → один `/api/incidents?…mode=recorded&asOf=T&to=T`; 10 replay-тиків в одному page turn → **рівно 2** recorded-запити (leading + trailing throttle 3 с); назад у live → `effective` | ✅ |
| **Main/Kyiv parity** (§8.6) | E04: `#/map/live?preset=kyiv` (hash-навігація; тест чекає **новий** об'єкт мапи на `__map` з даними) — ті самі `incident-points` properties (без opacity) і геометрії `incident-areas`, Печерський — полігон з `/api/places/regions` (не коло); legacy `events` на обох = лише `TargetCancelled` | ✅ |
| **Filters / catalog** | E05: легенда з каталогу в порядку `sortOrder` (ППО, вибух, влучання, пожежа), outage (`mapVisible=false`) не пропонується; форма словом («зірка»), лічильник 3; user toggle → шар без kind і назад; `filters.events` off → обидва шари порожні, легенда зникає; on → повернення, вибух лишається прихованим за incident | ✅ |
| **Mobile / accessibility** | E06 (desktop + mobile Pixel 7): легенда згорнута (`aria-expanded=false`), `aria-label` з лічильниками, не перекриває `.maplibregl-ctrl-scale`/`.maplibregl-ctrl-attrib` (згорнута **і** розкрита); Enter на summary → розкрито; кнопка incident → фокус → Enter → `role=dialog` з `aria-label` → Esc закриває; «Без локації» — кнопки, попап з якорем у центрі мапи; mobile висота ≤ 40vh; DOM-знімки `legend-desktop`/`legend-mobile`; **axe** (`@axe-core/playwright` 4.10) на легенді + попапі — 0 serious/critical (виправлено `color-contrast` `.text-slate-500` → `600/300`) | ✅ |
| **1k/10k workload** (§8.6) | E07: 10 000 incidents у 20 сторінках по 500 → store 10 000, `incident-points` 10 000 features, час до даних ≤ 10 с включно з page load (факт ~7 с; план B3 «5 с» — без page load), кластери на zoom країни (`incident-clusters` рендерить); 1 000 push через реальний батчер (`__incidentsPush`) → **1** оновлення store, revision 11, < 1.5 с | ✅ |
| **Reconnect / resync** | E08: `resync(0)` → рівно один `/api/incidents?from=`; два reload підряд → обидва запити, `byId` 7, `loading=false` (sequence guard) | ✅ |
| **Catalog UI** (§8.7) | A01 (admin :5184, замокано): «Зберегти» disabled без actor/reason; 422 → `mapColor must be #rrggbb` у `role=status`, нічого не збережено; PUT несе лише змінені поля + actor/reason; повідомлення про лаг «до 10 хв»; `enabled=false` для legacy-kind → 409 → confirm → повтор з `force=true`; аудит `mapColor: #ef6c00 → #123456` | ✅ |
| **Review queue** (§8.7) | A02: черга з прапорцями `ambiguous 1/2`, retracted-кандидат закреслений (не mergeable), фільтри state/kind, `truncated`-підказка; картка: markup `<b>…<script>` показаний **текстом**, `<script>` відсутній у DOM, `javascript:` URL не стає посиланням; команди disabled без actor/reason; preview злиття: «перенесеться 1 observation», «джерела 2», «не змінюється»; confirm при зміненій `targetRevision` → відмова «змінився після preview», `POST /merge` не викликано; свіжий preview → confirm → `POST /merge {sourceId 3, targetId 1, actor, reason}` | ✅ |
| Backend | `CatalogEndpointsTests` (17, +enabled-only не робить admin-owned, словник = seed, exact lifetime formats): 400/422 валідація (колір, іконка зі словника, lifetime 1 хв..7 діб, render mode, name, sortOrder), legacy mapping, snapshot лише admin-owned полів; `PlanMerge` = правила команди (kind/generation/retracted/self, `source_count`, min accuracy, max confidence, state target незмінний, `targetRevision`). `CatalogAdminTests` (2, PostGIS): PUT-семантика + seed `policyVersion+7` → `map_color/name/lifetime/visible` збережено, `dedup_policy`/`policy_version` оновлено, невідредагований kind — повністю з файлу, аудит before/after; review-предикат (`ambiguous` ∪ `jsonb ? near_candidates`) → {2, 3}, `PlanMerge` дозволяє | ✅ |

## Що змінилось на мапі (D4)

- Кольори legacy event-маркерів — з catalog adapter (`colorOfLegacy` за `legacyEventType`; старі константи — лише fallback; vitest).
- `EventPopup` — permalink `оригінал` (лише `http(s)`); `IncidentPopup` — href теж лише `http(s)`.
- `IncidentLegend`: форма словом, секція «Без локації», `aria-expanded`, `bottom-10` над ScaleControl, `max-height 40vh`, mobile-ширина; контраст тексту.
- Глиф: 48 px, halo 5, burst inner 0.66; hit-box кліку ±10 px; клік чекає `idle` мапи (symbol placement після jump — асинхронний).

## Запуски

| Команда | Результат | Примітка |
|---|---|---|
| `npx playwright test` (run1) | 0 / 15 | catch-all `**/api/**` перехопив `/src/api/*.ts` Vite → порожня сторінка → предикат `pathname.startsWith('/api/')` |
| run2 | 3 / 12 | паралельні workers (2 WebGL-мапи на один dev-сервер) → таймаути → `workers: 1`; знімки-еталони записано |
| run3 | 12 / 3 | E03 — тики в harness ~2 с (re-render мапи) → rate-based assert; E07 — fixture 48 год перевищувала lifetime → 90 хв; знімок legend-desktop |
| run4 | **15 / 0** (2.9 хв) | E01–E08 desktop + E01/E06 mobile |
| `npx playwright test e2e/A01-admin.e2e.ts` | **2 / 0** | A01/A02 з першого разу |
| run5 (усі, після D4/legacy-fixture, stale dev-сервер 504 «Outdated Optimize Dep» убито) | 17 / 0 (4.6 хв) | до review |
| run6 (після review: E03 рівно 2, E04 новий `__map`, A02 happy-path merge, E06 розкрита легенда + попап без локації, E07 ≤ 10 с) | 16 / 1 → **17 / 0** | A02: статус «виконано» затирався `open()` після merge → повідомлення після reopen |
| vitest P12-suites | **20 / 20** | catalog 5, store 7, layer 8 (+font, +legacy colours) |
| `tsc --noEmit -p tsconfig.app.json` / `-p tsconfig.e2e.json` | 0 / 0 | e2e специ під tsc (N8) |
| `p12-Puluj.Admin.Tests.trx` → `-run2` | 80 / 0 → **81 / 0** | +17 |
| `p12-catalog-run1.trx` | **2 / 0** | Integration `CatalogAdminTests` |
| `p12-Puluj.Messaging.Contracts.Tests.trx` / Processing / Api / Analytics | 61 / 0, 134 / 0, 34 / 0, 33 / 0 | регресія |
| `p12-Puluj.Messaging.Tests.trx` | **86 / 0 / 1** | регресія |
| `p12-Puluj.Integration.Tests.trx` → `-run2` | 41 / 0 / 1 → **41 / 0 / 1** | +2; міграція `AddEventKindAudit`; review-предикат — спільний `ReviewPredicate` endpoint'а. `EXPLAIN (ANALYZE, BUFFERS)` review-запиту **не знімався** (вікно ≤ 720 год, `Take(take+1)` по групах; замір — після появи даних) |

## Що не покрито (свідомо)

- E2E — UI-контракт над fixtures (без .NET/БД); live smoke з реальним API/SignalR — P16 release gate. Знімки — win32-only (без CI).
- `FeedPanel` кольори/підписи legacy — лишаються (U-задачі, файл поза P12); інтеграція фільтрів incidents у `FilterPanel` — U-задачі.
- Canvas keyboard-навігація — після P13; RBAC-ролі/права raw/LLM-audit, rules UI — P13; baseline kind/source/month — P15; golden corpus/API budget gate — P16.
- `If-Match` для конкурентних PUT каталогу і `expectedRevision` на merge — не реалізовано (ADR-0008/0010, P13); `NOTIFY catalog.changed`, «скинути до seed» — P13.
- Кольори legacy-маркерів на мапі тепер з каталогу: `ExplosionReport` `#dc2626`→`#fb8c00`, `AirDefenseActivity` `#2563eb`→`#1e88e5`, `TargetCancelled` `#16a34a`→`#9e9e9e` тощо; `FeedPanel` тримає старі константи (U-задачі) — feed і мапа тимчасово розходяться.
- Знімки E2E — `web/e2e/*-snapshots` (не `docs/evidence/.../e2e/`, як планувалось: Playwright тримає їх поруч зі spec); `reuseExistingServer` — stale dev-сервер треба вбити (`netstat :5183/:5184`).
- Vitest для `shapeLabel` окремо немає — покрито E05 («зірка») і A01.
- `npm run build` у `wwwroot` не запускався (паралельна робота іншого агента над `web/`); bundle зібрано у scratch outDir (P11) — у P12 повторено `tsc` обох конфігів.
