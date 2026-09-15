# P06 — llm-worker + finalizer: evidence

Сирі підсумки пише fixture (`MessagingFixture.RecordEvidence`) у [`messaging-crash-evidence.json`](messaging-crash-evidence.json) (ключі `P06-F01…F10`);
TRX — `test-results/p06-*.trx`. Інфраструктура: Testcontainers PostgreSQL 17 + PostGIS, RabbitMQ 4.15.0 quorum queues; LLM — `FakeLlmCompletion`
(без ключа/мережі, та сама таксономія помилок, що в `AnthropicCompletion`). Текст для LLM-гілки — `"Летить щось невідоме, загроза для півдня."`
(правила не дають фактів → `needs_llm`).

| Тест | Сценарій | Що перевірено | Підсумок |
|---|---|---|---|
| F01 | rules → `parse.completed{facts}` → finalizer | один `processing.extractions` (`completed/rules`), 1 observation, `observations.recorded` + `message.analysis.completed` валідні за схемами (expected_branches = `track-worker`), `targets` = 0 (shadow) | ✅ `facts 1, observations 1, targets 0, recorded 1, analysis 1` |
| F02 | `no_facts`, `unsupported` | лише `message.analysis.completed`, без `observations.recorded` | ✅ `observations_recorded 0` |
| F03 | `needs_llm` → llm-worker → finalizer | 1 виклик провайдера, extraction `completed/llm`, audit `llm_requests` з `request_id`, `fencing_token=1`, `run_id`, `provider_request_id`, `outcome=applied` | ✅ |
| F04 | **W8** (worker + finalizer, імітований takeover): провайдер повільний (Gate); поки він відповідає, job-рядок token 2 `succeeded` вставлено як «інший виконавець»; старий отримує відповідь | worker: одразу після виклику бачить token 1 < 2 → job `superseded`, audit `outcome=late`; holder не `running` → receipt `noop`, без другого виклику; finalizer: синтетичний `llm.completed` з token 1 → `noop` (`fencing token 1 < current 2`), stage лишається `awaiting_llm`, extractions 0 | ✅ `audit late, noop, provider_calls 1, extractions 0` |
| F04b | **W8** (реальний takeover): 2 репліки llm-worker (prefetch 1), репліка 1 заблокована у провайдері, lease (7 с) минає; дублікат команди з тим самим `event_id` → репліка 2 чекає lease, бере token 2 через `AcquireLeaseAsync` (token 1 → `interrupted`), теж повільна; відповідь репліки 1 приходить, поки репліка 2 ще `running` | старий результат: audit `late`, job `superseded`, delivery **deferred** (`DeliveryDeferredException` → attempt `superseded`, requeue, без inbox-запису); репліка 2 публікує `llm.completed{fencing_token: 2}` → extraction; redelivery старої → inbox duplicate | ✅ `provider_calls 2, published_token 2, extractions 1` |
| F09 | **W4**: crash consumer'а після відповіді провайдера й audit, до commit (`OnceHooks.BeforeCommit`) | рядок `llm_requests{answered, token 1}` переживає crash; redelivery **чекає lease** (через 3 с — досі 1 виклик), takeover token 2 → другий виклик → `llm.completed{2}` → extraction; token 1 → `interrupted`, audit token 1 лишається `answered` (оплачено, не застосовано) | ✅ `audit_rows 2, orphaned_answered 1, second_call after lease expiry` |
| F10 | terminal без виклику (прямий виклик handler'а): `deadline_at` у минулому; `normalized_text_hash` не збігається | `llm.failed{final}` валідний за схемою, `attempts = 1`, `fencing_token = 1` (власний job-рядок `failed`), провайдер не викликався | ✅ `deadline_exceeded`, `normalization_drift`, `provider_calls 0` |
| F05 | 2 retryable помилки (`provider_timeout`, `provider_error` 503), `Llm:MaxAttempts=2` | job attempts у `processing.attempts` без події між спробами; `llm.failed{final:true, attempts:2}`; extraction `failed`; quarantine 0 | ✅ |
| F06 | 3 дублі `parse.completed` + 2 репліки finalizer | 1 extraction, 3 `noop` receipts, розподіл між репліками | ✅ `extractions 1 (r1 3, r2 1)` |
| F07 | non-retryable (4xx) → `final` після 1 виклику; відмова моделі | `llm.failed{final}` без повторів; refusal → analysis `needs_review`, observations 0 | ✅ |
| F08 | порядок черг: `llm.completed` раніше за `parse.completed{needs_llm}` | 1 extraction; пізній `parse.completed` → `noop` (terminal не понижується до `awaiting_llm`) | ✅ |

## Запуски

| Файл | Результат | Примітка |
|---|---|---|
| `p06-finalizer-run1.trx` | 26 / 4 failed | до правок: F03/F04/F07 текст давав rule-факти; F05 `versions.model` null у схемі |
| `p06-finalizer-run2.trx` | 5 / 3 failed | breaker (`LlmBreaker`) тригерився 429 із F05 і паузував наступні тести на 1 хв |
| `p06-finalizer-run3.trx` | 8 / 0 | після `LlmBreaker.Reset()` у fixture, F05 → timeout+503 |
| `p06-messaging-run4.trx` | 61 / 0 / 1 skipped (P04-C06 W1c) | повний `Puluj.Messaging.Tests` до review |
| `p06-Puluj.Messaging.Contracts.Tests.trx` | 60 / 0 | topology v5, схеми |
| `p06-Puluj.{Integration,Processing,Api,Admin,Analytics}.Tests.trx` | 32/0/1, 105, 30, 56, 33 | регресія до review |
| `p06-finalizer-run5.trx` | 10 / 1 failed | після review-правок: F04b assert на `worker` job-рядка (handler singleton — `Producer` спільний) і пізній результат з тим самим `event_id` впирався в inbox-конфлікт до `ApplyAsync` (audit не `late`); F10 `$ref` схем без ініціалізації `ContractSchemas`. Прогін завис на `StopAsync` (replica заблокована у fake-провайдері після failed assert) — gate тепер звільняється у `finally` |
| `p06-finalizer-run6.trx` | 11 / 0 | fencing-перевірка одразу після виклику в `PrepareAsync`, deferral лише якщо holder `running` |
| `p06-messaging-run7.trx` | **64 / 0 / 1** | повний `Puluj.Messaging.Tests` після правок |
| `p06-Puluj.{Messaging.Contracts,Processing,Integration}.Tests-run2.trx` | 60, 105, 32/0/1 | повторна регресія |

## Що не покрито (свідомо)

- Реальний `AnthropicCompletion` (мережа/ключ) — без інтеграційного тесту; мапінг помилок перевірено кодом (`LlmCompletionException`), unit-тест мапінгу відсутній.
- Quarantine `llm.requested`/`llm.completed` без terminal `llm.failed` — finalizer лишається `awaiting_llm`; safety net — P13/P16 (ADR-0004 §Open).
- Reuse відповіді осиротілого `answered`-audit при takeover (F09: другий оплачений виклик) — можливе покращення, не в P06.
- `Llm:Enabled=true` з реальним ключем у CI не запускається; `AnthropicCompletion` без інтеграційного тесту.
