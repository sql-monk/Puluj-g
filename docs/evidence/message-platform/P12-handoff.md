# P12 — map quality blockers, main/Kyiv parity, catalog UI, incident popup/filters, E2E harness

Task: [P12 / issue #13](https://github.com/sql-monk/Puluj-g/issues/13).
Status: **done** — реалізація, тести (Playwright E2E desktop+mobile, vitest, Admin/Integration), документація; незалежне review результату
(`p12_review`): approve after fixes (без blocking) → N1–N13, Q1–Q6 виправлено/задокументовано → повторний прогін зелений. Rollout не виконувався (міграція `AddEventKindAudit` additive; нові admin endpoints; поведінка мапи — presentation).
Owner: Claude Code (Opus 5), агент `p12`. Reviewer: план — `p12_review` (approve after fixes; B1 field split/`presentation_overridden_at`, B2 fixture
clock, B3 `maxPages`/batcher, B4 `__map` в обох мапах, N1–N10, Q1–Q5 — внесено до старту, `P12-plan.md`); результат — `p12_review` (таблиця нижче).
Base commit: `4917327` (P11); результуючий commit — цей handoff комітиться разом із кодом (`P12-build-manifest.json`; паралельна робота public UI у `web/`
до коміту не входить).

## Результат для споживача

- **E2E/visual harness** (`web/e2e`, Playwright 1.58 + Chromium, `npm run e2e`): 17 сценаріїв над повністю замоканим API (без .NET/БД) — precision
  геометрії (E01), provenance popup (E02), history mode + throttle (E03), main/Kyiv parity (E04), catalog legend/filters (E05), mobile/a11y з axe (E06),
  10k incidents + 1k push burst (E07), resync (E08), admin catalog editor (A01) і review queue (A02). Знімки DOM-елементів (popup/легенда), `tsconfig.e2e.json`.
- **Catalog UI** (§8.7): `GET/PUT /api/admin/event-kinds`, `GET /{code}/audit`; presentation admin-owned після першої зміни (`presentation_overridden_at`),
  seed оновлює лише policy-поля; аудит `event_kind_audit` (actor/reason/before/after у тій самій tx); 409 для `enabled=false` legacy-kind без `force`;
  словник іконок — сервер; панель «Каталог подій» в Admin (лаг кешів 10 хв сказано в UI).
- **Черга ревʼю incidents** (§8.7): `GET /api/admin/incidents/review` (ambiguous/near з поточним станом кандидатів і `mergeable`), `GET /{id}/merge-preview`
  (pure `IncidentStateWriter.PlanMerge` — ті самі правила, що `MergeAsync`; `targetRevision` для stale-guard), `GET /{id}` з raw text/url per link
  (admin-only); панель «Інциденти»: merge через preview + підтвердження, split, resolve/retract/confirm/suppress з actor/reason; текст — лише текст, href — `http(s)`.
- **Map quality**: кольори legacy-маркерів з каталогу (`colorOfLegacy`), permalink у `EventPopup`, легенда: форма словом, «Без локації», `aria-expanded`,
  над ScaleControl, mobile 40vh; контраст; глиф/hit-box.

Docs: ADR-0008 (admin-owned presentation, audit, open items), ADR-0011 (P12-секція, open items), `docs/README.md` (admin endpoints), plan §16.1
(команда Playwright) / §17; evidence `P12-ui-evidence.md`, handoff, manifest.

## Тести (команда → exit code → результат → середовище)

Середовище: Windows 11, Node 22 / Playwright 1.58.2 / Chromium headless shell 145 / axe-core 4.10, .NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5`,
`rabbitmq:4.3-management`. .NET — під `pwsh -File scripts/with-lock.ps1`. TRX — `test-results/p12-*.trx`.

| Команда | Exit | Passed / Failed / Skipped |
|---|---|---|
| `cd web; npx playwright test` run1 → run6 | 1 → 0 | 0/15 → 3/12 → 12/3 → 15/0 → 17/0 → **17/0** (desktop 13 + mobile 4; run6 після review) |
| `cd web; npx vitest run src/catalog src/store/useIncidentStore.test.ts src/map/incidentLayer.test.ts` | 0 | **20/0** |
| `cd web; npx tsc --noEmit -p tsconfig.app.json`; `-p tsconfig.e2e.json` | 0; 0 | — |
| `dotnet test tests/Puluj.Admin.Tests` → run2 | 0 | 80/0 → **81/0** (+17) |
| `dotnet test tests/Puluj.Integration.Tests --filter CatalogAdminTests` | 0 | **2/0** |
| `dotnet test tests/Puluj.Integration.Tests` → run2 | 0 | **41/0/1** |
| `dotnet test tests/Puluj.Messaging.Tests` | 0 | **86/0/1** |
| `dotnet test tests/Puluj.Messaging.Contracts.Tests` / `Processing` / `Api` / `Analytics` | 0 | 61, 134, 34, 33 |
| `dotnet build Puluj.sln` | 0 | 0 warnings |
| `npm test` (весь web) | 1 | 1 падіння `src/public/query.test.ts` — паралельна задача public UI (не P12) |

## Evidence

[`P12-ui-evidence.md`](P12-ui-evidence.md); знімки `web/e2e/*-snapshots/*-win32.png`.

## Review результату → виправлення → повторна перевірка

| # | Finding | Виправлення | Перевірка |
|---|---|---|---|
| N1 | E03 — rate-based межа замість «рівно 2» | 10 тиків в одному `page.evaluate` → `during === 2` | E03 |
| N2 | E04 — hash-навігація могла лишити старий `__map` | `evaluateHandle` старої мапи → чекаємо новий об'єкт з даними, без sleep | E04 |
| N3 | A02 — лише stale-відмова | mock bump'ає revision один раз → свіжий preview → confirm → `POST /merge` з actor/reason | A02 |
| N4 | Словник іконок без `air-defence`/`alert-off`; `ICON_SHAPE` дубльований | `EventKindSeeder.IconVocabulary` — джерело для seed-валідації і PUT; `ICON_SHAPE` експортовано з `catalog.ts`; select показує поточну іконку поза словником | `CatalogEndpointsTests`, tsc |
| N5 | `presentation_overridden_at` ставився і для enabled-only PUT; порожній PUT | stamp лише при presentation-полі; порожній → 400 | unit `Only_a_presentation_field…` |
| N6 | Review: `Take(take*4)` по links міг обрізати incidents без ознаки | групування по incident у SQL, `Take(take+1)` → `truncated` | UI-підказка, README |
| N7 | Предикат черги скопійований у тест; EXPLAIN відсутній | `IncidentEndpoints.ReviewPredicate` спільний; EXPLAIN — задокументовано як не знятий | `CatalogAdminTests` |
| N8 | `document.getElementById` для merge target | React state `mergeTarget` | A02 |
| N9 | Фільтр state/kind і мертвий `list` | client-side фільтри над чергою; `list` видалено | UI |
| N10 | Overlap перевірявся лише згорнутою легендою | assert і для розкритої | E06 |
| N11 | E07 ≤ 15 с vs план 5 с | ≤ 10 с + примітка в decisions таблиці плану | E07 |
| N12 | Здвоєний `<summary>` у `IncidentStateWriter` | summary `MergeAsync` повернуто | build |
| N13 | Дубль імпорту react у `IncidentLegend` | об'єднано | tsc |
| Q1–Q6 | reuseExistingServer/snapshots/DPI; зміна кольорів legacy-маркерів; попап без локації; README `npm run e2e`/vitest shapeLabel/розташування знімків; `TryParseExact`; TOCTOU merge | evidence «не покрито», ADR-0010 (expectedRevision — P13), README, exact формати lifetime; попап без локації — кнопки з якорем у центрі | docs, E06 |

## Відомі обмеження / невиконані перевірки

- E2E — UI-контракт над fixtures; live smoke з реальним API — P16; знімки win32-only; `npm run build` у `wwwroot` не запускався (паралельна робота).
- Поза P12 (задокументовано в плані Q2): `FeedPanel`/`FilterPanel` інтеграція (U-задачі), canvas keyboard-навігація (після P13), rules UI/RBAC-ролі/права
  raw-LLM audit (P13), baseline kind/source/month (P15), golden corpus/API budget gate (P16), `If-Match` для PUT каталогу, `NOTIFY catalog.changed` (P13).
- Project card — токен без scope.

## Rollout / rollback / input ownership

Default deploy: міграція `AddEventKindAudit` (таблиця + nullable колонка), нові admin endpoints; rollback — `Down` (drop table/column), UI-панелі без
даних. Ownership: `web/e2e/*`, `web/src/admin/{CatalogPanel,IncidentsPanel}`, `web/src/api/adminCatalog.ts`, `Puluj.Admin/Endpoints/CatalogEndpoints`,
review/preview у `IncidentEndpoints`, `EventKindSeeder` split, `IncidentStateWriter.PlanMerge`.

## Чекбокси issue #13

- [x] E2E/visual: точність геометрії (E01), provenance (E02), history (E03), mobile/accessibility (E06 + axe), 1k/10k workload (E07); parity (E04), filters (E05), resync (E08), admin (A01/A02)
- [x] Тести на актуальній збірці; PostGIS для catalog/review integration; результати й пропуски зафіксовані
- [x] Незалежне code review — approve after fixes → виправлено → повторна перевірка зелена
- [x] Контракти (admin endpoints), міграція/rollback, конфігурація (Playwright scripts/devDeps), документація (ADR-0008/0011, README, plan) оновлені

## Наступний task

P13 (issue #16) — ops metrics/health, message explorer, scale/pause/drain/retry/DLQ UI, curation UI (§9, §8.7).
