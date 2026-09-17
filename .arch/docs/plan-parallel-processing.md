# План: паралельні інстанси процесора повідомлень

Мета — запускати кілька контейнерів Worker з роллю `processing` (і кілька воркерів усередині одного процесу) над однією
базою так, щоб кожне `RawMessage` було оброблене рівно один раз, жодне не губилось при крашах, а кореляція/дедуплікація
не псувалися від одночасних записів.

## 1. Поточний стан (що заважає)

- `ProcessingLoop` — «single consumer»: sweeper читає `Pending` з БД у пам'ять (`RawMessageQueue`, `SingleReader`),
  а `RawMessageProcessor.ProcessAsync` не бере жодного блокування — два інстанси прочитають один і той самий Pending-рядок
  і обидва створять цілі (дублікати).
- Стан у БД: `processing_status` 0 Pending / 1 Processed / 2 Failed / 3 Skipped; немає «взято в роботу», немає власника.
- `CorrelationSink`, `TextAlertSink`, тригер лінкера `puluj_on_target_insert` роблять read-modify-write над треками,
  тривогами, `target_links` — без серіалізації два паралельні повідомлення про один об'єкт відкриють два треки, не побачать
  дублікат, або зіпсують `TargetCount`/`Sequence`.
- `TrackWatchdog` теж читає-змінює треки без узгодження з sink-ом.
- `ReprocessService.ResetAsync` повертає рядки в Pending, не чекаючи на ті, що зараз обробляються.
- `RecordFailureAsync` робить `Attempts++` через окремий контекст (read-modify-write) — при паралелізмі можливий lost update.
- Compose: `processor` — «рівно один інстанс»; `Worker:Name` однакове для реплік.

## 2. Механізм захоплення (claim) у БД

Новий статус `ProcessingStatus.InProgress = 4`, нові колонки `raw_messages.claimed_by varchar(64) NULL`,
`claimed_at timestamptz NULL`.

Клас `RawMessageClaims` (`Puluj.Processing/Pipeline/RawMessageClaims.cs`), сирий SQL через `NpgsqlCommand` на з'єднанні
`PulujDbContext` (як `RawMessageIngestor`): EF `SqlQuery` загортає текст у підзапит, а `UPDATE … RETURNING` / CTE зі
зміною даних дозволені лише на верхньому рівні.

1. **Захопити найстаріше** (backlog, sweep):
   ```sql
   WITH next AS (
       SELECT raw_message_id FROM raw_messages
       WHERE processing_status = 0 AND attempts < @max
       ORDER BY published_at, raw_message_id
       LIMIT 1 FOR UPDATE SKIP LOCKED)
   UPDATE raw_messages r SET processing_status = 4, claimed_by = @name, claimed_at = now()
   FROM next WHERE r.raw_message_id = next.raw_message_id
   RETURNING r.raw_message_id
   ```
   `FOR UPDATE SKIP LOCKED` + `UPDATE` в одному стейтменті — два воркери ніколи не отримають один рядок. Індекс
   `ix_raw_messages_pending_published` (published_at WHERE status = 0) обслуговує ORDER BY. Один рядок за раз: у пам'яті
   немає «захоплених, але не початих», тож при зупинці/краші в підвішеному стані лишається щонайбільше по одному
   повідомленню на воркер.
2. **Захопити конкретний id** (NOTIFY `RawMessageStored`): той самий UPDATE з `WHERE raw_message_id = @id AND
   processing_status = 0 AND attempts < @max RETURNING`. Усі інстанси отримують NOTIFY, виграє один; решта отримує 0 рядків.
3. **Повернути прострочені** (lease): раз на `PendingPollInterval` кожен інстанс виконує
   ```sql
   UPDATE raw_messages SET
       attempts = attempts + 1,
       processing_status = CASE WHEN attempts + 1 >= @max THEN 2 ELSE 0 END,
       processed_at = CASE WHEN attempts + 1 >= @max THEN now() ELSE NULL END,
       claimed_by = NULL, claimed_at = NULL
   WHERE processing_status = 4 AND claimed_at < now() - @lease
   RETURNING raw_message_id, source_id, processing_status
   ```
   Спроба, що впала разом із процесом, рахується (інакше «отруйне» повідомлення, що валить процес, крутилося б вічно).
   Для рядків, що стали Failed, пишеться `processing_errors` (stage `lease`). Ліз (`ProcessingOptions.ClaimLease`,
   типово 5 хв) має перекривати найдовшу обробку (LLM-виклик 4–6 с з повторами).
