# EntityExtractor: прибрати треки та впорядкувати інтерфейс

## Межі задачі

Вимкнути генерацію та публічний показ сутності `track` у контурі EntityExtractor, зберігши історичні рядки. Додати загальні піктограми вже наявних типів сутностей і переробити ліву панель. Не змінювати геокодування, кореляцію, точність координат чи legacy processor. Не видаляти generic line renderer для інших користувацьких сутностей.

## Аналіз

- Актуальний клієнт: `entity-web`; незалежні EntityApi/EntityAdmin та Python `entity-extractor` працюють з `ee_*`.
- `install.py` повторно вмикає track; одного приховування ліній у клієнті недостатньо.
- Панель змішує перемикачі показу, native multiple-select, період і довідку; URL-фільтри та локальні налаштування мають різну семантику.
- Підтримка SVG є, але відсутні типові піктограми і надійний fallback при помилці завантаження.

## Робочі пакети та власники

1. **WP-0 — координатор + незалежний reviewer:** перевірити цей план до реалізації; зафіксувати й врахувати зауваження.
2. **WP-1 — backend-субагент:** нова атомарна data migration вимикає extractor `track` та його definition/map; installer більше не вмикає track. Зберегти всі таблиці/рядки. Перевірити повторний install, enabled/disabled installation та API-поведінку. Оновити README.
3. **WP-2 — UI-субагент:** компактна ліва панель зі зрозумілими секціями, checkbox-вибором типів/джерел, підписами українською, активними фільтрами, окремими налаштуваннями показу. Зберегти URL, Back/Forward, часовий пояс Kyiv, історичний режим, невідомі значення, reset та mobile focus/Escape/inert.
4. **WP-3 — координатор:** загальний набір піктограм на основі типу сутності; preview/вибір у EntityExtractor; custom SVG має пріоритет. Надійний circle fallback при очікуванні/помилці, захист асинхронного lifecycle; не додавати нової класифікації об'єктів. Клієнт не показує retired tracks навіть зі старим API.
5. **WP-4 — незалежний reviewer:** code review усіх змін та незалежна перевірка регресій. Автори виправляють зауваження; reviewer проводить post-review виправленого diff.
6. **WP-5 — координатор:** браузерна візуальна оцінка desktop/mobile, світла/темна тема, порожній результат, фільтри та піктограми; виправлення видимих дефектів. Зберегти screenshots/evidence і зазначити mocked або real API.
7. **WP-6 — координатор:** task-scoped commit, push, PR у main, реальні GitHub checks; merge після зелених required checks. Перевірити remote main та зв'язати результати з кінцевим commit.

## Перевірки та критерії приймання

- Немає нових EE tracks після застосування міграції; повторна інсталяція не вмикає їх. Історичні записи не видаляються; старий public track detail стає недоступним коректно.
- Треки не потрапляють у карту, її лічильники або список доступних map-фільтрів, навіть якщо старий backend повернув track.
- Користувацькі не-track лінійні сутності зберігають підтримку.
- Піктограми зрозумілі без залежності лише від кольору; невідомий тип має нейтральний fallback; custom SVG не губиться; theme switch не створює зниклих маркерів або необроблених помилок.
- Панель не має горизонтального overflow при 320/415 px, доступна з клавіатури, може згортатися; reset не залишає прихованих фільтрів без пояснення.
- Python focused/full suite; .NET релевантні migration/API тести; frontend lint, unit, build; Playwright desktop/mobile регресії. CI запускає існуючий frontend/backend workflow, результат не підмінюється локальною збіркою.
- Розгортання у працюючі контейнери — окреме від merge; звіт чітко вказує, чи застосовано runtime-зміни. Жодного очищення БД або повторної обробки історії.

## Рев'ю плану

Незалежний reviewer перевірив архітектуру та ownership: блокуючих архітектурних проблем немає. До реалізації внесено зауваження:

1. Вже запущена extraction-транзакція може завершитись зі старими налаштуваннями. Runtime rollout: зупинити приймання/дочекатись завершення EE jobs, застосувати міграцію, відновити обробку та перевірити disabled flags. Критерій відсутності нових tracks діє після цього бар'єра, не від моменту старту міграції.
2. Stale API/URL: захист охоплює definitions, map rows/counts/feed, списки фільтрів і track detail. Збережений track-фільтр не перетворюється непомітно на «всі»: показати явну недоступність і можливість скинути його.
3. Backend agent володіє Python/migration/backend tests/README; UI agent — FilterPanel/DataFilterControls/App/panel components та panel E2E; координатор — presentation registry/admin/entityLayers/MapView/feed/entityFilters та icon E2E. Shared APIs узгоджуються повідомленнями.
4. Зміни Python перевіряються окремим CI job; наявний workflow раніше запускав лише frontend і .NET.
5. Оптимізацію статичних геометрій карти виключено з поточного обсягу; зміни стосуються presentation та вилучення tracks.

## Виконання та докази

- WP-1/WP-2/WP-3 реалізовано у гілці `codex/ee-map-presentation`.
- Незалежне code review виявило P2: старий backend включав retired tracks у catalogue total навіть за відсутності tracks на поточній page. Виправлено: read-side виключає retired definitions, DTO повертає `excludesRetiredTracks`; клієнт не показує непідтверджений total. Додано regression test. Post-review прийняв виправлення; pre-migration fixture узгоджено з API guard.
- Python: **101 passed, 1 skipped** (Windows не підтримує POSIX process-group сценарій); Ruff пройшов.
- Frontend lint: завершився з кодом 0, існуючі warnings залишено без нерелевантного рефакторингу.
- Початковий frontend suite: 148 passed, 4 тайм-аути Intl formatting на навантаженому Windows host. Повторний запуск обмежує кількість workers; це ще не фінальний passing evidence.
- Повторний frontend suite: **160/160 passed** (`--maxWorkers=2 --testTimeout=30000`); TypeScript build пройшов після виправлення missing await у новому E2E. Остаточне незалежне post-review: схвалено, відкритих зауважень немає.
- CI на checkpoint `731f879`: .NET **395 passed, 1 skipped**, Python **102 passed**; frontend unit/build/lint пройшли. Браузерні регресії: **79 passed, 2 failed** (той самий сценарій вибору джерела на desktop/mobile); до merge потрібен зелений повторний запуск. PR: https://github.com/sql-monk/Puluj-g/pull/79.
- Візуальна оцінка: світла/темна тема при 1280/415/320 px, реальний перемикач теми, контраст тексту >=4.5 і відсутність горизонтального overflow. Переглянуті screenshots `.tmp/ee-panel-visual/panel-{1280,415,320}-{light,dark}.png`; дані та basemap у fixture mocked. Виправлено світлий текст на світлому фоні. Окремий ручний браузерний огляд перевірив empty/error state, Escape та повернення focus; живий API під час нього був недоступний.
- Після reboot усунено останній тестовий race: `check()` перевіряв URL-controlled checkbox до асинхронного `hashchange`. Тест тепер перевіряє початковий стан, click → checked + URL, Back → попередній URL + unchecked. Targeted desktop/mobile **2/2 passed**; незалежне post-review схвалило виправлення без зауважень. Фінальний повний CI запускається для PR #79; актуальний результат та SHA зафіксовані у checks і validation описі PR. Merge дозволений лише після успіху всіх jobs на остаточному HEAD.
- Live Docker containers/БД не змінювались; runtime retirement потребує описаного в README rollout.
