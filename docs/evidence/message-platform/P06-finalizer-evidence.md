# P06 — llm-worker + finalizer: evidence

Сирі підсумки пише fixture (`MessagingFixture.RecordEvidence`) у [`messaging-crash-evidence.json`](messaging-crash-evidence.json) (ключі `P06-F01…F08`);
TRX — `test-results/p06-*.trx`. Інфраструктура: Testcontainers PostgreSQL 17 + PostGIS, RabbitMQ 4.15.0 quorum queues; LLM — `FakeLlmCompletion`
(без ключа/мережі, та сама таксономія помилок, що в `AnthropicCompletion`). Текст для LLM-гілки — `"Летить щось невідоме, загроза для півдня."`
(правила не дають фактів → `needs_llm`).

| Тест | Сценарій | Що перевірено | Підсумок |
|---|---|---|---|
| F01 | rules → `parse.completed{facts}` → finalizer | один `processing.extractions` (`completed/rules`), 1 observation, `observations.recorded` + `message.analysis.completed` валідні за схемами (expected_branches = `track-worker`), `targets` = 0 (shadow) | ✅ `facts 1, observations 1, targets 0, recorded 1, analysis 1` |
| F02 | `no_facts`, `unsupported` | лише `message.analysis.completed`, без `observations.recorded` | ✅ `observations_recorded 0` |
| F03 | `needs_llm` → llm-worker → finalizer | 1 виклик провайдера, extraction `completed/llm`, audit `llm_requests` з `request_id`, `fencing_token=1`, `run_id`, `provider_request_id`, `outcome=applied` | ✅ |
| F04 | **W8**: провайдер повільний (Gate), lease минає, «інший виконавець» бере token 2; старий отримує відповідь | worker Apply → receipt `noop`, audit `outcome=late`, job `superseded`; finalizer з `fencing_token` < поточного → `noop`, stage лишається `awaiting_llm`, extractions 0 | ✅ `worker_late_noop 1, audit late, finalizer_late_noop 1, extractions 0` |
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
| `p06-messaging-run4.trx` | 61 / 0 / 1 skipped (P04-C06 W1c) | повний `Puluj.Messaging.Tests` |
| `p06-contracts.trx` | 60 / 0 | topology v5, схеми |
| `p06-integration.trx` / `p06-processing.trx` / `p06-api.trx` / `p06-admin.trx` / `p06-analytics.trx` | 32/0/1, 105, 30, 56, 33 | регресія |

## Що не покрито (свідомо)

- Реальний `AnthropicCompletion` (мережа/ключ) — без інтеграційного тесту; мапінг помилок перевірено кодом (`LlmCompletionException`), unit-тест мапінгу відсутній.
- Quarantine `llm.requested`/`llm.completed` без terminal `llm.failed` — finalizer лишається `awaiting_llm`; safety net — P13/P16 (ADR-0004 §Open).
- Live takeover при **двох живих** репліках worker'а (у F04 takeover імітовано вставкою job-рядка) — покривається логікою lease, не окремим тестом.
