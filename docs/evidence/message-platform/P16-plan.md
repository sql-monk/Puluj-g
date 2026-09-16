# P16 — load/chaos/canary, release gate та виведення legacy

Task: [P16 / issue #15](https://github.com/sql-monk/Puluj-g/issues/15). Вхідний стан: P04–P15 реалізовані; робоча картка переведена у `In Progress`.

Локальна реалізація завершена: незалежний post-review знайшов три P2 (затирання historical evidence filtered run-ом, пропущений `FinalizerTests` у gate та некоректне твердження про scoped canary), усі три виправлені й релевантні тести повторно зелені. Target-environment canary не виконувався з цього workspace і лишається операторським gate.

## Межі результату

P16 не оголошує локальний benchmark production SLO і не запускає production cutover. Вона додає відтворюваний gate на
одноразових PostGIS/RabbitMQ, фіксує виміряні committed outcomes, перевіряє canary ownership і дає оператору безпечну
процедуру canary/cutover/rollback. Production canary виконує лише оператор з доступом до цільового середовища за
`P16-release-runbook.md`; його результати додаються до evidence без raw текстів і credentials.

## Реалізація

1. `P16ReleaseGateTests` працює тільки з Testcontainers `postgis/postgis:17-3.5` і `rabbitmq:4.3-management`:
   - **G01** проводить чотири різні повідомлення через collector ingress, outbox, всі stage/domain/projection workers і
     перевіряє terminal receipts, підтверджений outbox та observation-backed effects. Після canary запускається legacy
     processor; він мусить створити **0** effects для кожного raw. Це доказ, що cutover не має двох writer-ів одного scope.
   - **L01** запускає 1/2/4/8 competing replicas `archive` — незалежної durable гілки — і записує для кожного профілю
     committed outcomes, drain time, throughput та p50/p95/p99 `(completed_at - expected_at)`. Normalizer/parser навмисно
     не стартують у цій пробі: їхній backlog не може бути виданий за completion archive branch.
2. `scripts/test-p16.ps1` — єдина команда release gate. Вона очищає `PULUJ_TEST_CONNECTION` і
   `PULUJ_TEST_ALLOW_RESET`, тож fixture не може випадково `TRUNCATE` dev/live БД. Відсутній Docker — error, не skip.
   Скрипт запускає P16 + crash/domain/replay/lifecycle integration groups, contract suite, PostGIS suite, build і
   Playwright/lint/Vitest. P16 facts пишуться у власний `P16-release-evidence.json`, не перезаписуючи P03–P15
   crash evidence. `-SkipWeb` — явний виняток, який не є completed release gate.
3. `P16-release-runbook.md` описує canary, вимірювання, gate, cutover, rollback і порядок відключення legacy. Скрипт
   `deploy.ps1 -Broker -DomainWriters` уже fence-ить owner-ів: він зупиняє legacy `processor` до старту domain writers;
   data-level guards додатково відхиляють не того owner-а.

## Acceptance matrix

| Вимога | Доказ / команда | Статус для isolated gate |
|---|---|---|
| Raw/outbox/ACK, duplicate, broker restart, missing bindings, DLQ/retry | `CrashTests`, real RabbitMQ/PostGIS | автоматизовано |
| LLM lease/takeover, finalizer, domain races, no double writers | `FinalizerTests`, `DomainWriterTests`, P16 G01 | автоматизовано |
| Replay isolation, catchup, promote/rollback | `ReplayTests`, real RabbitMQ/PostGIS | автоматизовано |
| 1/2/4/8 actual completions | P16 L01 evidence | автоматизовано, SLO затверджує target env |
| UI/admin and API regression | Vitest, Playwright, PostGIS suite | автоматизовано |
| Canary/cutover/rollback | `P16-release-runbook.md` + G01 | rehearsal automated; target-env execution operator-owned |
| HA quorum loss, disk alarm and production-sized DB plan | runbook acceptance sheet | не підмінюється single-node Testcontainers |

## Порядок завершення

1. Запустити `pwsh -File scripts/test-p16.ps1` на чистому checkout і зберегти TRX + JSON evidence.
2. Виконати незалежне post-review diff і запустити релевантні тести повторно після виправлень.
3. Оператор проводить target-environment canary за runbook; відсутність такого доступу фіксується як external gate, а не
   приховується як `passed`.
4. Після виконання acceptance matrix додати P16 handoff, закрити issue та перемістити project card у `Done`.
