# Адміністративний контур

Admin працює на окремому порті `8091` і має доступ на запис. Не відкривайте його у публічний інтернет. Задайте `Admin__Token`; UI передає його в `X-Admin-Token`.

Панель показує колектори, рівно один processor, pipeline, PostgreSQL, логи та контейнери. Для processor немає scale endpoint: Compose фіксує одну репліку, а PostgreSQL advisory lock блокує випадково запущений другий процес.

Ruleset API підтримує draft → replace rules → validate → preview/corpus → publish і rollback. Мутації вимагають `actor` та `reason`.

`POST /api/admin/sources` створює Telegram джерело з кодом `tg_<username lowercase>` ([правила кодів](naming.md#коди-джерел)) і відповідає `409`, якщо цей канал уже є джерелом під будь-яким кодом; те саме при зміні `channel` через `PUT /api/admin/sources/{id}`. Джерела з повідомленнями не видаляються — їх вимикають.

`POST /api/admin/ops/reprocess` вимагає точного `REPROCESS_DERIVED_DATA`, очищує похідні targets/tracks/alerts і повертає raw rows у Pending. Оригінали в `raw_messages`, sources, settings і довідники не видаляються.

Після зміни перевірте `/api/health`, один processor heartbeat, зменшення Pending, відсутність нових Failed і контрольний об'єкт через API.
