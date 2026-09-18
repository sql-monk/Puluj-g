# Доменна модель, PostgreSQL/PostGIS і довідники

Puluj-G зберігає незмінний вхід окремо від похідних даних. Основний шлях: `sources` → `raw_messages` → один processor → `targets`, `target_tracks`, `track_targets`, `target_track_revisions`, `target_links` та `air_alerts`.

`raw_messages.processing_status` є durable станом прямої обробки: Pending, Processed або Failed. Processor атомарно claim-ить рядок у PostgreSQL, пише доменний результат і завершує raw row в одній транзакції. Другий процес processor не запускається: це обмежено Compose і PostgreSQL advisory lock.

`collector_states` зберігає cursor і health колектора, `processing_errors` — помилки, `llm_requests` — аудит LLM, `app_settings` — runtime settings. Аналітичний worker володіє окремою схемою `analytics` з таблицями `messages`, `track_firsts`, `runs` і `state`.

Ідентичність raw message — `(source_id, source_message_key, source_revision)`; hash є лише індексом схожості. Повторне надходження повертає наявний raw ID. Оригінальний text/payload не переписується.

`places` використовує PostGIS, SRID 4326. Точна геометрія записується лише за наявності фактичного evidence; інакше location може лишатися семантичним place або `null`.

Роль `migrate` застосовує міграції під advisory lock, потім запускає seeders для sources, taxonomy та Gazetteer. Runtime-джерелом істини є БД.

![Завантаження й використання довідників](diagrams/reference-data-flow.png)

Редагована схема: [reference-data-flow.drawio](diagrams/reference-data-flow.drawio).