4. **Звільнити при зупинці**: воркер, скасований після claim (до транзакції або посеред неї — транзакція відкочується,
   рядок лишається InProgress), повертає рядок у Pending з `CancellationToken.None`
   (`WHERE raw_message_id = @id AND processing_status = 4 AND claimed_by = @name`). Це лише оптимізація — ліз і так поверне,
   але без зайвої зарахованої спроби і без 5-хвилинної затримки після рестарту.

**Обробка під блокуванням рядка.** `RawMessageProcessor.ProcessAsync` відкриває транзакцію і першим ділом бере
`SELECT … FROM raw_messages WHERE raw_message_id = @id FOR UPDATE`, потім завантажує рядок і перевіряє:
- `Pending` — обробляє (прямий виклик: тести, dev-сценарії; claim тут не обов'язковий — блокування рядка робить це безпечним);
- `InProgress` і `claimed_by == моє ім'я` — обробляє;
- інакше — пропускає (0). Так повернення за лізом плюс повторний claim іншим інстансом ніколи не дадуть подвійної обробки:
  усі рішення про статус приймаються під блокуванням рядка, а `UPDATE`, що чекав на цей замок, після коміту перевіряє
  WHERE заново (EvalPlanQual) і не зачепить рядок, що став Processed.

**Помилки.** Замість rollback + окремого контексту: `SAVEPOINT` після блокування рядка; при винятку —
`ROLLBACK TO SAVEPOINT`, `ChangeTracker.Clear()`, атомарний `UPDATE raw_messages SET attempts = attempts + 1,
processing_status = CASE …, claimed_by/claimed_at = NULL …` + `INSERT processing_errors`, коміт. Рядок весь час під замком,
lost update неможливий; при `attempts + 1 >= MaxAttempts` — Failed, інакше назад у Pending (як зараз, повтор при
наступному claim). Якщо впала сама транзакція/з'єднання — лог, рядок лишається InProgress, його поверне ліз.

Успішна обробка лишає `claimed_by`/`claimed_at` як provenance («хто і коли взяв»), для Skipped теж.

## 3. Порядок обробки та кореляція

- Один інстанс з `Concurrency = 1` дає **точно сьогоднішню семантику**: найстаріше-перше, NOTIFY-повідомлення поза чергою.
- З N воркерами повідомлення в межині «вікна» ~N штук стають у довільному порядку; кореляція вже терпить це (`isNewer`,
  «відбій не закриває трек, бачений після нього», кандидати за вікном часу), але детермінізм перебудови втрачається.
  Задокументувати: для відтворюваної перебудови — один інстанс, `Concurrency = 1`.
- **Серіалізація стадії запису.** Парсинг тексту (нормалізація, правила, LLM — уся дорога частина) іде паралельно;
  все, що читає-змінює похідні таблиці — `AlertsInUaHandler` (read-modify-write `air_alerts`: start і end однієї тривоги
  сусідять у черзі й без замка дали б unique violation), вставка цілей (тригери лінкера), sink-и (дедуплікація,
  кореляція, текстові тривоги) і коміт — під глобальним `pg_advisory_xact_lock(AdvisoryLocks.Store)` у транзакції
  повідомлення; замок береться безпосередньо перед першим записом у похідні таблиці. Один ключ на транзакцію → дедлоків між
  воркерами немає; порядок замків: рядок raw_message → advisory. `ReprocessService` бере advisory-замок лише після того, як узяв замки рядків
  raw_messages (див. п. 4) — воркер ніколи не чекає на reset, тримаючи advisory, отже циклу немає. Ціна: стадія запису ~20–40 мс/повідомлення, тобто стеля ~30–50 повідомлень/с сумарно — те, що є зараз в
  одному процесі; масштабуються парсинг і LLM. Дрібніший ключ (за категорією цілі) — можливе продовження, не в цьому плані.
- `TrackWatchdog` бере той самий advisory-замок у явній транзакції — його read-modify-write треків більше не гоняється з
  sink-ом. Кожен інстанс запускає свій watchdog; другий просто нічого не знаходить.
- `TrackWatchdog.oldestPending` рахує лише Pending — InProgress-рядків одночасно ≤ інстансів × Concurrency, впливу немає.

## 4. NOTIFY, sweep, пауза

- Кожен інстанс тримає свій LISTEN (`PgNotifyListener`, є). `RawMessageQueue` стає multi-reader каналом id з NOTIFY
  (`SingleReader = false`), без `Depth`/`DequeueAllAsync`; воркери беруть id через `TryDequeue` і `WaitToReadAsync`.
- Sweep у старому вигляді зникає: воркер у циклі
  1. якщо в каналі є id з NOTIFY → claim-by-id → обробка;
  2. інакше claim-oldest → обробка;
  3. нічого не захоплено → чекає першого з: новий id у каналі, `PendingPollInterval` (страховка від загубленого NOTIFY).
- Пауза (`Runtime:Processing:Paused`): один фоновий таск на інстанс перечитує прапорець раз на `PendingPollInterval` і
  водночас виконує повернення за лізом; воркери під час паузи не роблять claim-oldest **і** claim-by-id (NOTIFY-id під час
  паузи відкидаються — sweep підбере їх після відновлення; сьогодні NOTIFY обходив паузу). Затримка реакції ≤ інтервалу.
- `ReprocessService.ResetAsync` — одна транзакція у такому порядку: (1) `UPDATE raw_messages SET processing_status = 0,
  attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL WHERE processing_status <> 0` — блокується на
  рядках, які саме обробляються, до їхнього коміту, після чого перевірка WHERE повертає їх у Pending; (2)
  `pg_advisory_xact_lock(Store)`; (3) DELETE похідного; COMMIT. Чому так: після (1) reset тримає замки всіх рядків, тому
  claim-oldest (`SKIP LOCKED`) нічого не бере, claim-by-id чекає коміту і бере вже скинутий рядок — його ціль ляже
  після DELETE, тобто це вже перебудова. Похідне повідомлення, закомічене до (1), видаляється у (3), а рядок скинуто
  в (1). Дедлок неможливий: reset чекає на рядки → власники рядків чекають на advisory → власник advisory тримає лише
  свій рядок і на reset не чекає (claim і `SELECT … FOR UPDATE` воркера, що чекають на reset, нічого не тримають).
  Це закриває наявне вікно «ціль записана після DELETE, рядок повернуто в Pending → дубль» без пауз і очікувань.
  `scripts/reprocess.sql`, `reprocess-alerts.sql` — той самий порядок (UPDATE → advisory → TRUNCATE/DELETE),
  `claimed_by = NULL`; ключ замка задокументований у скрипті.

## 5. Паралелізм усередині інстансу

`ProcessingOptions.Concurrency` (типово 2; 1 = поведінка як зараз); `PendingBatchSize` більше не потрібен — прибрати з опцій,
appsettings і docs. `ClaimLease` — новий. `ProcessingLoop` запускає `Concurrency` однакових
воркер-циклів з п. 4 плюс один сервісний таск (пауза + ліз). Кількість інстансів і `Concurrency` незалежні; сумарна
кількість одночасних транзакцій = інстанси × Concurrency (пул Npgsql за замовчуванням 100 — достатньо).

## 6. Схема БД і міграція

- `RawMessage`: `ClaimedBy string?` (64), `ClaimedAt DateTimeOffset?`; enum `InProgress = 4`.
- `RawMessageConfiguration`: властивості + partial-індекс `ix_raw_messages_in_progress_claimed_at` (claimed_at WHERE
  processing_status = 4) для запиту лізу. Існуючі індекси на `processing_status = 0` обслуговують claim.
- Міграція `AddRawMessageClaims` — через `scripts/add-migration.ps1` (dotnet ef 10.0.12 є; `DesignTimeDbContextFactory`
  не потребує БД). Перевірити, що згенерований Up містить лише дві колонки й індекс (snapshot чистий у git).
- Ролі: `puluj_admin` має UPDATE на всі таблиці — нові колонки покриті; `puluj_reader` — SELECT.
- `processing_errors.stage = "lease"` для повернених за лізом до Failed.

## 7. Ідентичність інстансу, compose

- `WorkerOptions`: `AppendHostName` (bool, типово false); `InstanceName` = `Name` або `Name-<hostname>`. Heartbeat,
  `claimed_by` і властивість `app` у логах/телеметрії (`Program.cs`) використовують `InstanceName`.
- `AddPulujProcessing(configuration, instanceName)`: реєструє `ProcessorIdentity(Name)`; тести/дефолт — `Environment.MachineName`.
- `deploy/docker-compose.yml`: `processor` з `deploy.replicas: 2`, `Worker__AppendHostName: "true"`,
  `Processing__Concurrency: 2`. Лог-файл `worker-processor-.log` спільний для реплік: file sink уже має `shared: true`
  (appsettings.json), рядки розрізняються властивістю `app` = `puluj-<InstanceName>`.
- Правильність claim не залежить від унікальності імен (замки рядків), унікальність потрібна лише для heartbeat і provenance.

## 8. Спостережуваність

- `PulujMetrics.RawProcessed(instance, outcome)` — лічильник `puluj.rawmessages.processed{instance,outcome}` (processed /
  skipped / failed / retried).
- Адмін-панель: `queue` уже містить усі статуси (`InProgress` з'явиться сам; `OpsPanels.tsx` уже додає його до «У черзі»).
  Додати в `ProcessingReportDto` розбивку InProgress за `claimed_by`? — ні, зайве для цього кроку; `claimed_by` видно в БД.
- Лог старту: `Processing loop started: {Instance}, concurrency {N}`.
- Задокументувати: `Llm:MaxCallsPerMinute` — ліміт на процес, з N інстансами сумарний ліміт × N; heartbeat-ключі реплік
  (`processor-<hostname>`) змінюються при перестворенні контейнера — застарілі ключі панель ховає через 15 хв, у
  `app_settings` вони лишаються (кілька байтів на рестарт).

## 9. Тести

Інтеграційні (`tests/Puluj.Integration.Tests`, PostGIS через Testcontainers — Docker є):
1. `Concurrent_claimers_process_each_message_exactly_once`: 60 повідомлень, 4 воркер-таски з різними іменами (2 імені ×
   2 воркери — імітація двох інстансів; свій `RawMessageProcessor` на ім'я через `ActivatorUtilities.CreateInstance` з явним
   `ProcessorIdentity`) над `RawMessageClaims` + `RawMessageProcessor`; після завершення всі рядки
   Processed, `claimed_by` не NULL, на кожне повідомлення рівно одна ціль, `processing_errors` порожня.
2. `Expired_claims_return_to_pending_and_count_the_attempt`: рядок з `InProgress`, `claimed_at = now − 10 хв`,
   `attempts = 0` → після `ReclaimExpiredAsync` Pending/attempts 1; з `attempts = MaxAttempts − 1` → Failed + помилка.
3. `Processor_skips_rows_claimed_by_another_instance`: claim під ім'ям A, `ProcessAsync` під B → 0, рядок далі InProgress/A.
4. `Two_processing_loops_share_the_backlog`: два справжні `ProcessingLoop` з різними `ProcessorIdentity` над одним DI
   (IndexProvider.Ready виконано через `RefreshAsync`), 40 Pending-повідомлень без NOTIFY → усі Processed, обидва імені
   зустрічаються в `claimed_by` (при 40 повідомленнях і 2 воркерах на інстанс — практично гарантовано; перевірити м'яко:
   `Distinct().Count() >= 1` і жорстко — рівно одна ціль на повідомлення).
Наявні тести викликають `ProcessAsync` на Pending-рядках напряму — лишаються робочими (п. 2).
Фікстуру (контейнер, DI, міграції, seed) винести в `PipelineFixture` як xunit collection fixture: одна БД на всі
інтеграційні тести, класи виконуються послідовно; нові тести перевіряють лише власні рядки (свій префікс `source_message_id`).

## 10. Файли, що зміняться

- `src/Puluj.Domain/Enums/*.cs` (ProcessingStatus.InProgress), `Entities/RawMessage.cs`
- `src/Puluj.Infrastructure/Persistence/Configurations/RawMessageConfiguration.cs`, нова міграція + snapshot
- `src/Puluj.Infrastructure/Ingestion/IRawMessageQueue.cs`, `ReprocessService.cs`, `PulujMetrics.cs`
- `src/Puluj.Processing/ProcessingOptions.cs`, `DependencyInjection.cs`, `Pipeline/ProcessingLoop.cs`,
  `Pipeline/RawMessageProcessor.cs`, нові `Pipeline/RawMessageClaims.cs`, `Pipeline/ProcessorIdentity.cs`,
  `Correlation/TrackWatchdog.cs`; `src/Puluj.Infrastructure/Persistence/AdvisoryLocks.cs` (ключ спільний для процесора і reset)
- `src/Puluj.Worker/Program.cs`, `Hosting/WorkerOptions.cs`, `Hosting/WorkerHeartbeat.cs`, `appsettings*.json` (Serilog shared)
- `deploy/docker-compose.yml`, `scripts/reprocess.sql`, `scripts/reprocess-alerts.sql`
- `docs/README.md` (розділи «Запуск», «Шлях повідомлення», «Експлуатація»), коментарі класів
- `tests/Puluj.Integration.Tests/PipelineTests.cs` (+ новий файл `ParallelProcessingTests.cs` з тією ж фікстурою)

## Ревʼю 1

1. EF `SqlQueryRaw` загортає SQL у підзапит — `UPDATE … RETURNING`/CTE зі зміною даних там заборонені. → п. 2: сирий
   `NpgsqlCommand`, як у `RawMessageIngestor`.
2. `AlertsInUaHandler` робить read-modify-write над `air_alerts` до вставки цілей; start/end однієї тривоги сусідять у
   черзі й без замка дадуть unique violation (source_id, source_alert_id) → зайва помилка і повтор. → advisory-замок
   береться перед першим записом у похідні таблиці, для структурованих повідомлень — перед handler-ом.
3. Запропоноване «чекати count(InProgress)=0» у `ResetAsync` не закриває гонку (воркер, що ще не побачив паузу, може
   зробити claim одразу після підрахунку), а advisory-замок на весь reset дав би дедлок (reset тримає advisory і чекає
   рядок; воркер тримає рядок і чекає advisory). → порядок UPDATE raw_messages → advisory → DELETE в одній транзакції
   (обґрунтування в п. 4); очікування прибрано.
4. Скасування посеред обробки лишало рядок InProgress до кінця лізу і рахувало спробу. → звільнення claim при будь-якому
   скасуванні з `CancellationToken.None`.
5. Serilog file sink уже `shared: true` — пункт про перевірку знято.
6. `Program.cs` формує `appName` з `Name` — має брати `InstanceName`, інакше репліки нерозрізнювані в логах/OTel.
7. Тест 1: `RawMessageProcessor` — singleton з однією ідентичністю; для кількох імен потрібні окремі екземпляри
   (`ActivatorUtilities.CreateInstance` з явним `ProcessorIdentity`). Уточнено.
Правки внесено.

## Ревʼю 2

1. Запит лізу має повертати й `source_id` — він потрібен для `processing_errors`. Виправлено.
2. `PendingBatchSize` стає мертвою опцією (claim по одному) — прибрати з `ProcessingOptions`, `appsettings.json`, docs.
3. `Llm:MaxCallsPerMinute` — обмежувач на процес; з N інстансами сумарний ліміт росте. Не блокер, задокументувати.
4. `AppendHostName` + перестворення контейнерів → нові heartbeat-ключі; панель ховає застарілі, ключі накопичуються в
   `app_settings`. Прийнятно, задокументувати.
5. Перевірено ще раз сценарії дедлоку (воркер X у парсингу, воркер Y у записі, reset, watchdog): порядок «рядок raw →
   advisory → рядки треків/тривог» однаковий у всіх учасників, reset бере advisory після рядків raw, watchdog тримає лише
   advisory. Циклів немає. Перевірено сценарій «reset скинув щойно захоплений рядок»: воркер після замка бачить Pending і
   обробляє його вже після DELETE — це і є перебудова; повторний claim іншим воркером бачить Processed/InProgress → пропуск.
Правки внесено.

## Ревʼю 3

Перевірено: claim-by-id під час reset (блокується до коміту reset і бере вже скинутий рядок), пауза при старті (прапорець
читається до запуску воркерів), спільна БД для двох тестових класів (collection fixture, послідовне виконання,
власні префікси id), `FOR UPDATE` через `ExecuteSql` (Npgsql виконує SELECT як non-query). Зауважень немає — до імплементації.

## Статус

Реалізовано 2026-09-15 за планом (міграція `20260915010735_AddRawMessageClaims`). Інтеграційні тести
(`ParallelProcessingTests`: одночасні claim-и, ліз, чужий claim, два `ProcessingLoop`, шлях помилки під savepoint) і
наявні `PipelineTests` проходять на PostGIS у Testcontainers; unit-тести Processing — без змін.
