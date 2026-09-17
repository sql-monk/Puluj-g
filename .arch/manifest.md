# Архів документації — D01

Це незмінний знімок документації, що була в репозиторії до початку повного
переписування 2026-09-17. Файли нижче переміщено зі збереженням їхніх
відносних шляхів: початковий шлях `docs/...` тепер є
`.arch/docs/...`. Архів не є джерелом поточних правил і не перевіряється
`scripts/docs/verify-docs.ps1`.

## Перенесений наративний матеріал

| Первинні шляхи | Архівний шлях | Вміст |
| --- | --- | --- |
| `README.md`, `Puluj.md` | `.arch/README.md`, `.arch/Puluj.md` | Коренева документація проєкту |
| `todo_map_viina_layer.md`, `todo_osint_sources.md`, `todo_viina_ingest.md` | `.arch/todo_*.md` | Попередні плани й backlog |
| `contracts/messaging/README.md` | `.arch/contracts/messaging/README.md` | Наративний опис контрактів; самі machine-readable контракти не переміщено |
| `web/README.md` | `.arch/web/README.md` | Попередній опис вебзастосунку |
| `docs/README.md`, `docs/correlation.md`, `docs/event-platform-delivery-plan.md`, `docs/fork-deployment.md`, `docs/public-ui-contract.md` | `.arch/docs/` з тими самими іменами | Загальна, кореляційна, delivery/deployment та UI документація |
| `docs/plan-admin-ops.md`, `docs/plan-analytics-service.md`, `docs/plan-github-issues.md`, `docs/plan-granular-processing-locks.md`, `docs/plan-map-events.md`, `docs/plan-map-ttl-queries.md`, `docs/plan-message-platform.md`, `docs/plan-parallel-processing.md`, `docs/plan-processing-log-review.md`, `docs/plan-public-stats-page.md` | `.arch/docs/` з тими самими іменами | Попередні тематичні плани |
| `docs/adr/README.md`, `docs/adr/ADR-0001-transport.md` … `docs/adr/ADR-0013-message-analytics.md` | `.arch/docs/adr/` | Усі 13 ADR і їхній індекс |
| `docs/diagrams/README.md`, `docs/diagrams/build.py`, `docs/diagrams/export.mjs` | `.arch/docs/diagrams/` | Попередній реєстр і засоби створення/експорту діаграм |
| `docs/diagrams/01-overview`, `02-pipeline`, `03-data-model`, `04-correlation`, `05-realtime-history`, `06-code-classes`, `07-deployment` — кожен у форматах `.drawio` і `.png` | `.arch/docs/diagrams/` | Сім пар редагованих діаграм і PNG-прев’ю |

## Залишено поза архівом

Ці шляхи інвентаризовано, але вони є робочими артефактами, а не старою
наративною документацією. Їх не можна переносити до `.arch/` лише через
сусідство з документацією.

| Шлях | Класифікація | Чому лишається активним |
| --- | --- | --- |
| `contracts/messaging/asyncapi.yaml`, `schemas/**`, `topology.json`, `completion-manifest.json` | Контракти й конфігурація | Їх споживають код і тести |
| `contracts/messaging/fixtures/**` | Contract-test fixtures | Приклади валідних/невалідних подій для перевірок |
| `contracts/messaging/tools/gen-asyncapi.py` | Інструмент генерації | Підтримує активний контракт |
| `docs/evidence/message-platform/**` | Докази виконання і результати тестів | Включає JSON/Markdown handoff, manifests та `.trx` |
| `docs/fixtures/public-ui-contract.fixture.json` | Fixture | Вхідні дані перевірки UI-контракту |
| `data/gazetteer/README.md`, `data/gazetteer/**`, `data/corpus/**`, `data/taxonomy/**`, `data/sources.json` | Seed-дані та інструкція seed | README пояснює отримання даних, а не описує компонент |
| `web/e2e/fixtures/README.md`, `web/e2e/fixtures/**`, `web/e2e/*-snapshots/**` | E2E fixtures і візуальні snapshots | Це тестові вхідні дані та очікувані результати |
| `web/src/assets/**`, `web/public/**`, `src/**/wwwroot/**` | Runtime assets | Ресурси застосунків, не документація |

Нові сторінки створюються тільки поза `.arch/` і за правилами
[активної документації](../docs/README.md).
