# P06 — план виконання і review

Issue: https://github.com/sql-monk/Puluj-g/issues/8. Початок: 2026-09-15. Base: `991b95d` (master, P05 закомічено, дерево чисте).
Залежність P05 — done (стадії normalizer/parser, `parse.completed{needs_llm}` + `llm.requested`, `stage_results`, `FactMapper`, topology v4 з paused finalizer/llm-worker).
Виконавець: Claude Code (Opus 5); незалежний reviewer: субагент `p06_review` (план → результат). Результат комітиться.

## Аналіз задачі

Хвиля 4: LLM worker (`llm.requested` → `llm.completed | llm.failed`) з lease/fencing, budget/rate limits і повним request audit; extraction finalizer
(`parse.completed` + `llm.*` → **рівно один** канонічний immutable extraction на (raw, run) → `observations.recorded` + `message.analysis.completed`).
Критерії issue: crash/late-result/fencing tests; один канонічний extraction на run; повна provenance запитів.

Що є: `LlmParser` (Anthropic client, system prompt + JSON schema, `MapJson` → `ParsedFact`, audit у `llm_requests`, `LlmBreaker`, rate limiter) —
працює всередині legacy `RawMessageProcessor`; `llm_requests` (worker, model, prompt, outcome, usage, cost, тексти) без `request_id`/`fencing_token`;
`processing.attempts` з `job_key`, `fencing_token`, `lease_until` (незаповнені); контракти `llm.completed` (request_id, fencing_token, outcome
facts|no_facts|needs_review, facts[], usage, duration), `llm.failed` (error, attempts, final), `observations.recorded` (extraction_result_id, extraction_version,
observations[] з observation_id/legacy_target_id, expected_branches), `message.analysis.completed` (outcome, method, fact_count, expected_branches, versions,
timings, error, llm_request_ids); `completion-manifest.json` (домен-гілки за категорією; analysis outcomes). Topology v4: `finalizer`/`llm-worker` paused з backlog.

Що P06 **не** робить: track/alert/incident workers (P09/P10) — `observations.recorded` отримує лише archive; **не пише `targets`** — legacy loop
лишається їх єдиним писарем до cutover (P09/P14): канонічні факти живуть у нових таблицях `processing.extractions`/`processing.observations`,
`legacy_target_id` порожній (compat window, §8.1); DB-driven rules (P08); analytics проєкції (P15); UI (P13).

## Рішення, прийняті планом

