# Реєстр діаграм

Кожна активна діаграма має редагований Draw.io source та похідний прозорий
PNG. Попередній набір збережено в [архіві](../../.arch/manifest.md).

| Назва | Охоплення | Сторінка | Редагований файл | PNG |
| --- | --- | --- | --- | --- |
| Межі повноважень admin | authentication, DB і Docker access | [D08](../admin-operations.md) | [Draw.io](admin-authority-boundaries.drawio) | [PNG](admin-authority-boundaries.png) |
| Потік Admin UI та API | екрани, API й дані | [D10](../web-clients.md) | [Draw.io](admin-ui-api-data-flow.drawio) | [PNG](admin-ui-api-data-flow.png) |
| Analytics schema boundary | inputs, outputs і ownership | [D09](../analytics-service.md) | [Draw.io](analytics-data-flow-schema-boundary.drawio) | [PNG](analytics-data-flow-schema-boundary.png) |
| Provenance та freshness звітів | межі аналітичних висновків | [D09](../analytics-service.md) | [Draw.io](analytics-report-provenance-freshness.drawio) | [PNG](analytics-report-provenance-freshness.png) |
| Межі даних колекторів | оригінали, похідні дані й секрети | [D04](../collectors-ingestion.md) | [Draw.io](collector-data-boundaries.drawio) | [PNG](collector-data-boundaries.png) |
| Життєвий цикл колектора | startup, retry, backoff і recovery | [D04](../collectors-ingestion.md) | [Draw.io](collector-lifecycle.drawio) | [PNG](collector-lifecycle.png) |
| Рішення кореляції | кандидати, scoring і неоднозначність | [D06](../correlation-tracks-incidents.md) | [Draw.io](correlation-decision.drawio) | [PNG](correlation-decision.png) |
| Web entrypoints | build і static hosting | [D10](../web-clients.md) | [Draw.io](frontend-entrypoints-build-deploy.drawio) | [PNG](frontend-entrypoints-build-deploy.png) |
| Incident lifecycle | review, merge, split і transitions | [D06](../correlation-tracks-incidents.md) | [Draw.io](incident-lifecycle-review.drawio) | [PNG](incident-lifecycle-review.png) |
| Live update і history | realtime та historical replay | [D07](../public-api-read-side.md) | [Draw.io](live-update-history-replay.drawio) | [PNG](live-update-history-replay.png) |
| Message lifecycle | projection, backfill і reconciliation | [D09](../analytics-service.md) | [Draw.io](message-lifecycle-projection-reconciliation.drawio) | [PNG](message-lifecycle-projection-reconciliation.png) |
| Privacy boundary | user location і client ETA | [D10](../web-clients.md) | [Draw.io](privacy-boundary-user-location-eta.drawio) | [PNG](privacy-boundary-user-location-eta.png) |
| Read-side provenance | raw message до публічного record | [D06](../correlation-tracks-incidents.md) | [Draw.io](provenance-read-side.drawio) | [PNG](provenance-read-side.png) |
| Public catalogue | navigation та evidence retrieval | [D07](../public-api-read-side.md) | [Draw.io](public-catalogue-evidence.drawio) | [PNG](public-catalogue-evidence.png) |
| Public map flow | snapshot, realtime, details і history | [D10](../web-clients.md) | [Draw.io](public-map-user-flow.drawio) | [PNG](public-map-user-flow.png) |
| Read-side interaction | REST, SignalR, DB і notification bridge | [D07](../public-api-read-side.md) | [Draw.io](read-side-interaction.drawio) | [PNG](read-side-interaction.png) |
| Reference data flow | Gazetteer, taxonomy і seed | [D03](../domain-data-model.md) | [Draw.io](reference-data-flow.drawio) | [PNG](reference-data-flow.png) |
| Rules і LLM | decision path та audit | [D04](../collectors-ingestion.md) | [Draw.io](rules-llm-decision.drawio) | [PNG](rules-llm-decision.png) |
| Ruleset lifecycle | draft, publish, rollback і audit | [D08](../admin-operations.md) | [Draw.io](ruleset-change-lifecycle.drawio) | [PNG](ruleset-change-lifecycle.png) |
| Runtime configuration | env, appsettings, БД і сервіси | [D02](../deployment-operations.md) | [Draw.io](runtime-configuration.drawio) | [PNG](runtime-configuration.png) |
| Track lifecycle | revisions, watchdog і closure | [D06](../correlation-tracks-incidents.md) | [Draw.io](track-lifecycle-revisions.drawio) | [PNG](track-lifecycle-revisions.png) |

Вимоги до пари та імені визначені у [правилах](../naming.md); фіксований
спосіб генерації PNG — у [інструкції експорту](export.md).
