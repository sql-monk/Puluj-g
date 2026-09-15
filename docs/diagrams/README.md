# Діаграми

Джерело діаграм — `build.py` (компактний опис у Python → `*.drawio`). Файли `.drawio` відкриваються у
[draw.io / diagrams.net](https://app.diagrams.net) і в VS Code (розширення *Draw.io Integration*); PNG використовуються в markdown.

```
python docs/diagrams/build.py     # перегенерувати *.drawio
node docs/diagrams/export.mjs     # перегенерувати *.png (потрібен пакет playwright з chromium: npm i playwright && npx playwright install chromium)
```

`export.mjs` рендерить через embed.diagrams.net у headless Chromium — desktop-версія draw.io не потрібна. Embed відповідає
лише батьківському вікну, тому скрипт тримає його в iframe на службовій сторінці. Змінна `DIAGRAMS_DIR` дозволяє запускати
скрипт з іншої теки (де встановлено playwright).
Якщо діаграму відредаговано вручну у draw.io, перенесіть зміни в `build.py`, інакше наступний `build.py` їх перезапише.

| Файл | Зміст |
|---|---|
| `01-overview` | компоненти і потік даних |
| `02-pipeline` | шлях повідомлення від RawMessage до NOTIFY |
| `03-data-model` | таблиці та provenance chain |
| `04-correlation` | дублікат / приєднати / новий трек, закриття |
| `05-realtime-history` | live-події та історичний режим |
| `06-code-classes` | ключові класи й межі проєктів у коді |
| `07-deployment` | Docker-оточення, порти та перший запуск порожньої БД |
