# P00 — inventory записувачів і блокувань

Дата: 2026-09-15. Поточний checkout; SQL definitions з окремої мігрованої PostGIS:
[`P00-sql-inventory.json`](P00-sql-inventory.json). Це стан тестової БД, не production audit.

## C# / SQL ownership

| Writer / вхід | Read / write scope | Межа атомарності й координація |
|---|---|---|
| `RawMessageIngestor.IngestAsync`; Telegram/alerts collectors, dev/admin ingest | `raw_messages` INSERT, identity/hash indexes | Один autocommit INSERT ON CONFLICT; NOTIFY після commit. Однаковий content під новим source ID досі дедуплікується за hash; зміна identity — P04. |
| `RawMessageClaims.ClaimOldestAsync` / `ClaimAsync` | Pending → InProgress, owner/time | Один UPDATE RETURNING, SKIP LOCKED для backlog; raw row lock. |
| `RawMessageClaims.ReleaseAsync` | InProgress → Pending конкретного owner | Один умовний UPDATE. |
| `RawMessageClaims.ReclaimExpiredAsync` | status/attempts + `processing_errors` | Status UPDATE і error INSERT наразі різні commits; можливе missing lease audit після crash. P03/P05 мають об'єднати stage state/audit. Не доказ inbox/outbox. |
| `RawMessageProcessor` | raw FOR UPDATE; targets, статус, помилки; виклик sinks | Одна transaction, savepoint для rollback бізнес-спроби. **Store до derived read/write**, до commit. Parse/LLM тримає raw row і connection; no-facts обходить Store. |
| `AlertsInUaHandler` | `air_alerts` за `(source_id, source_alert_id)`, start/end provenance | Викликається під Store до lookup. Unique index — страховка, не заміна read-modify-write lock. |
| `TextAlertSink` | source/place intervals, cancellations між джерелами; `targets` cancellation evidence | Під Store. Рекурсивні ancestor/descendant scopes включають глибину >1. Late start використовує вже збережений відбій; expiration має межу MaxAge. |
| `CorrelationSink`, `TrackUpdater` | candidates всієї категорії; duplicate targets; tracks, track_targets, revisions | Під Store, включно з «кандидатів немає → create». Confidence original target також shared write. Cancel охоплює всі категорії, якщо категорія відсутня. |
| `trg_targets_insert_kinematics` → `puluj_on_target_insert` | `source_daily_stats(source,day)`, `target_anchors`, `target_links` | Виконується всередині INSERT targets під Store. Статистика спільна між категоріями. |
| `puluj_link_target` | candidates/remaining probability своєї категорії; DELETE/INSERT target_links | SQL trigger transaction. Late observation не перебудовує весь successor graph; детермінований replay/SQL redesign — P09/P14. |
| `trg_targets_duplicate` → `puluj_on_target_duplicate` | deletes kinematic links, copy link; statistics двох sources/days; source_copies | Під Store. Взаємні copies можуть брати source/day rows у зворотному порядку; category locks самі не доводять відсутності deadlock. |
| `TrackWatchdog.SweepAsync` | Pending+InProgress event-time watermark; active tracks/revisions; expired text alerts | raw table ACCESS SHARE → Store → read/modify/commit; NOTIFY після commit. |
| `ReprocessService.ResetAsync` (admin) | усі raw statuses, deletes derived state/statistics/errors | raw table ACCESS EXCLUSIVE → reset UPDATE → Store → deletes → commit. Блокує нові claims/ingest на час reset; це обмеження legacy reset до P14. |
| `scripts/reprocess.sql`, `scripts/reprocess-alerts.sql` (manual maintenance) | raw statuses + усі derived / structured alerts | raw ACCESS EXCLUSIVE → UPDATE → Store → TRUNCATE/DELETE → commit; той самий fence, що admin reset. |
| `scripts/fix-text-alert-ends.sql` | repair invalid text intervals у air_alerts | transaction + Store → UPDATE → commit. Raw не читає і не блокує після Store. |
| `scripts/requeue-failed.sql` | Failed → Pending, attempts/claim reset | Raw-only transaction, не змінює derived state і не бере Store. |
| `SettingsStore`, `CollectorStateStore`, worker status reporters | app_settings, collector_states | Власні SaveChanges/upserts, без Store; окремі operational hot rows. |
| `AdminEndpoints` source CRUD | sources/config/secrets; collector_states при delete | EF SaveChanges; відмова видаляти джерело з raw evidence, FK як кінцевий захист. Не writer tracks/alerts. |
| `OpsEndpoints` database query | SQL read-only transaction, statement timeout | Не довільний writer; reset через окремий сервіс вище. |
| `LlmParser` audit | llm_requests | Окремий DbContext/commit, виконується під час raw transaction; provider limits не входять у baseline. |
| `DatabaseInitializer`, taxonomy/source/gazetteer seed | schema, taxonomy, places, sources | Окремий migration advisory lock; запуск до collectors/processing. Seed не припускається безпечним паралельно з довільним online schema change. |
| `AnalysisRunner`, `TrackFirstsBuilder` | analytics schema; читають public raw/tracks | Session advisory lock `ANALY`; batch transactions для messages/copies/watermark. Не writer public tracks. |
| `AnalyticsReportService.ResetAsync` | analytics copies/messages/track_firsts/state | DELETE в одній transaction, але без session lock `ANALY` воркера; взаємодія з runner — окремий audit P15, не частина Store гарантії. |

SQL helpers `puluj_target_anchor`, `puluj_class_params`, `puluj_day`, `puluj_target_chain`,
`puluj_target_predecessors`, `puluj_target_family` — read-only функції; `source_rating_daily` — view.
FK/internal triggers враховані constraints inventory; user triggers окремо. Повні definitions/indexes
в artifact, щоб перевірка не покладалась тільки на текст міграції.

## Рішення щодо experiment

Granular path вимкнено в processor/reset/watchdog. Ключ `Store = 0x50554C554A01` сумісний
з попереднім консервативним writer. Парсинг лишився паралельним, store серіалізований.
Experimental constants прибрано; primitive test перевіряє серіалізацію Store, а pipeline — окремі race tests.
Незалежність категорій не заявляється; винесення SQL hot rows і нові scopes — P09.

Порядок всіх активних stateful writers: raw table/rows → Store → derived rows.
Watchdog бере raw table read lock до Store, інакше table-fenced reset створив би цикл.
Зміни не потребують schema migration чи DTO changes.

## Інші відомі межі

- NOTIFY є best effort; poll відновлює raw backlog, але немає durable per-subscriber ACK/outbox.
- Lease owner має тільки instance name, не generation/fencing token; майбутні короткі stage
  transactions вимагають токенів (P03/P05/P06). Нинішній raw row lock утримується весь process.
- `_transientRetries` у пам'яті; після restart лічильник transient budget губиться.
- Late text starts/cancels покрито тестами цього зрізу; повне злиття перекривних інтервалів
  від кількох запізнілих starts і versioned deterministic replay не реалізоване (P09/P14).
- Baseline не перевіряє broker, provider, production canary, UI чи analytic reset. Їхні задачі окремі.
