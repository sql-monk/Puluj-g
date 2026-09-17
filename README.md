# Puluj-G

## Quick Start: Docker

З кореня репозиторію для **першої навмисно порожньої інсталяції** виконайте:

```powershell
pwsh scripts/deploy.ps1 -InitializeDatabase
```

Команда створює зовнішній том PostgreSQL `puluj-g-pgdata`, збирає Compose-стек,
чекає одноразову роль `migrate` і перевіряє доступність сервісів. Після успіху
відкрийте публічну карту: <http://localhost:8090>; admin-панель:
<http://localhost:8091>. Перевірки доступності: <http://localhost:8090/api/health>
та <http://localhost:8091/api/health>.

Для **наступного запуску або оновлення наявної БД** не створюйте новий том:

```powershell
pwsh scripts/deploy.ps1
```

Скрипт відмовиться непомітно ініціалізувати порожню БД. Якщо наявний том має
іншу назву, передайте її явно через `-DatabaseVolume <name>`. Перед оновленням
із ризиком для даних підготуйте перевірену резервну копію. Деталі про ролі,
конфігурацію, broker-профіль і діагностику — у
[посібнику з розгортання та експлуатації](docs/deployment-operations.md).

## Що таке Puluj-G

Puluj-G збирає повідомлення з первинних джерел, зберігає їхнє походження,
обробляє їх у tracks та incidents і показує перевірний read-side на публічній
мапі. Адміністративний контур дає операторам інструменти для контролю джерел,
налаштувань, черг, runs і review.

Система допомагає перетворити потік повідомлень на керовані, простежувані
спостереження; вона не встановлює істину, точну геолокацію або
причинно-наслідковий зв'язок. Координати, precision, confidence і зв'язки є
даними report/policy, а не гарантіями. Від публічного об'єкта до raw message
зберігаються source, revision, правила/версії та decision reason, щоб висновок
можна було перевірити без домислювання відсутніх фактів.

## Як це працює

Первинні джерела надходять через колектори до PostgreSQL як незмінні raw
повідомлення. Далі legacy processor або, у broker-режимі, durable event stages
нормалізують і розбирають дані; доменні writers створюють tracks, alerts та
incidents з provenance. API віддає read-side і realtime-сповіщення публічній
карті, а admin і analytics працюють з операційними та похідними даними.

![Огляд системи Puluj-G](docs/diagrams/system-overview.png)

Редагована схема: [system-overview.drawio](docs/diagrams/system-overview.drawio).
Деталі: [колектори й ingestion](docs/collectors-ingestion.md),
[платформа обробки](docs/message-processing-platform.md),
[кореляція та provenance](docs/correlation-tracks-incidents.md),
[public API/read-side](docs/public-api-read-side.md),
[admin-процедури](docs/admin-operations.md) і
[аналітичний сервіс](docs/analytics-service.md).

## Документація за ролями

| Для кого | З чого почати |
| --- | --- |
| Користувач | [Публічна карта й приватність](docs/web-clients.md), [public API та evidence](docs/public-api-read-side.md) |
| Розробник | [Доменна модель і дані](docs/domain-data-model.md), [колектори](docs/collectors-ingestion.md), [обробка повідомлень](docs/message-processing-platform.md), [web-клієнти](docs/web-clients.md) |
| Оператор | [Розгортання й конфігурація](docs/deployment-operations.md), [admin-контур](docs/admin-operations.md), [analytics](docs/analytics-service.md) |
| Інтегратор | [Public API, realtime і контракти](docs/public-api-read-side.md), [provenance треків та incidents](docs/correlation-tracks-incidents.md) |

Повний навігаційний індекс, правила документації та реєстр схем — у
[docs/README.md](docs/README.md).
