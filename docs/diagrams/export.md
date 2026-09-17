# Відтворюваний експорт Draw.io у PNG

PNG — похідний файл від `.drawio`. Для кожної зміни схеми запускайте експорт
з кореня репозиторію, а потім перевірку:

```powershell
pwsh -File scripts/docs/export-diagrams.ps1
pwsh -File scripts/docs/verify-docs.ps1
```

Скрипт викликає локально встановлений Draw.io Desktop CLI (`drawio` за
замовчуванням) з незмінними параметрами: PNG, прозоре тло, масштаб 2 і межа
0. Для Windows, якщо команда не є в `PATH`, передайте повний шлях:

```powershell
pwsh -File scripts/docs/export-diagrams.ps1 -DrawioCommand 'C:\Program Files\draw.io\draw.io.exe'
```

Експорт створює PNG поруч із кожним `.drawio` у `docs/diagrams/`. Не
редагуйте PNG вручну; після експорту перегляньте зміни, переконайтеся у
прозорості тла та додайте/оновіть посилання на обидва файли на сторінці й у
[реєстрі](README.md).
