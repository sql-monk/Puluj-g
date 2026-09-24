# Контрольна точка перед перезавантаженням — 2026-09-24

Історична контрольна точка перед перезавантаженням. Користувач повернувся й попросив продовжити; актуальні результати наведені в `ee-map-presentation-plan.md` та PR #79.

## Узгоджений обсяг

Прибрати треки Entity Extractor, зберігши історичні дані; додати піктограми сутностей і впорядкувати ліву панель фільтрів. Не покращувати кореляцію чи геолокацію. Завершити незалежні рев'ю, перевірки та інтеграцію в GitHub main після зеленого CI. Живий стек не розгортався, міграція на робочій БД не запускалася.

## Збережений стан

- Гілка: `codex/ee-map-presentation`.
- Draft PR: https://github.com/sql-monk/Puluj-g/pull/79 (прикріплений до задачі).
- План: `docs/ee-map-presentation-plan.md`.
- Попередні коміти: `a76bf52`, `0761edd`, `80cb3a2`.
- Поточний checkpoint включає останні п'ять UI/E2E виправлень: контраст світлої теми, явну accessible назву SVG textarea, scope alert у detail test, click/assert для видалюваного retired checkbox, перевірку реального перемикача теми та контрасту.
- Незалежні plan/code/post-review завершені без відкритих зауважень. Merge gate ще НЕ пройдено.

## Перевірки та межі

- Frontend unit: 160/160 PASS; lint без помилок (56 попередніх warnings); TypeScript PASS; public/admin builds PASS.
- Python local: 101 PASS / 1 platform skip; Linux CI: 102 PASS.
- Останній CI run `35992475007` на `80cb3a2`: backend PASS (.NET 395 PASS / 1 skip), Python PASS; frontend E2E 76 PASS / 5 FAIL. Ці п'ять збоїв стосувалися селекторів/взаємодії у нових тестах; виправлення включені в checkpoint, потребують повторного CI.
- Піктограми MapLibre проходили CI desktop/mobile; остання локальна вибірка 5 desktop тестів перервана для reboot після 2 PASS, отже не вважати її успішною повністю.
- Focused panel unit: 5/5 PASS. Targeted visual test: PASS; ширини 1280/415/320, light/dark, contrast >=4.5, overflow assertions.
- Шість локальних скриншотів: `.tmp/ee-panel-visual/panel-{1280,415,320}-{light,dark}.png`. Частину перевірили root та UI reviewer. Дані й basemap у visual fixture mocked.
- Ручна перевірка через браузер: desktop/mobile, Escape/focus, теми; backend proxy 5277 був недоступний, тому цей огляд покривав error/empty state, не живі дані.
- Логи: `.tmp/ee-backend-final.log`, `.tmp/ee-python-final.log`, `.tmp/ee-frontend-latest.log`; CI artifact `.tmp/ee-ci-report`.

## Продовження після перезавантаження

1. Перевірити поточні git status/branch/HEAD і стан PR79; не припускати, що CI завершився успішно.
2. Запустити/перевірити CI на checkpoint HEAD. За потреби локально перевірити `EE-admin.e2e.ts`, `PublicEntityIcons.e2e.ts`, виправлений retired checkbox у `PublicShellAudit.e2e.ts`. Не запускати кілька браузерних suite паралельно: host був перевантажений.
3. Усунути підтверджені збої, повторити лише релевантні перевірки; відобразити остаточні результати у плані/PR.
4. Після зеленого CI на точному HEAD зробити PR ready, merge у main без обходу checks, перевірити remote main. Авторизація на GitHub main була надана у початковому запиті.
5. Повідомити користувачу результат і окремо зазначити, що live deployment не виконувався. Для майбутнього deployment дотримуватися drain → migrate → resume з README.

Не використовувати старі session IDs після reboot. Не зупиняти сторонні Docker/VS Code/PowerShell процеси. Локальні Vite та незавершені власні тести зупиняються перед reboot.
