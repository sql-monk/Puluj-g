# Адміністративний контур

Admin працює на окремому порті `8091` і має доступ на запис. Не відкривайте
його у публічний інтернет. Задайте `Admin__Token`; UI зберігає його лише в
localStorage браузера і передає в `X-Admin-Token`.

## Моніторинг

Панель показує один processor, колектори, pipeline, PostgreSQL, логи та
контейнери. Processor має фіксовану кількість контейнерів — один;
Admin не має endpoint або UI для його масштабування. Дозволені дії над
контейнерами — restart, stop і start; `admin`, `postgis` та `migrate` захищені.

SQL console (`POST /api/admin/ops/db/query`) приймає один `SELECT` або `WITH … SELECT`:
без `;`, коментарів, `app_settings`, секретних назв і `pg_*`. Запит виконується
у read-only транзакції з timeout 10 с і лімітом 200 рядків.

## Керовані зміни

Ruleset API реалізує draft → replace rules → validate → preview/corpus → shadow →
publish, а також stop shadow і rollback. Мутації вимагають `actor` і `reason`.
Incident commands (resolve/retract/confirm/suppress/unsuppress/merge/split) проходять
через locked `IncidentStateWriter` і пишуть revision з actor/reason.

`POST /api/admin/ops/reprocess` вимагає точного `REPROCESS_DERIVED_DATA`, відмовляє при
paused processing і скидає похідні дані для повторної побудови. Оригінали в
`raw_messages` не видаляються, але результат на карті змінюється; перед дією
потрібні scope, backup і план перевірки.

## Перевірка

Після зміни конфігурації або стану перевірте:

- `/api/health`;
- один живий processor heartbeat;
- зменшення Pending у `raw_messages`;
- відсутність нових Error у processor/PostgreSQL logs;
- контрольний об'єкт через API й публічну карту.
