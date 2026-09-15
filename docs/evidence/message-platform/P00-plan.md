# P00 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/1. Початок: 2026-09-15, статус In Progress.
Base: `3fa6e9145a15ba3f44472d14db63fdf229ce61f5`, з наявними незакоміченими змінами.
Виконавець: Codex; незалежний reviewer: субагент `p00_review` (дозволено користувачем).

## Аналіз

`AlertsInUaHandler.HandleAsync` читає інтервал до `TakeDerivedStateLocksAsync`.
Два старти можуть одночасно побачити відсутній інтервал; початок і кінець — втратити частину provenance.
Тригери targets змінюють `source_daily_stats`, `source_copies`, `target_links`, `target_anchors`:
категорії не ізолюють ці записи. Тести незалежних advisory keys перевіряють примітив, а не pipeline.
Fixture приховує відсутню БД через ранній return, що дає хибні passed.

## Кроки

1. Зберегти початковий diff; скласти inventory C# writers, claims, triggers, admin/reset/watchdog.
2. Перевірити план на порядок locks, read-before-lock, SQL side effects, scope й тестованість.
3. Виключити granular experiment із робочого шляху: консервативний Store lock до першого
   derived read/write, той самий lock для watchdog/reset. Парсинг і no-facts лишити паралельними.
4. Зробити відсутню інфраструктуру явною помилкою suite; захистити external test connection
   від ненавмисного очищення потрібної БД. Запускати на одноразовій PostGIS.
5. Додати реальні конкурентні тести structured start/end і duplicates, track candidate-create,
   text cancellation і watchdog, з committed-state assertions і обмеженим часом.
6. Зняти актуальні functions/triggers/indexes із тестової БД. Зберегти SQL inventory artifact.
7. Запустити baseline 1/2/4 workers на однакових синтетичних даних і ресурсах: mixed,
   одна категорія, alerts, no-facts, deterministic slow-parser, history/live. Рахувати committed
   roots, retries/errors окремо, throughput, drain, percentiles і доступні resource/wait metrics.
8. Виконати build і релевантні suites; записати команди, exit codes, кількість тестів, build identity.
9. Незалежне review diff/evidence; виправити blocking findings і повторити зачеплені перевірки.
10. Оновити реєстр P00, handoff, rollback/rollout та статус GitHub відповідно до доказів.

## Самоперевірка плану

- P00 є audit/baseline, не впровадженням RabbitMQ чи нових writers із P03–P09.
- Не можна оголосити granular patch надійним лише після перенесення одного lock: SQL writers
  перетинають його scopes. Консервативний baseline — свідомий вибір, scaling gate може не пройти.
- Store охоплює і читання, і commit. Structured шлях захоплює його до handler, text — після parser.
- Усі тести працюють на одноразовій БД; Docker-сервіси користувача не перезапускаються.
- Stub LLM вимірює очікування parser, не provider rate limits/cost. RabbitMQ metrics — N/A до P02.
- Evidence описує точні межі вимірів і всі пропуски; самоперевірка не заміняє незалежне review.

## Незалежне review плану — p00_review

Погоджено повернення Store. До виконання додано:

1. Reset не fence-ить Pending/new rows через status-filtered UPDATE: потрібен raw table fence.
2. Watchdog має брати raw table read lock **до** Store, щоб reset не створив зворотного порядку.
3. Watermark watchdog включає InProgress, а не лише Pending.
4. Text cancellation тестує область → район → громаду/поселення, а також end-before-start.

## Незалежне review реалізації й методики

- Store, raw fence та recursive scope погоджені reviewer.
- Baseline live-only/history-live виправлено: ідентичні 30 live roots, +90 history у другому.
- Додано assertions нульових facts/tracks/alerts для no-facts та breakdown mixed.
- `TargetCount` рахує canonical observations, не evidence duplicates: тест перевіряє 1 canonical,
  12 links/revisions, 11 duplicates, 2 sources. Семантику домену заради тесту не змінено.
- Відновлено no-facts telemetry і cleanup transient retry counter.
- Завершальний audit додав manual SQL maintenance writers. Обидва reprocess scripts отримали
  raw table fence; repair intervals — transaction + Store. Самі scripts виконано в конкурентних
  integration tests на одноразовій БД: усі три сценарії пройшли.
- Фінальні результати та завершальне review — у P00-handoff.md.
