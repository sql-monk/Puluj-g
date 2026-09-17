# План: за підсумками логу процесінгу 13–15.09 і ревʼю конвеєра

Джерела: `logs/worker-2026091{3,4,5}.log` (локальний Worker), журнал `puluj-postgis-1` (деталі дедлоків),
`docker logs puluj-processor-{1,2}`, `pg_stat_user_tables` / `pg_stat_user_indexes`, `logs/api-20260915.log`.
Код: `Puluj.Processing` (Pipeline, Correlation, Structured, Llm), `KinematicsSql`, `ReprocessService`, `NotifyBridge`.

## 1. Що показав лог

### 1.1. Дедлоки в store-стадії (15.09, 04:42–04:57) — 31 випадок, 1 повідомлення втрачене

`40P01 deadlock detected` між `UPDATE targets SET duplicate_of_target_id` і `UPDATE target_tracks …` — тобто **дві
транзакції одночасно в store-стадії**, чого advisory-замок `AdvisoryLocks.Store` не мав допустити. Причина — не код, а
змішаний деплой: docker-контейнери зі старого образу (`puluj-processor`, зібраний 00:19, до появи `AdvisoryLocks.cs`
о 04:02) працювали до 04:59 паралельно з локальним Worker нової версії (04:42–04:57). Старий процесор не бере замок,
тож обидва писали в `target_tracks`/`source_daily_stats` навперемінно. Після перезбирання реплік о 04:59 у журналі
PostgreSQL дедлоків немає (`docker logs puluj-postgis-1 | grep deadlock`).

Наслідки, які вже є проблемою коду:
- транзієнтна помилка (`PostgresException.IsTransient`, 40P01/40001) рахується як спроба: `RawMessage 99794` після двох
  дедлоків став `Failed` (attempts = 4 ≥ MaxAttempts 3) і вже не обробиться без ручного скидання;
- кожен дедлок дає два ERR зі стек-трейсом (EF `Update` + `RawMessageProcessor`) — 80 рядків логу на подію;
- ніщо не заважає повторити змішаний запуск (локальний `dev-run.ps1` поверх docker-реплік) — memory вже фіксувала таку
  саму проблему для Telegram-сесії.

### 1.2. Пропускна здатність: ~11–16 повідомлень/с, стеля — store-стадія під глобальним замком

Локальний прогін (2 воркери, 10 095 повідомлень): total mean 163 мс, p50 105, p90 374, p99 952; `rules` mean 1.2 мс;
«parse» mean 74 мс — але це **очікування advisory-замка**, бо `parsedMs` знімається після `AdvisoryLocks.Take`;
`store + sinks` mean 84 мс, p90 196. Docker-репліки (2 × 2 воркери) зараз дають ~950 повідомлень/хв при 89 610 Pending —
~1,5 год на хвіст. План паралельної обробки оцінював store у 20–40 мс і стелю 30–50/с; реально удвічі гірше.

Куди йде store (за `pg_stat_user_tables`):
- `air_alerts`: **83 346 seq scan, 671 млн прочитаних рядків** — `CorrelationSink` шукає тривогу за
  `start_raw_message_id = X OR end_raw_message_id = X` для кожної цілі AirRaidAlert/AlertCancelled; індексів на ці
  колонки немає (перевірено `EXPLAIN`: Seq Scan). Запити з `LIKE 'text:%' AND ended_at IS NULL` ідуть по частковому
  індексу `ix_air_alerts_place_id` — з ними все гаразд.
- `CloseTracksInRegionAsync` вантажить усі активні треки з локацією на кожен «відбій» і фільтрує область у пам'яті;
  під час перебудови активних треків сотні (watchdog закриває 50–220 на хвилину).
- `RecountSourcesAsync` + `BestConfidenceAsync` — два запити по `track_targets ⋈ targets` на кожне приєднання; можна одним.
- Тригери `puluj_on_target_insert` → `puluj_link_target` (~15 мс/ціль) і `puluj_on_target_duplicate` — у межах
  очікуваного, не чіпаємо.
- `raw_messages`: 1 930 seq scan × ~230 тис. рядків — це `GROUP BY processing_status` адмін-панелі, не процесор;
  `target_track_revisions`: 1 653 seq scan — replay/аналітика. Поза цим планом, лише зафіксовано.

### 1.3. LLM: 7 (15.09) і 62 (13.09) `LLM request failed` — `credit balance is too low`

