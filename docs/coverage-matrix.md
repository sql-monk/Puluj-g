# Матриця активної документації

| Область | Документ | Source of truth |
| --- | --- | --- |
| Compose, міграції, один processor | [Розгортання](deployment-operations.md) | [Compose](../deploy/docker-compose.yml), [deploy](../scripts/deploy.ps1) |
| PostgreSQL/PostGIS і сутності | [Доменна модель](domain-data-model.md) | [PulujDbContext](../src/Puluj.Infrastructure/Persistence/PulujDbContext.cs) |
| Collector → raw → processor | [Колектори](collectors-ingestion.md) | [ingestor](../src/Puluj.Infrastructure/Ingestion/RawMessageIngestor.cs), [processing loop](../src/Puluj.Processing/Pipeline/ProcessingLoop.cs) |
| Кореляція й треки | [Кореляція](correlation-tracks.md) | [correlator](../src/Puluj.Processing/Correlation/Correlator.cs), [sink](../src/Puluj.Processing/Correlation/CorrelationSink.cs) |
| Public read-side | [API](public-api-read-side.md) | [routes](../src/Puluj.Api/Endpoints/EndpointRouteBuilderExtensions.cs) |
| Admin | [Операції](admin-operations.md) | [admin program](../src/Puluj.Admin/Program.cs) |
| Analytics | [Аналітика](analytics-service.md) | [analytics context](../src/Puluj.Analytics/Persistence/AnalyticsDbContext.cs) |
| Browser clients | [Web](web-clients.md) | [Vite config](../web/vite.config.ts) |

`pwsh -File scripts/docs/verify-docs.ps1` перевіряє локальні посилання та пари Draw.io/PNG. Він не запускає Docker або зовнішні інтеграції.
