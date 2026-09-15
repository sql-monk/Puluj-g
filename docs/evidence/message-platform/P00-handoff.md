# P00 — аудит, concurrency fixes та baseline

Task: [P00 / issue #1](https://github.com/sql-monk/Puluj-g/issues/1).
Status: done — локальна реалізація, тести та незалежне review завершені; rollout не виконувався.
Owner: Codex. Reviewer: незалежний субагент `p00_review`, залучений за дозволом користувача.
Base commit: `3fa6e9145a15ba3f44472d14db63fdf229ce61f5`; результат — локальні незакомічені зміни.

## Результат

- Небезпечний granular lock experiment виключено. Store береться до читання structured interval,
  охоплює candidate-create, dedup, SQL-тригери й commit; парсинг та no-facts працюють паралельно.
- Reset fence-ить всю raw table перед статусами і derived deletes; новий claim/ingest не може
  проскочити між reset і видаленням результатів. Watchdog бере raw read lock до Store.
- Watchdog враховує InProgress history і не створює повторну expiry revision при конкурентних sweeps.
- Text cancellation охоплює всіх нащадків; start після вже обробленого end зберігає закритий інтервал
  із provenance обох повідомлень. Збережено canonical TargetCount semantics.
- Ручні SQL writers приведено до тієї самої дисципліни locks. Скрипти на live/dev БД не запускалися.
- Відсутня PostGIS тепер провалює fixture. External connection дозволяється тільки для `_test`
  із `PULUJ_TEST_ALLOW_RESET=1`; типовий шлях — одноразова Testcontainers PostGIS.

Контракти/міграції: DTO, schema й application flags не змінюються. Нові env тільки для test harness:
`PULUJ_TEST_ALLOW_RESET`, `PULUJ_RUN_BASELINE`, `PULUJ_EVIDENCE_DIRECTORY`.

## План, audit, evidence

- [План, самоперевірка та незалежні review findings](P00-plan.md).
- [Повна карта writers, SQL side effects і відомих меж](P00-writers.md).
- [SQL definitions із тестової БД](P00-sql-inventory.json): 19 migrations, 2 user triggers,
  9 application functions, 85 indexes, 54 constraints.
- [Machine-readable baseline](P00-baseline.json): 42 виміряні прогони, 4 500 committed roots,
  0 errors, 0 retries. Warmup 120 roots не включено до цих чисел.
- [TRX results](test-results/), включно з проміжними невдалими тестами; вони не приховані.
- Source/build hashes: `P00-build-manifest.json`.

## Baseline 1/2/4

Середня швидкість двох прогонів, committed roots/second. Кожен worker має окрему identity
і справжній claim/process loop; воркери працюють у **одному testhost**, із спільними pool і PostGIS.

| Profile | 1 worker | 2 workers | 4 workers | 4 / 1 |
|---|---:|---:|---:|---:|
| Mixed sources/types | 22.81 | 33.12 | 56.75 | 2.49× |
| Single category | 19.86 | 27.45 | 26.59 | 1.34× |
| Structured alerts | 34.78 | 68.29 | 71.81 | 2.06× |
| No facts | 78.58 | 149.55 | 199.42 | 2.54× |
| 25 ms slow-parser stub | 11.70 | 20.93 | 25.03 | 2.14× |
| Live only | 23.15 | 32.78 | 34.17 | 1.48× |
| History + same live | 20.77 | 28.35 | 28.25 | 1.36× |

Ідентичні inputs підтверджені input hashes усередині profile. Live-only — ті самі 30 live roots,
що у history-live; другий профіль додає 90 history roots. Поле artifact `messagesPerRun=120`
позначає звичайний/max розмір; виняток live-only має фактичний `completedRoots=30`.
Для live визначення тут — пізніша event-time lane у фіксованому synthetic input, не live Telegram.

**Serial ceiling лишився:** чотири workers для однієї категорії дають лише 1.34×. Mixed/no-facts
досягли попереднього орієнтира 2× у цьому середовищі; це не production guarantee.
За 4 workers p95 завершення однакових live roots від початку drain виросло з **853 ms до 4 213 ms**
(4.94×) при додаванні history. Орієнтир ≤20% погіршення **не виконано**: legacy oldest-first queue
не має окремого бюджету live/replay. Це виміряний baseline для P02/P05/P09/P14/P16, не blocker
аудиту P00 і не твердження про готовність нової платформи.

## Середовище і межі вимірювання

- Windows host, .NET SDK 10.0.401; PostgreSQL 17.5 + PostGIS 3.5.2, образ `postgis/postgis:17-3.5`.
- Docker engine 29.8.0; 12 доступних engine CPU, 33 460 715 520 bytes RAM. Одна Testcontainers БД
  для всіх прогонів, без окремих container resource caps. Інші сервіси host працювали паралельно.
- Fixed input version `p00-synthetic-v1`, чистий dataset перед кожним прогоном; rules справжні,
  SQL functions/triggers ввімкнені. Slow parser — тільки deterministic async delay 25 ms перед rules.
- Root p95/p99 містить claim+process+postcommit NOTIFY; live backlog p95/p99 починається з drain.
  Stage summaries — наявні p50/p90/max; їх не названо p95/p99. Percentiles доступні в JSON.
- Виміряні testhost CPU/working set і samples pg_stat_activity кожні 20 ms; samples не дорівнюють
  точним lock milliseconds. Pool wait, SQL execution і commit окремо не інструментовано;
  DB CPU/disk I/O, broker wait/disk/cost не атрибутовано. Це явні прогалини для P16.
- RabbitMQ suite не запускалась: RabbitMQ ще не є transport цього checkout, harness — P02.
  Реальні LLM provider limits/cost, 8 workers, production-shaped corpus, multi-process scaling,
  fault injection broker/relay, canary і frontend E2E не входять до виконаних P00 перевірок.

## Перевірки

1. `pwsh scripts/test-p00.ps1 -Baseline` → exit 0; **12 passed / 0 failed / 0 skipped**,
   включно з 42-run baseline. `test-results/p00-final.trx`.
2. `dotnet test Puluj.sln` (під `scripts/with-lock.ps1`, baseline вимкнений) → exit 0;
   Processing **98**, Admin **56**, API **28**, Analytics **33**, Integration **23** passed.
   Разом **238 passed**, **1 explicit skip** (baseline окремо виконано пунктом 1).
3. Після додавання regression tests для maintenance SQL: `dotnet test tests/Puluj.Integration.Tests/Puluj.Integration.Tests.csproj`
   під build mutex → exit 0; **26 passed / 0 failed / 1 explicit baseline skip**.
   `test-results/p00-integration-final.trx`. Разом із незміненими unit suites перевірено 241 окремий
   ненавантажувальний тест. SHA production assembly збігається з baseline: `033a634b7955f59b3e418e108d897d65bb21fee15dbd6f1f963971c263646418`.
4. `git diff --check` → exit 0; повідомлення CRLF є попередженнями Git, не whitespace errors.

Проміжні запуски: перший race suite **8 passed / 1 failed** через помилку fixture source code
`tg_monitor` (виправлено `tg_monitoringwar`); другий **9 passed / 1 failed** через неправильне
очікування TargetCount=12 для duplicate evidence (виправлено 1 canonical / 12 links). Це помилки
нових тестів, не прийняті production failures. Фінальний suite з актуальної збірки пройшов.

## Rollout / rollback / ownership

Deployment, restart сервісів і reset live/dev даних **не виконувалися**. Поточний результат локальний;
до rollout треба включити лише перелічені P00 hunks і пов'язані тести/docs у reviewable commit/PR,
зберігши сторонні незакомічені map/admin/deploy зміни. Manifest перелічує P00 source files.

Оновлювати всі процесори/watchdog/admin writers узгоджено, не змішувати granular і Store збірки.
Перед оновленням зупинити старі processing instances та maintenance jobs; застосувати узгоджену
збірку і запустити їх знову. Schema rollback не потрібний. Відкотити можна до попередньої
**Store-based** збірки зі зупинкою всіх writers; повертати granular patch не можна без P09 доказів.
Legacy reset утримує table fence до кінця операції і тимчасово блокує ingestion/claims — це
свідома ціна коректності, а не новий online replay механізм.

Наступний крок за залежностями: P01 використовує writer map та виміряні обмеження для ADR/контрактів;
потім P02 використовує baseline для прототипу доставки, хоча на дошці P02 стоїть вище P01. P09 мусить прибрати
SQL hot rows з critical path, а P14 — ізолювати replay від live. Автоматично їх не розпочато.

## Завершальне незалежне review

`p00_review`: **Approval, blocking findings відсутні**. Reviewer перевірив code, maintenance scripts,
конкурентні regression tests, final TRX, baseline і відповідність усіх source/assembly hashes manifest.
Усі findings із P00-plan.md виправлені та перевірені. P00 прийнято як audit + безпечний legacy
baseline; це не приймання майбутніх transport/scale/live-replay guarantees.
