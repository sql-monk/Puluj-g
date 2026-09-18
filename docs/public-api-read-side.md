# Публічний API, realtime і read-side

`Puluj.Api` віддає read-side через REST `/api/*` та SignalR `/hubs/map`. Публічні endpoints лише читають PostgreSQL і cached reference data.

Основні маршрути:

- service/map: `/api/version`, `/api/health`, `/api/map/config`, `/api/snapshot`, `/api/replay`, `/api/timeline`;
- tracks/targets/alerts: `/api/tracks/{id}`, `/api/tracks/{id}/predecessors`, `/api/targets/{id}`, `/api/targets`, `/api/alerts/history`;
- public catalogue: `/api/public/entities`, `/{kind}/{id}`, `/evidence`, `/messages`, `/relations`;
- reference: `/api/taxonomy`, `/api/event-kinds`, `/api/sources`, `/api/places/*`;
- statistics: `/api/stats/targets`, `/api/stats/alerts`, `/api/stats/sources`, `/api/stats/recognition`.

Підтримувані catalogue kinds: `track`, `alert`, `observation`. Cursor є opaque; long IDs передаються як decimal strings і не мають перетворюватися на JavaScript `number`.

Hub надсилає `TrackUpserted`, `TrackClosed`, `AlertChanged`, `TargetCreated` та `Resync(at)`. PostgreSQL `LISTEN/NOTIFY` і SignalR — best-effort wake-up: після reconnect або `Resync` клієнт перечитує REST source of truth.

![Read-side interaction](diagrams/read-side-interaction.png)

Редагована схема: [read-side-interaction.drawio](diagrams/read-side-interaction.drawio).