`LlmParser` не має «запобіжника»: кожне повідомлення, що схоже на звіт про ціль, робить HTTP-виклик (усередині
транзакції з `FOR UPDATE` на рядку) і пише WRN зі стек-трейсом. При вичерпаному балансі/недійсному ключі це марні
сотні мілісекунд і шум на кожне повідомлення, доки ліміт `MaxCallsPerMinute` не спрацює.

### 1.4. Помилки 13.09, які вже усунуто в поточному коді (перевірено)

- `ArgumentOutOfRangeException` у `CountExtractor.Extract` (185 падінь) — guard `targetTokenIndex > tokens.Count` є.
- `23505 ix_air_alerts_source_id_source_alert_id` (87) — start/end однієї тривоги в паралельних транзакціях; закрито
  advisory-замком перед `AlertsInUaHandler`.
- `ChannelReader.Count NotSupported` (64 «Pending sweep failed») — старий `ProcessingLoop`, коду більше немає.
- `unknown location 'м. Київ' (oblast)` (75) — зараз 1 нерозвʼязана ціль на 2 363 тривоги по Києву; не відтворюється.
- Telegram `FLOOD_WAIT` — колектор, поза процесінгом.

### 1.5. Суміжне: 300 МБ `api-20260915.log` за 20 хвилин

Локальний Api зі свіжим кодом стартував о 04:18 до застосування міграції `AddRawMessageClaims` (її накотив Worker о
04:42): `column r.claimed_at does not exist` на кожен NOTIFY → 33 522 × (2 ERR EF зі стеком + WRN `NotifyBridge`).
`dev-run.ps1` стартує Api через 3 с після Worker, не чекаючи міграцій.

## 2. Ревʼю коду (що не в логах, але варто виправити)

- `RawMessageProcessor`: рядок логу «parse N ms» містить очікування замка — діагностика вводить в оману (п. 1.2).
  Немає метрики стадій (лише лічильники), тому стелю не видно в OTel.
- `RecordFailureAsync`: усі винятки рівні — транзієнтні PostgreSQL-помилки треба повертати в Pending без спроби.
- `CorrelationSink.OnTargetsAsync`: пошук тривоги за raw_message_id без індексу (п. 1.2); `CloseTracksInRegionAsync`
  можна відсікти в SQL за `LastSeenAt <= o.ObservedAt` і категорією до завантаження.
- `LlmParser`: немає паузи після невідновних помилок API (400 invalid_request/401/403) і після 429.
- `ProcessingLoop.ReleaseAsync`: текст логу обірваний («the lease will»).
- `dev-run.ps1`: не попереджає, що docker-репліки `puluj-processor-*` / `puluj-collector-telegram-*` уже працюють над
  тією ж БД (джерело дедлоків 15.09 і конфлікту Telegram-сесії).
- `docs/README.md` «Експлуатація»: немає вимоги «усі процесори однієї версії», немає пояснення полів таймінгу.

## 3. Роботи

### Фаза 1 — надійність (робиться зараз)

1. **Транзієнтні помилки БД не рахуються спробою.** `RecordFailureAsync`: якщо у ланцюжку винятків є
   `PostgresException` з `IsTransient` (40001, 40P01, 55P03, 57P01, 08xxx) — `ROLLBACK TO SAVEPOINT`, рядок назад у
   Pending з тими самими `attempts`, `processing_errors.stage = "transient"` (повідомлення = SqlState + MessageText,
   без стеку), лог WRN одним рядком, метрика `RawProcessed(…, "retried_transient")`. Щоб «отруйне» повідомлення не
   крутилося вічно, транзієнтний повтор обмежується `ProcessingOptions.MaxTransientRetries` (типово 10) — лічильник у
   пам'яті процесора на raw_message_id; після нього — звичайний шлях зі спробою.
   Тест (`ParallelProcessingTests`): імітація транзієнтної помилки → attempts не змінився, статус Pending, є запис
   stage `transient`.
2. **Скинути `RawMessage 99794`** назад у Pending (SQL у `scripts/`, одноразово, після п. 1 — інакше знову Failed).
3. **Таймінги стадій.** `RawMessageProcessor`: `parse {ParseMs} ms, lock wait {LockMs} ms, store + sinks {SinkMs} ms`;
   гістограма `puluj.processing.stage{stage=parse|lock|store}` (мс) у `PulujMetrics`; docs «Експлуатація» — що означає
   кожне поле і що «lock wait ≈ store» = стеля глобального замка.
