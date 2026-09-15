# Контракти шини повідомлень (P01)

Machine-readable частина ADR-0002…0005 ([`docs/adr/`](../../docs/adr/README.md)). Статус: **proposed**;
runtime (P02/P03) читає ці файли, а не власні копії. Зміни — лише разом із тестами
`tests/Puluj.Messaging.Contracts.Tests` і, за потреби, новим `topology_version`.

| Файл | Що це |
|---|---|
| `topology.json` | Registry очікуваних підписок: events (kind, producer, schema, required/optional/conditional subscriptions), subscriptions (bindings, lanes, emits, queue_policy, idempotency, owner_task, status), producer roles, queue policies, bridge на legacy NOTIFY |
| `completion-manifest.json` | Terminal outcomes аналізу й delivery, доменні гілки за категорією observation, workflow stages |
| `schemas/envelope.schema.json` | JSON Schema 2020-12 envelope (plan §5.1) з умовними required за scope події |
| `schemas/common.schema.json` | Спільні `$defs` (observation, location, error, versions, fencing token) |
| `schemas/events/*.schema.json` | Payload кожного event type |
| `asyncapi.yaml` | AsyncAPI 3.0: channels/operations/bindings; **генерується** з `topology.json` |
| `fixtures/valid/*.json` | Повний приклад кожного event type + identity/lane/additive/payload_ref варіанти |
| `fixtures/invalid/*.json` | `{reason, expected_error_contains, event}` — має падати валідацію |
| `fixtures/sequences/*.json` | Republish того самого event_id; analytics out-of-order |
| `fixtures/identity-cases.json` | Legacy `source_message_id` → `(source_message_key, source_revision)` для Telegram/alerts.in.ua |
| `fixtures/compatibility.json` | Правило `major_equal`, матриця compatible/quarantine, breaking examples |
| `tools/gen-asyncapi.py` | Генератор `asyncapi.yaml` з `topology.json` |

## Команди

```powershell
dotnet test tests/Puluj.Messaging.Contracts.Tests/Puluj.Messaging.Contracts.Tests.csproj
python contracts/messaging/tools/gen-asyncapi.py   # після зміни topology.json
```

## Як додати event type

1. `schemas/events/{type}.schema.json` з `$id` `https://puluj.local/contracts/messaging/schemas/events/{type}.schema.json`;
   без `additionalProperties: false` на верхньому рівні (ADR-0003: additive = сумісно).
2. `topology.json.events.{type}`: kind, producer (має бути у `subscriptions` або `producer_roles` з цим `emits`),
   schema, `schema_version: "1.0"`, scope, `replay_source`, required subscriptions (для `replay_source` — `archive`).
3. Envelope: додати тип у `$defs.eventType.enum` і до відповідного `allOf` блоку (message/aggregate scope).
4. `fixtures/valid/{type}.json`; за потреби invalid fixture.
5. Тести: hard-coded перелік у `TopologyRegistryTests.ExpectedEventTypes`/`ExpectedRequired` — оновити свідомо.
6. `topology_version` +1, якщо змінюється набір очікуваних підписок; `python tools/gen-asyncapi.py`.

## Як додати subscription

`topology.json.subscriptions.{id}`: bindings (кожна подія має перелічити її в required/optional/conditional),
lanes, emits (кожна — з `producer` = цей id), `queue_policy` (`required` для required), `idempotency`,
`owner_task`, `status: planned`. Audit/analytics/projection підписки — `emits: []`. Потім `topology_version` +1,
`ExpectedSubscriptions` у тестах, generator.

## Правила сумісності

- `schema_version` `MAJOR.MINOR`: той самий MAJOR — сумісно; інший → `quarantined`, не exception.
- Нове optional поле — MINOR; видалення/перейменування required, зміна типу — MAJOR.
- Envelope-поля додаються лише optional; required набір §5.1 не змінюється без нового ADR.
