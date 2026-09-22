# Адміністративний контур

Admin працює на окремому порті `8091` і має доступ на запис. Не відкривайте його у публічний інтернет. Задайте `Admin__Token`; UI передає його в `X-Admin-Token`.

Панель показує колектори, рівно один processor, pipeline, PostgreSQL, логи та контейнери. Для processor немає scale endpoint: Compose фіксує одну репліку, а PostgreSQL advisory lock блокує випадково запущений другий процес.

Ruleset API підтримує draft → replace rules → validate → preview/corpus → publish і rollback. Мутації вимагають `actor` та `reason`.

`POST /api/admin/sources` створює Telegram джерело з кодом `tg_<username lowercase>` ([правила кодів](naming.md#коди-джерел)) і відповідає `409`, якщо цей канал уже є джерелом під будь-яким кодом; те саме при зміні `channel` через `PUT /api/admin/sources/{id}`. Джерела з повідомленнями не видаляються — їх вимикають.

`POST /api/admin/ops/reprocess` вимагає точного `REPROCESS_DERIVED_DATA`, очищує похідні targets/tracks/alerts і повертає raw rows у Pending. Оригінали в `raw_messages`, sources, settings і довідники не видаляються. Ексклюзивний лок на `raw_messages` тримається лише мить: масове скидання статусів іде батчами без локу, а похідні таблиці чистяться під advisory-локом Store, тож колектори продовжують писати. На час операції ставиться пауза `reprocess: …`; якщо операція обірвалась, пауза лишається з причиною `reprocess: failed …` — повторний виклик того самого ендпоінта дозволений поверх неї і завершує роботу (ідемпотентно). Пауза `history load: …` належить Telegram history load; поверх неї reprocess повертає `409`. Той самий порядок у [`scripts/reprocess.sql`](../scripts/reprocess.sql).

Після зміни перевірте `/api/health`, один processor heartbeat, зменшення Pending, відсутність нових Failed і контрольний об'єкт через API.