4. **LLM circuit breaker.** `LlmOptions.FailurePause` (типово 15 хв): після `AnthropicApiException` зі статусом
   400/401/403 (billing, auth, invalid request) модель не викликається до кінця паузи; після 429 — 1 хв. Перший збій —
   WRN без стеку (тип помилки + message), далі DBG; метрика `LlmCall("paused")`. Unit-тест на логіку паузи (без
   мережі — винести рішення в `LlmBreaker`).
5. **Захист від змішаного запуску.** `dev-run.ps1`: якщо `docker ps` (бінарник у
   `C:\Program Files\Docker\Docker\resources\bin`, може бути не в PATH) показує контейнери `puluj-processor-*` або
   `puluj-collector-telegram-*` — `Write-Warning` з поясненням (дедлоки / одна Telegram-сесія) і запит підтвердження
   (`-Force` пропускає). `docs/README.md` «Експлуатація»: усі процесори над однією БД — однієї версії (advisory-замок
   існує лише з `AddRawMessageClaims`); при оновленні спершу зупинити старі.
6. **Api/Admin не стартують зі схемою, старішою за код.** У `Program.cs` обох сервісів перед `app.Run()`: цикл
   `GetPendingMigrationsAsync` (роль `puluj_reader` має SELECT на `__EFMigrationsHistory`) — доки є незастосовані
   міграції, раз на 5 с лог ERR «waiting for migrations: …» і health = Unhealthy; ендпоїнти не піднімаються. Захищає
   від п. 1.5 і від docker-старту `api` раніше за `migrate`. `NotifyBridge`: повторні збої одного типу — не частіше
   одного WRN на хвилину (лічильник, як `SkipReportInterval`).

### Фаза 2 — store-стадія (після фази 1, окремою міграцією)

7. Індекси `air_alerts (start_raw_message_id)`, `air_alerts (end_raw_message_id)` (`TargetConfiguration`, міграція
   `AddAirAlertMessageIndexes`); запит у `CorrelationSink` — два `FirstOrDefault` замість `OR`, щоб планувальник
   гарантовано брав індекс.
8. `CloseTracksInRegionAsync`: у SQL — `Status == Active && LastLocationPlaceId != null && LastSeenAt <= o.ObservedAt`
   і `TargetCategoryId == o.TargetCategoryId` коли задано; семантика області (через газетир) лишається в пам'яті.
9. `RecountSourcesAsync` + `BestConfidenceAsync` → один запит (`GroupBy` → `Count(distinct source)`, `Max(confidence)`).
10. Заміряти після 7–9 на перебудові (`store + sinks` p50/p90 з логу) і оновити оцінку стелі в
    `plan-parallel-processing.md`.

### Фаза 3 — за межі глобального замка (лише план)

Дрібніший ключ advisory-замка: за `target_category_id` для цілей TargetObserved (дедуплікація і кореляція живуть у межах
категорії), «усі категорії у фіксованому порядку» — для тривог, відбоїв без категорії та `AlertsInUaHandler`. Потрібно
довести відсутність циклів (порядок ключів за зростанням) і що тригер `puluj_link_target` читає лише свою категорію
(так: `t.target_category_id = n.target_category_id`). Окремий план після вимірів фази 2.

## 4. Файли

- `src/Puluj.Processing/Pipeline/RawMessageProcessor.cs`, `ProcessingOptions.cs`, `Pipeline/ProcessingLoop.cs`
- `src/Puluj.Processing/Llm/LlmParser.cs`, `LlmOptions.cs`, новий `Llm/LlmBreaker.cs`
- `src/Puluj.Processing/Correlation/CorrelationSink.cs`
- `src/Puluj.Infrastructure/PulujMetrics.cs`, `Persistence/Configurations/TargetConfiguration.cs`, міграція
- `src/Puluj.Api/Program.cs`, `src/Puluj.Admin/Program.cs`, `src/Puluj.Api/Services/NotifyBridge.cs`
- `scripts/dev-run.ps1`, `scripts/requeue-failed.sql` (новий)
- `tests/Puluj.Integration.Tests/ParallelProcessingTests.cs`, `tests/Puluj.Processing.Tests/Llm/*`
- `docs/README.md` («Експлуатація»), `docs/plan-parallel-processing.md` (оцінка стелі)

## Статус

2026-09-15: фаза 1 (пп. 1, 3–6; п. 2 — скрипт `scripts/requeue-failed.sql`, не виконаний) і пп. 7–9 фази 2 реалізовано;
міграція `20260915023646_AddAirAlertMessageIndexes`. П. 10 (заміри після індексів) — після накату на робочу БД.
Фаза 3 — не починалась.
