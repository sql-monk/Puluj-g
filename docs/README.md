# Документація Puluj-g

Це стартовий каркас нового набору документації. Він не описує поточні
компоненти: такі сторінки додаватимуться наступними D-задачами за спільними
правилами нижче.

## Навігація

- [Розгортання, конфігурація та експлуатація](deployment-operations.md)
- [Доменна модель, PostgreSQL/PostGIS і довідники](domain-data-model.md)
- [Колектори, ingestion і первинні джерела](collectors-ingestion.md)
- [Обробка повідомлень і платформа подій](message-processing-platform.md)
- [Кореляція, треки, інциденти та provenance](correlation-tracks-incidents.md)
- [Публічний API, realtime та read-side](public-api-read-side.md)
- [Адміністративний контур і операційні процедури](admin-operations.md)
- [Аналітичний сервіс і життєвий цикл повідомлень](analytics-service.md)
- [Web-клієнти: публічна карта і адмін-панель](web-clients.md)
- [Правила іменування та розміщення](naming.md)
- [Шаблон сторінки компонента](templates/component.md)
- [Реєстр діаграм](diagrams/README.md)
- [Відтворюваний експорт PNG](diagrams/export.md)
- [Перевірка документації](../scripts/docs/verify-docs.ps1)
- [Архів попереднього набору](../.arch/manifest.md)

## Межі активної документації

Активна наративна документація живе в `docs/`. Дані контрактів, fixtures,
seed-інструкції й докази тестування залишаються робочими артефактами у своїх
каталогах; їхня класифікація наведена в [manifest архіву](../.arch/manifest.md).
Валідатор перевіряє активні сторінки та діаграми, але виключає
`docs/evidence/` і `docs/fixtures/` саме як такі артефакти.

Перед комітом документаційних змін запустіть:

```powershell
pwsh -File scripts/docs/verify-docs.ps1
```
