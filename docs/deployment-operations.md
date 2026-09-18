# Розгортання, конфігурація та експлуатація

## Топологія

Compose-проєкт `puluj-g` запускає PostGIS, одноразовий `migrate`, два
колектори, рівно один `processor`, API, Admin та Analytics. Колектори
комітять оригінал прямо в PostgreSQL `raw_messages`; processor атомарно
забирає Pending рядки. `Processing__Concurrency=2` задає внутрішній
паралелізм одного процесу. Масштабування processor через deploy чи
Admin не підтримується.

PostgreSQL зберігає дані у зовнішньому томі `puluj-g-pgdata` і доступний з
хоста на `5442`. API має порт `8090`, Admin — `8091`; Analytics порт на хост
не публікує. `logs` — спільний том, `tgsession` доступний лише Telegram
колектору.

Admin монтує Docker socket для restart/stop/start дозволених сервісів. Це
root-еквівалентний доступ до хоста, тому Admin не можна відкривати
назовні без окремого захисту.

## Запуск і оновлення

Інтерактивний майстер:

```powershell
pwsh scripts/deploy.ps1
```

Перша навмисно порожня інсталяція:

```powershell
pwsh scripts/deploy.ps1 -InitializeDatabase -NonInteractive
```

Оновлення наявної БД:

```powershell
pwsh scripts/deploy.ps1 -NonInteractive
```

Скрипт відмовляється непомітно створювати порожній PostgreSQL volume.
`-NoBuild` використовує наявні образи, `-Services` обмежує запуск обраними
сервісами, `-SkipSql` пропускає одноразові SQL-корекції.

`migrate` бере advisory lock, застосовує EF migrations і seeders, після чого
завершується. Інші сервіси стартують лише після його успіху.
Після deploy скрипт перевіряє, що працює рівно один processor, виводить
стан `raw_messages` і опитує API/Admin health endpoints.

## Повний reset

```powershell
pwsh scripts/deploy.ps1 -ResetDatabase -ConfirmReset
```

Це руйнівна операція. Скрипт перевіряє Compose project, контейнер PostGIS і
точний volume до зупинки сервісів. Reset видаляє операційні дані,
Telegram session і логи; потім створює БД заново та виконує звичайний
migration/seed/start flow. Без успішного `migrate` reset не вважається завершеним.

## Конфігурація і діагностика

`app_settings` має пріоритет над `appsettings*.json` і environment для runtime-ключів.
`Runtime:*` — статуси, а не конфігурація. Не додавайте секрети або Telegram
session до Git.

Базові перевірки:

```powershell
docker compose -p puluj-g ps
docker logs puluj-g-processor --tail 200
docker exec puluj-g-postgis-1 psql -U puluj -d puluj -c "SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1"
```

API: <http://localhost:8090/api/health>. Admin: <http://localhost:8091/api/health>.
