# Матриця покриття активної документації

## Як читати матрицю

Рядок пов'язує реалізований компонент, алгоритм, процес або взаємодію з
наративною сторінкою, редагованою Draw.io-схемою, її похідним PNG та живим
source of truth. Посилання на код, Compose і скрипти навмисно відносні, щоб
працювати з checkout репозиторію. `Реалізовано` означає лише статичну звірку
цього checkout; воно не означає успішний runtime smoke або доступність
зовнішньої системи.

Обмеження та відкладені/майбутні можливості не переносяться з архівних планів
до цього статусу: вони явно лишаються у розділах «Відомі межі» відповідної
сторінки. Архівні матеріали в `docs/evidence/` — докази або історичний
контекст, а не активний контракт.

| Тип і покриття | Документ | Draw.io | PNG | Source of truth | Стан |
| --- | --- | --- | --- | --- | --- |
| Взаємодія: загальний шлях source → pipeline → read-side | [README](../README.md) | — | — | [Compose](../deploy/docker-compose.yml), [Worker roles](../src/Puluj.Worker/Program.cs) | Реалізовано; межі в README |
| Компонент: Compose topology, мережі, томи й доступ | [D02](deployment-operations.md) | — | — | [Compose](../deploy/docker-compose.yml) | Реалізовано; не є production-hardening recipe |
| Процес: deliberate first install та deploy | [D02](deployment-operations.md) | — | — | [deploy script](../scripts/deploy.ps1) | Реалізовано; Docker smoke не запускався |
| Взаємодія: config files/env/DB settings | [D02](deployment-operations.md) | [Draw.io](diagrams/runtime-configuration.drawio) | [PNG](diagrams/runtime-configuration.png) | [DB provider](../src/Puluj.Infrastructure/Settings/DbConfigurationProvider.cs), [settings store](../src/Puluj.Infrastructure/Settings/SettingsStore.cs) | Реалізовано; secrets лишаються поза docs |
| Компонент: ownership основної та analytics schema | [D03](domain-data-model.md) | — | — | [PulujDbContext](../src/Puluj.Infrastructure/Persistence/PulujDbContext.cs), [AnalyticsDbContext](../src/Puluj.Analytics/Persistence/AnalyticsDbContext.cs) | Реалізовано; межі в D03 |
| Взаємодія: public object до raw provenance | [D03](domain-data-model.md) | — | — | [catalogue queries](../src/Puluj.Api/Services/PublicCatalogQueries.cs), [message queries](../src/Puluj.Api/Services/PublicMessageQueries.cs) | Реалізовано; не доказ фактичної істини |
| Процес: Gazetteer, taxonomy та seed | [D03](domain-data-model.md) | [Draw.io](diagrams/reference-data-flow.drawio) | [PNG](diagrams/reference-data-flow.png) | [Gazetteer seeder](../src/Puluj.Infrastructure/Seeding/GazetteerSeeder.cs), [migrate role](../src/Puluj.Worker/Program.cs) | Реалізовано; seed залежить від середовища |
| Компонент: collector-to-ingestion path | [D04](collectors-ingestion.md) | — | — | [collector ingress](../src/Puluj.Collectors/CollectorIngress.cs), [raw ingestor](../src/Puluj.Infrastructure/Ingestion/RawMessageIngestor.cs) | Реалізовано; межі в D04 |
| Процес: collector start, retry, backoff, Telegram history fairness і recovery | [D04](collectors-ingestion.md) | [Draw.io](diagrams/collector-lifecycle.drawio) | [PNG](diagrams/collector-lifecycle.png) | [supervisor](../src/Puluj.Collectors/CollectorSupervisor.cs), [Telegram scheduler](../src/Puluj.Collectors/Telegram/TelegramBackfillScheduler.cs) | Реалізовано; external source behavior не гарантується |
| Межа: original data, session та secrets | [D04](collectors-ingestion.md) | [Draw.io](diagrams/collector-data-boundaries.drawio) | [PNG](diagrams/collector-data-boundaries.png) | [Telegram collector](../src/Puluj.Collectors/Telegram/TelegramCollector.cs), [Compose volume](../deploy/docker-compose.yml) | Реалізовано; секрети не документуються |
| Процес: raw message до derived result | [D04](collectors-ingestion.md) | — | — | [processing loop](../src/Puluj.Processing/Pipeline/ProcessingLoop.cs), [raw processor](../src/Puluj.Processing/Pipeline/RawMessageProcessor.cs) | Один processor з внутрішнім bounded concurrency |
| Алгоритм: rules, parser, LLM і target materialization | [D04](collectors-ingestion.md) | [Draw.io](diagrams/rules-llm-decision.drawio) | [PNG](diagrams/rules-llm-decision.png) | [rule parser](../src/Puluj.Processing/Parsing/RuleParser.cs), [target builder](../src/Puluj.Processing/Pipeline/TargetBuilder.cs) | LLM не є джерелом істини |
| Алгоритм: candidate scoring та ambiguity кореляції | [D06](correlation-tracks-incidents.md) | [Draw.io](diagrams/correlation-decision.drawio) | [PNG](diagrams/correlation-decision.png) | [correlator](../src/Puluj.Processing/Correlation/Correlator.cs), [sink](../src/Puluj.Processing/Correlation/CorrelationSink.cs) | Реалізовано; score не є ймовірністю |
| Процес: track revisions і watchdog closure | [D06](correlation-tracks-incidents.md) | [Draw.io](diagrams/track-lifecycle-revisions.drawio) | [PNG](diagrams/track-lifecycle-revisions.png) | [track writer](../src/Puluj.Processing/Writers/TrackWriterHandler.cs), [watchdog](../src/Puluj.Processing/Correlation/TrackWatchdog.cs) | Реалізовано; closure є timeout policy |
| Процес: incident review, merge/split і transitions | [D06](correlation-tracks-incidents.md) | [Draw.io](diagrams/incident-lifecycle-review.drawio) | [PNG](diagrams/incident-lifecycle-review.png) | [state writer](../src/Puluj.Processing/Incidents/IncidentStateWriter.cs), [policy](../src/Puluj.Processing/Incidents/IncidentPolicy.cs) | Реалізовано; evidence не стирається |
| Взаємодія: provenance у read-side | [D06](correlation-tracks-incidents.md) | [Draw.io](diagrams/provenance-read-side.drawio) | [PNG](diagrams/provenance-read-side.png) | [incident queries](../src/Puluj.Api/Services/IncidentQueries.cs), [catalogue queries](../src/Puluj.Api/Services/PublicCatalogQueries.cs) | Реалізовано; location може бути неточною/відсутньою |
| Компонент: public REST read-side | [D07](public-api-read-side.md) | [Draw.io](diagrams/read-side-interaction.drawio) | [PNG](diagrams/read-side-interaction.png) | [route map](../src/Puluj.Api/Endpoints/EndpointRouteBuilderExtensions.cs), [API program](../src/Puluj.Api/Program.cs) | Реалізовано; лише read-side |
| Взаємодія: SignalR, notifications і history reload | [D07](public-api-read-side.md) | [Draw.io](diagrams/live-update-history-replay.drawio) | [PNG](diagrams/live-update-history-replay.png) | [notify bridge](../src/Puluj.Api/Services/NotifyBridge.cs), [map hub](../src/Puluj.Api/Hubs/MapHub.cs) | Реалізовано; best-effort notification |
| Взаємодія: public catalogue, evidence і cursor | [D07](public-api-read-side.md) | [Draw.io](diagrams/public-catalogue-evidence.drawio) | [PNG](diagrams/public-catalogue-evidence.png) | [catalogue queries](../src/Puluj.Api/Services/PublicCatalogQueries.cs), [message queries](../src/Puluj.Api/Services/PublicMessageQueries.cs) | Реалізовано; cursor opaque |
| Компонент: admin authority boundaries | [D08](admin-operations.md) | [Draw.io](diagrams/admin-authority-boundaries.drawio) | [PNG](diagrams/admin-authority-boundaries.png) | [admin program](../src/Puluj.Admin/Program.cs), [authorization](../src/Puluj.Admin/Endpoints/AdminEndpoints.cs) | Реалізовано; не RBAC |
| Процес: ruleset draft, validation, shadow, publish | [D08](admin-operations.md) | [Draw.io](diagrams/ruleset-change-lifecycle.drawio) | [PNG](diagrams/ruleset-change-lifecycle.png) | [ruleset endpoints](../src/Puluj.Admin/Endpoints/RulesetEndpoints.cs), [service](../src/Puluj.Infrastructure/Rules/RulesetService.cs) | Реалізовано; state conflicts можливі |
| Взаємодія: operator actions для incidents | [D08](admin-operations.md) | [Draw.io](diagrams/incident-lifecycle-review.drawio) | [PNG](diagrams/incident-lifecycle-review.png) | [incident endpoints](../src/Puluj.Admin/Endpoints/IncidentEndpoints.cs) | actor/reason requirements у code |
| Компонент: analytics inputs, outputs і schema boundary | [D09](analytics-service.md) | [Draw.io](diagrams/analytics-data-flow-schema-boundary.drawio) | [PNG](diagrams/analytics-data-flow-schema-boundary.png) | [analytics context](../src/Puluj.Analytics/Persistence/AnalyticsDbContext.cs), [worker program](../src/Puluj.Analytics.Worker/Program.cs) | Реалізовано; derived data |
| Процес: lifecycle projection, backfill і reconciliation | [D09](analytics-service.md) | [Draw.io](diagrams/message-lifecycle-projection-reconciliation.drawio) | [PNG](diagrams/message-lifecycle-projection-reconciliation.png) | [backfill](../src/Puluj.Analytics/Lifecycle/LifecycleBackfill.cs), [reconciliation](../src/Puluj.Analytics/Lifecycle/LifecycleReconciliation.cs) | Реалізовано; missing facts stay unknown |
| Межа: analytics report provenance та freshness | [D09](analytics-service.md) | [Draw.io](diagrams/analytics-report-provenance-freshness.drawio) | [PNG](diagrams/analytics-report-provenance-freshness.png) | [report service](../src/Puluj.Analytics/Reporting/AnalyticsReportService.cs), [lifecycle report](../src/Puluj.Analytics/Lifecycle/LifecycleReportService.cs) | Реалізовано; no real-time completeness guarantee |
| Компонент: Vite entrypoints, builds і static hosting | [D10](web-clients.md) | [Draw.io](diagrams/frontend-entrypoints-build-deploy.drawio) | [PNG](diagrams/frontend-entrypoints-build-deploy.png) | [Vite config](../web/vite.config.ts), [map entry](../web/src/main.tsx), [admin entry](../web/src/admin/main.tsx) | Реалізовано; builds не запускалися тут |
| Взаємодія: public map fetch, filters, history і live updates | [D10](web-clients.md) | [Draw.io](diagrams/public-map-user-flow.drawio) | [PNG](diagrams/public-map-user-flow.png) | [map filters](../src/Puluj.Api/Services/MapFilter.cs), [map routes](../src/Puluj.Api/Endpoints/EndpointRouteBuilderExtensions.cs) | Реалізовано; realtime/history різні за повнотою |
| Межа: browser-only home location та ETA | [D10](web-clients.md) | [Draw.io](diagrams/privacy-boundary-user-location-eta.drawio) | [PNG](diagrams/privacy-boundary-user-location-eta.png) | [ETA hook](../web/src/eta/useEta.ts), [calculation](../web/src/eta/computeEta.ts) | Реалізовано; ETA є оцінкою |
| Взаємодія: Admin SPA та protected API | [D10](web-clients.md) | [Draw.io](diagrams/admin-ui-api-data-flow.drawio) | [PNG](diagrams/admin-ui-api-data-flow.png) | [Admin app](../web/src/admin/AdminApp.tsx), [admin endpoints](../src/Puluj.Admin/Endpoints/AdminEndpoints.cs) | Реалізовано; UI не замінює server guardrails |

## Перевірка матриці

`pwsh -File scripts/docs/verify-docs.ps1` перевіряє локальні посилання активної
документації, а також взаємну наявність Draw.io/PNG і посилання на кожен PNG.
Він не виконує Docker, API, browser або зовнішні інтеграції. Для повторного
експорту зберігайте редагований `.drawio` разом з PNG за
[інструкцією експорту](diagrams/export.md).
