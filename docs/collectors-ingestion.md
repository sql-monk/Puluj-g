# Колектори, ingestion і первинні джерела

## Призначення

Колектори перетворюють зовнішній допис або структуровану відповідь на
`IncomingMessage`, а ingestion зберігає оригінал як `RawMessage`. Нормалізація,
парсинг та доменні події є наступними похідними етапами і не підміняють
первинний payload/text.

Користуйтеся цим описом, коли додаєте джерело або шукаєте, де зникло
повідомлення. Успішний результат цього етапу — raw-повідомлення з джерелом,
стабільним ключем і збереженим оригіналом; поява об'єкта на карті потребує ще
наступних етапів обробки.

## Джерела та шлях даних

У `sources` зберігаються code, тип, URL, trust level, priority, enabled,
source-specific config і secrets. `CollectorSupervisor` бере лише enabled
джерела, упорядковує їх за priority та передає кожному колектору ті, які він
обробляє. Зміна enabled-набору або відстежуваних config/secrets/polling значень,
а також relevant collector options, перезапускає колектори з новою
конфігурацією без перезапуску процесу.

Telegram collector працює для Telegram sources з username у config. Він
зберігає нові й відредаговані channel posts: edit має ту саму key, але окрему
revision, тому оригінал не втрачається. alerts.in.ua полить active alerts і
формує start/end повідомлення зі стабільними ключами `{id}:start` та
`{id}:end`. Для обох шляхів `RawMessageIngestor` зберігає наявні оригінальні
text/payload і URL, рахує latency та будить processor через PostgreSQL `NOTIFY`;
poll Pending рядків покриває втрату notification.

За звичайного шляху `RawMessageIngestor` пише прямо у `raw_messages`. Коли
увімкнено Messaging ingress, collector комітить `ingress.received` та
checkpoint в outbox, після чого `relay` і `raw-writer` мають доставити подію
до raw table. Якщо цих ролей немає, Worker лише попереджає: повідомлення ще
не потрапляють у `raw_messages`.

![Зовнішнє джерело до БД і події](diagrams/ingestion-flow.png)

Редагована схема: [ingestion-flow.drawio](diagrams/ingestion-flow.drawio).

## Стан, retry та відновлення

Кожний collector запускається supervisor-ом у власній task. Помилка одного
колектора не зупиняє інші: supervisor повторює його з exponential backoff від
5 секунд до 5 хвилин; після понад 10 хвилин healthy run backoff скидається.
`collector_states` фіксує polling/success, останній message time/id, cursor,
error (до 2000 символів) і послідовні помилки. Це operational state, а не
заміна raw provenance.

Telegram recent backfill використовує останній source message id. Якщо задано
`BackfillSince`, повна історія з цієї дати читається сторінками, а cursor
лежить у `collector_states`, тож перезапуск продовжує роботу. Під час такого
завантаження processing ставиться на паузу через runtime status; після
завершення сировина може бути заново поставлена у Pending для відтворення
похідних даних у publication order. Flood-wait від Telegram обробляється
очікуванням, якщо API повертає відповідну помилку.

![Життєвий цикл колектора](diagrams/collector-lifecycle.png)

Редагована схема: [collector-lifecycle.drawio](diagrams/collector-lifecycle.drawio).

## Telegram session і секрети

Для Telegram потрібні `ApiId`, `ApiHash` і телефон, а session path за
замовчуванням — `session/puluj.session` (у Compose це том `tgsession`). За
відсутніх або некоректних credentials колектор лишається idle та пише runtime
status, а не намагається працювати з частково налаштованою сесією. Перший код
входу можна подати через admin setting, configuration або тимчасовий файл
`<SessionPath>.code`; код видаляє цей файл після читання, а DB-значення коду
очищає після використання.

У production застосовуйте секрет-сховище/захищені значення середовища або
контрольований admin workflow. Не комітьте `ApiHash`, пароль, verification
code, bearer token, `.session`, `.updates` чи `.code`; не вставляйте їх у
issue, log або документацію. API admin приховує позначені секретні settings,
а source secrets не віддаються браузеру.

![Межі даних і секретів](diagrams/collector-data-boundaries.png)

Редагована схема: [collector-data-boundaries.drawio](diagrams/collector-data-boundaries.drawio).

## Як додавати колектор

Новий колектор реалізує `ICollector`, однозначно визначає, які enabled
`Source` він обробляє, і передає до `CollectorIngress` або
`RawMessageIngestor` первинні поля `IncomingMessage`: source ID, стабільну
message identity, published time, оригінальний text/payload та URL за
наявності. Identity має відрізняти revision від нового поста. Checkpoint треба
комітити разом із повідомленням там, де це підтримує ingress, щоб crash не
перестрибнув непереданий запис.

Не використовуйте content hash як дедуплікацію між різними повідомленнями:
він є індексом схожості. Не перетворюйте source text на нормалізований факт до
збереження raw. Додайте роль Worker, DI-реєстрацію, конфігураційні ключі й
перевірки відповідно до фактично потрібної інтеграції; не оголошуйте підтримку
нового джерела лише через seed row.

## Відомі межі

- `AlertsInUa:BackfillPeriod` описано кодом як разове history load; доступна
  глибина залежить від API, а не гарантується документацією.
- Telegram AutoJoin застосовується лише для public channels, які вдалося
  resolve; неуспішний канал позначається станом помилки та не вимикає інших.
- Trust level — атрибут джерела, не автоматичне підтвердження незалежності
  повідомлень або географічної точності.
