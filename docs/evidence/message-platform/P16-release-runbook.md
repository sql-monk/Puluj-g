# P16 release runbook — canary, cutover і rollback

Ця процедура призначена для оператора тестового або production-like середовища. Не вставляйте в evidence raw
повідомлення, URL з токенами, connection strings чи secrets.

## 1. Передумови і стоп-умови

- Збережіть commit/image digest, Compose profile, RabbitMQ/PostGIS версії, CPU/RAM/disk limits і UTC час старту.
- Підготуйте окреме одноразове тестове середовище для load/chaos. `PULUJ_TEST_CONNECTION` дозволений тільки для БД з
  суфіксом `_test` разом із `PULUJ_TEST_ALLOW_RESET=1`; release gate за замовчуванням використовує Testcontainers.
- До canary мають бути зелені `scripts/test-p16.ps1`, міграції additive, topology hash узгоджений, а dashboard показує
  health PostGIS/RabbitMQ. Якщо будь-яка required subscription має pending/quarantined/unconfirmed delivery — **стоп**.
- Не запускайте `ReprocessService.ResetAsync` або legacy `processing` у scope, де вже є platform domain writer.

## 2. Baseline та load/chaos

1. На однакових CPU/RAM/DB limits запустіть baseline і candidate на однаковому зафіксованому наборі повідомлень.
   Окремо: mixed, one-category, alerts, no-facts, LLM-heavy (детермінований stub окремо від реального provider limit),
   history + live.
2. Для 1/2/4/8 worker replicas запишіть: committed jobs/s, completed roots/s, backlog drain time, wait/processing/
   end-to-end p95/p99, SQL locks/pool wait, CPU/RAM/I/O, broker disk та cost. Retries і duplicate deliveries — окремі
   лічильники; вони не додаються до throughput.
3. Перевірте: collector crash до/після checkpoint; relay crash до/після confirm; consumer crash до commit і після commit
   до ACK; delayed confirm; DB/broker outage; missing binding/unroutable; DLQ retry; offline subscription; LLM lease
   takeover. Після кожного сценарію звірте root→delivery→effect і reconciliation.
4. Для quorum profile перевірте три fault domains, loss of quorum та disk alarm. Single-node Testcontainers не є доказом
   цих трьох пунктів.

## 3. Canary (платформа поруч із legacy)

1. Розгорніть additive migrations і профіль `broker`, але **без** `-DomainWriters`:
   `pwsh scripts/deploy.ps1 -Broker`.
2. `-Broker` є **глобальним** profile перемикачем для увімкнених collectors; topology registry описує subscriptions і
   lanes, але не є source/run canary-control. Не оголошуйте canary обмеженим лише зміною registry. Для обмеженого
   canary використовуйте окреме isolated environment або окремий collector/source, вимкніть усі інші collectors і
   зафіксуйте owner, interval та rollback owner поза registry. Переконайтеся, що collector ingress/outbox активні,
   deliveries створені до producer, а legacy `processor` виключений з broker-owned scope.
3. Надішліть контрольну canary вибірку. Для кожного root звірте archive, receipts, raw identity/revision, expected set,
   no duplicate effects, dashboard latency і source link. G01 у `P16ReleaseGateTests` є відтворюваною мінімальною
   репетицією цієї перевірки.
4. Gate: 4 workers мають дати не менше 2× throughput 1 worker до saturation для незалежного CPU/DB workload; live p95
   під simultaneous replay не гірше 20 % від live-only. Якщо gate не пройдено — зберегти числа й залишити scope на legacy;
   не «компенсувати» проблему додаванням queue replicas.

## 4. Cutover і перевірка ownership

1. Зафіксуйте checkpoint/reconciliation (legacy Pending проти new jobs: без gaps і overlap), дочекайтесь terminal
   deliveries і зафіксуйте watermark.
2. Запустіть `pwsh scripts/deploy.ps1 -Broker -DomainWriters`. Скрипт спершу зупиняє `processor`, після чого стартує
   `track-worker,alert-worker,watchdog,incident-worker`; він відмовляється, якщо processor лишився. Перевірте role list,
   що processor containers = 0, і SQL counts `targets_by_writers` / `targets_by_legacy`.
3. Перевірте live map/API, admin queue dashboard, source links, Alert/Track/Incident outcomes і bounded payloads. Replay
   має власну lane/generation та не створює production map notifications, alert effects чи live KPI updates.

## 5. Rollback і legacy retirement

- При alert/error fence новий writer: зупинити domain-writer roles, не запускати одночасно legacy processor, зберегти
  receipts/outbox/quarantine evidence. Повернутися до legacy через `pwsh scripts/deploy.ps1 -Broker` тільки після
  перевірки input ownership і reconciliation.
- Старий `ProcessingLoop`, `ReprocessService.ResetAsync` та copy-analytics не видаляються автоматично. Видалення дозволене
  лише після успішного canary window, backup/restore rehearsal, empty legacy-owned backlog, відсутнього incoming scope,
  code search/CI без legacy consumer, та схваленого rollback plan. Позначте subscription `retired` із audit reason; не
  робіть silent delete.
- Handoff має містити commit/image, environment, commands+exit codes, test counts, metric table, chaos results,
  unresolved skips, ownership, canary window, decision та rollback checkpoint.