| # | Рішення | Мотив / межі |
|---|---|---|
| D1 | **Схема (міграція `AddExtractions`, additive)**: `processing.extractions` (extraction_id uuid PK, raw_message_id, run_id, extraction_version int, method, outcome, versions jsonb, facts jsonb, llm_request_ids uuid[], finalized_by, created_at; **unique (raw_message_id, run_id)**, без updates — immutable), `processing.observations` (observation_id uuid PK, extraction_id FK, raw_message_id, run_id, event_kind_code, category, effective_at, payload jsonb, legacy_target_id null); `llm_requests` +`request_id uuid null`, +`fencing_token int null`, +`run_id uuid null` | Issue: «рівно один канонічний extraction на run» — unique + `ON CONFLICT DO NOTHING`; ADR-0006 `outputs.extraction_result_id`; audit provenance |
| D2 | **`ILlmCompletion`** (Processing/Llm): `Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest, ct)` — `AnthropicCompletion` витягнутий з `LlmParser.AskAsync` (той самий system prompt/schema через internal accessors; `provider_request_id` з відповіді, usage, refusal); `LlmParser` без змін поведінки (legacy loop). Тести — `FakeLlmCompletion` (канонічні відповіді, timeout, refusal) | Реальний API не викликається в тестах; провайдер підміняється |
| D3 | **`LlmWorkerHandler`** (підписка `llm-worker`): `PrepareAsync` — lease у `processing.attempts` (job_key `llm:{request_id}`): `INSERT` нової спроби з `fencing_token = max+1`, `lease_until = now + Llm timeout×2` лише якщо немає живого lease (інакше transient → backoff, доставка повернеться); budget: `LlmBreaker` open / rate limit → transient (backoff, без оплати); deadline з `llm.requested.deadline_at` минув → `llm.failed{final:false → після MaxAttempts final}`; виклик провайдера поза tx; помилка провайдера (429/5xx/timeout) → attempt `failed` + `llm.failed{retryable, attempts, final = attempts ≥ Llm:MaxAttempts}`; `ApplyAsync` — re-check «мій token = max для job» (інакше `noop` late result + audit outcome `late`), `llm_requests` audit (request_id, fencing_token, run_id, usage, cost, тексти, provider_request_id), attempt `succeeded`, outbox `llm.completed{fencing_token, facts через MapJson → TargetBuilder → FactMapper, usage, duration}` або `llm.failed` | ADR-0004 §6 п.6, W8; ADR-0005 |
| D4 | **`FinalizerHandler`** (підписка `finalizer`), state machine на (raw, run): `parse.completed{facts|no_facts|unsupported|failed}` → фіналізація одразу (method rules/structured/none); `parse.completed{needs_llm}` → stage `finalize` outcome `awaiting_llm` (stage_results, оновлюваний стан); `llm.completed` → фіналізація (method `llm`; outcome `needs_review` → analysis `needs_review`); `llm.failed{final}` → analysis `failed` з error; `llm.failed{!final}` → лишаємось awaiting. **Fencing**: `llm.*` з `fencing_token` < max token job_key у `attempts` → `noop` «late result» (receipt). Порядок черг довільний: `llm.completed` до `parse.completed{needs_llm}` → фіналізуємо; пізній `parse.completed` → extraction уже є → `noop`. Фіналізація в `ApplyAsync`: `INSERT extractions ON CONFLICT (raw, run) DO NOTHING` (RETURNING → інакше noop), `observations` (observation_id UUIDv7 на факт), stage `finalize` → outcome, outbox `observations.recorded` (лише completed з фактами; `expected_branches` за manifest із категорій) + `message.analysis.completed` (завжди; `timings` з raw/stage_results; `llm_request_ids`) | ADR-0005 finalizer state machine; один extraction; «no_facts/failed теж видимі» |
| D5 | `legacy_target_id` не заповнюється (стадії не пишуть `targets`; зв'язок — P09/P14 при cutover); `expected_branches` записуються, але track/alert/incident — planned (deliveries лише archive) | §16.2 п.5; compat window |
| D6 | Topology v5: `finalizer`, `llm-worker` → `active`; Worker ролі `finalizer`, `llm-worker`; Compose `messaging` +ролі; `Llm:MaxAttempts` (default 3), `Llm:LeaseSeconds` | ADR-0002 |
| D7 | Тести (MessagingFixture + fake completion): F01 rules→finalize (extraction 1, observations, обидві події валідні, `targets` 0), F02 no_facts/unsupported (лише analysis.completed), F03 needs_llm → llm-worker → llm.completed → finalize llm (audit row з request_id/fencing/usage; порядок: llm.completed до parse.completed → 1 extraction, пізній parse → noop), F04 W8 fencing (lease expired → takeover token 2; пізній результат token 1 → finalizer noop; worker Apply з застарілим token → noop+audit late), F05 llm.failed non-final → awaiting; final → analysis failed; F06 duplicate parse.completed (2 event_id) → 1 extraction/1 analysis; F07 provider timeout/429 → llm.failed retryable, attempts, final після MaxAttempts; unit: payload'и проти schema (у integration), lease SQL | Issue: crash/late-result/fencing; один extraction |
| D8 | Docs: ADR-0004 W8 → P06 tests, ADR-0005 (finalizer реалізовано, `analyzed` stage), ADR-0006 (+extractions/observations, llm_requests), ADR-0002 v5, README контрактів «Runtime (P06)», fork-deployment, plan §17; evidence P06; commit | — |

## Кроки

1. Статус (issue #8 in_progress, §17). 2. Review плану → правки. 3. Міграція/entities; `ILlmCompletion` + `AnthropicCompletion` + accessors у `LlmParser`;
`LlmWorkerHandler`; `FinalizerHandler`; DI/ролі/topology v5/Compose. 4. Тести F01–F07; контрактні; повні suites; build; compose. 5. Docs/evidence/review
результату → правки → повторний прогін; handoff; issue; commit.

## Незалежне review плану — p06_review

Вердикт: **умовно прийнято (approve after fixes)**. Blocking → правки D3 внесено до старту:

| # | Finding | Правка |
|---|---|---|
| B1 | Transient-через-throw при живому lease / rate limit / breaker спалює `max_delivery_attempts` (5) → `llm.requested` у quarantine, finalizer навічно awaiting | Живий чужий lease → **чекати** до `lease_until` у `PrepareAsync` (bounded ≤ lease), не throw; rate limit → `AcquireAsync` у межах deadline; breaker open → terminal `llm.failed{final:true, code=budget_unavailable}`; job-рівень `Llm:MaxAttempts` (3) за `job_key` — handler комітить `llm.failed{final:true}` раніше, ніж consumer дійде до 5; safety net: quarantine `llm.requested`/`llm.completed` → finalizer `failed` через reconciliation — відкрите для P13/P16 (зафіксувати) |
| B2 | `llm.failed{final:false}` з commit = без повтору; deadline минув → non-final зависає | Retryable помилка провайдера → attempt job `failed` (autocommit) + throw transient (без події) до `Llm:MaxAttempts`, далі `llm.failed{final:true}`; `final:false` не публікується; deadline минув → `final:true, retryable:false, code=deadline_exceeded` |
| B3 | Takeover `max+1` без unique → дві репліки з тим самим token; lease-рядки з `subscription_id`/`event_id` consumer'а подвоюють `CountAttemptsAsync` і потрапляють під `interrupted` | Lease у короткій tx під `pg_advisory_xact_lock(hashtext(job_key))`; partial unique `(job_key, fencing_token) WHERE fencing_token > 0`; job-рядки з `subscription_id = 'llm-worker:job'` (consumer рахує лише свій `subscription_id`) |
| B4 | Audit оплаченого виклику в `ApplyAsync` губиться при W4/W12a | Audit autocommit одразу після відповіді провайдера (`request_id`, `fencing_token`, `run_id`, `attempt_id`, `provider_request_id`, usage, cost, тексти); `ApplyAsync` лише оновлює `outcome` (`applied | late | failed`) |

Non-blocking прийняті: N1 (budget = rate limiter + breaker на репліку + `budget.max_output_tokens` з запиту; `max_cost_usd`/денний cap — межа, owner ADR-0007/P16),
N2 (worker завантажує raw, нормалізує, звіряє `normalized_text_hash`/version → drift = `llm.failed{final, normalization_drift}`), N3 (stage `finalize` з guard
`awaiting_llm → terminal`, terminal не понижується; перевірка extraction у тій самій tx; `extraction_version` = 1 на run; ADR-0006/topology idempotency оновити),
N4 (`llm.failed{final}` token 1 → потім takeover token 2 з фактами → extraction `failed` immutable, результат noop — задокументувати + audit `late`), N5
(`targets` не пишемо — compat projection §8.1; для planned гілок `domain_completed` не настане у compat window — ADR-0005/0006), N6
(`Messaging:Consumer:PrefetchBySubscription`, llm-worker 2; `Llm:LeaseSeconds` = джерело lease), N7 (`LlmCompletionException{StatusCode, Retryable}` спільний для
Anthropic і fake; F08 W4-crash після відповіді → audit є, другого платного виклику до expiry немає; F09 дві репліки finalizer → 1 extraction), N8 (індекси,
`versions.model/prompt` з payload `llm.completed`).

