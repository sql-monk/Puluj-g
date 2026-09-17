# ADR-0005 — Runs, generations, lanes і completion semantics

Статус: **accepted** (P14 — orchestration: state machine, replay runs з checkpoint, delta catchup, verify/promote/rollback, shadow-гілка incident-worker; див. «Orchestration (P14)» нижче). Історія: proposed (P01); частково реалізовано P03 — receipts `processing.deliveries` з terminal `completed/noop/quarantined/waived`
(`SubscriptionConsumer`, `DlqConsumer`, `SubscriptionAdmin.WaiveAsync`) і мінімальні `processing.runs` (один відкритий run на lane
`live`/`history`, `ProcessingRuns`); P05 — стадії `normalize`/`parse` у `processing.stage_results` (unique `(raw, run, stage, stage_version)`,
повтор → `noop`) і outcomes parser'а `facts | no_facts | unsupported | needs_llm | failed` у `parse.completed`; P06 — **finalizer** (`FinalizerHandler`):
state machine на (raw, run) з terminal outcomes `completed | no_facts | unsupported | needs_review | failed`, канонічний extraction у
`processing.extractions` (unique (raw, run), immutable) + `processing.observations`, `message.analysis.completed` для кожного outcome,
`observations.recorded` лише для `completed`; **llm-worker** (`LlmWorkerHandler`) з lease/fencing (ADR-0004 W8). Workflow stage `analyzed` тепер
задовольняється; `domain_completed` настає з P09/P10: гілки `track-worker`/`alert-worker`/`incident-worker` active (`info` — track-worker як fact writer). Generation до P14 —
`run.generation_id` або стала live generation `UUIDv5(generation:live)` (`processing.generations` on demand, ADR-0010 п.8). Machine-readable: [`completion-manifest.json`](../../contracts/messaging/completion-manifest.json).
Вимоги: plan §4, §5.2, §11, §15.1 «Completion semantics», §15.2. Orchestration/generations/state machine — P14.

## Контекст

Сьогодні `RawMessage.ProcessingStatus` — один сумарний стан на весь pipeline; reset очищує похідні дані
глобально. Потрібні: незалежні стани етапів, версіоновані повторні обчислення без global reset, явне
визначення «оброблено» як завершення обов'язкових бізнес-гілок (не ACK транспорту), окремі стани для
UI/аналітики: «оригінал збережено», «розібрано», «доменно оброблено», «аналітика наздоганяє», помилки.

## Рішення

### Run і generation

- **Run** (`processing.runs.run_id`, UUIDv7) — конкретний запуск обробки з фіксованими версіями
  (`pipeline_version`, `normalization`, `rules/ruleset_id`, `model/prompt`, `catalog_policy`), lane, scope
  (джерела, часовий інтервал, етапи) і checkpoint. Кожна подія несе `processing_run_id`.
- **Live run** — один активний на lane `live` (довгоживучий; нова збірка/версія правил = новий live run
  з `supersedes_run_id`). **History run** — backfill джерела в lane `history` (ті самі версії, що live).
  **Replay run** — повторний розрахунок збережених raw у lane `replay` у **нову generation**.
- **Generation** (`generation_id`) — набір доменних результатів (tracks/alerts/incidents/projections), який
  можна зробити активним. Live і history пишуть в активну generation; replay — в ізольовану; promote
  атомарно перемикає `active_generation` pointer; rollback повертає попередній.
- Результати старих runs **не перезаписуються**: `processing.stage_results` unique
  `(raw_message_id, run_id, stage, stage_version)`; replay створює нові рядки з новим run.

State machine run: `created → running → paused → running → verified → promoted | rolled_back | cancelled`;
`failed` з будь-якого активного стану. Terminal для history run — `completed` (scope вичерпано);
для live run — `superseded` (новий live run із `supersedes_run_id`); `verified/promoted/rolled_back` — лише replay. Promote дозволений лише з `verified` (delta catchup до watermark,
звірка counts). Shadow (replay) run не публікує в `projection` live і не надсилає сповіщень (ADR-0002: без
replay lane у projection).

### Lanes і quotas

`lane ∈ live | history | replay` — частина routing key, назви черги і envelope. Окремі черги не дають
ресурсного бюджету самі по собі (P00: live p95 ×4.94 при history): quotas потрібні на рівнях черги
(concurrency/prefetch), DB pool, CPU, LLM budget — ADR-0007.

### Analysis outcomes (terminal стадії extraction)

`completed | no_facts | unsupported | needs_review | failed` — усі terminal; `message.analysis.completed`
публікується для **кожного**. `observations.recorded` — лише для `completed`. Finalizer state machine:
`rules sufficient → completed`; `fallback required → awaiting_llm → completed | failed | needs_review`;
проміжні результати — `processing.attempts`, пізній LLM результат — за `fencing_token` (ADR-0004 W8).

**Fallback (P05):** коли правила нічого не знайшли, а текст схожий на звіт про ціль (`LlmParser.LooksLikeTargetReport`), модель увімкнена,
lane `live` і пост свіжий (`Llm:MaxMessageAgeHours`), parser публікує **обидві** події: факт спроби `parse.completed{outcome: needs_llm,
fallback_reason}` (finalizer бачить, що правила відпрацювали, і переходить у `awaiting_llm`) і команду `llm.requested` (`request_id`,
`fencing_token: 1`, `deadline_at`, `input.normalized_text_hash`, `rules_context.attempt_id`). Інакше — `no_facts` з `fallback_reason`
`llm_disabled | llm_skipped_lane | llm_skipped_stale` (або без причини, якщо текст не схожий на звіт). Формулювання §4 «parse.completed або
llm.requested» читається як «команда додатково до факту спроби». Lease/attempt LLM-job створює llm-worker (P06): job-рядки `processing.attempts` (`subscription_id = llm-worker:job`, `job_key = llm:{request_id}`, `fencing_token`
1..n, `lease_until`), takeover після закінчення lease, `llm.failed{final:true}` після `Llm:MaxAttempts` або одразу для non-retryable/deadline/breaker/drift;
`final:false` не публікується. Finalizer: `llm.completed{needs_review}` (відмова моделі) → analysis `needs_review` без observations; `llm.failed{final}` →
`failed` з error; пізній результат зі старим `fencing_token` → `noop`; після `llm.failed{final}` пізніший takeover з фактами — теж `noop` (extraction
immutable; оплачений виклик видно в `llm_requests.outcome = late`). `versions` extraction для LLM-шляху = `versions` стадії `awaiting_llm`
(normalization/rules) + `model`/`prompt` з `llm.completed`; рядки `processing.observations` — лише для `completed`.

### Completion manifest

Для кожного raw у run очікуваний набір deliveries = required підписки кожної опублікованої події
(за `topology_version`) + доменні гілки за категоріями observations:

| Категорія observation | Гілка |
|---|---|
| `target` | `track-worker` |
| `alert` | `alert-worker` |
| `incident` | `incident-worker` |
| `info` | — (feed/analytics, без доменного власника) |

`expected_branches` записується в payload `observations.recorded` і `message.analysis.completed`, щоб
монітор не залежав від повторного обчислення. Fan-out **не** означає очікування кожного типу воркера
для кожного поста.

### Terminal outcomes delivery

`completed` — ефект застосовано; `noop` — consumer перевірив і свідомо нічого не змінив (не його scope,
stale revision) — receipt обов'язковий; `quarantined` — retries вичерпано/schema несумісна — **не** успіх;
`waived` — audited waiver зупиненої підписки з причиною. Receipt пишеться в транзакції результату
(`processing.deliveries`), стан до ACK може бути committed.

### Workflow stages (для UI/analytics)

| Стан | Умова |
|---|---|
| `stored` | receipt/подія `raw.stored` |
| `analyzed` | `message.analysis.completed` (будь-який outcome) |
| `domain_completed` | усі гілки з `expected_branches` мають terminal receipt `completed|noop|waived` |
| `analytics_caught_up` | `message-analytics` має receipts для `raw.stored` і `message.analysis.completed` (P15: рядок `analytics.message_lifecycle` з `analyzed_at`; ADR-0013) |
| `needs_attention` | будь-який `quarantined`, або outcome `needs_review|failed`, або expected без receipt довше SLO |

`RawMessage.ProcessingStatus` лишається compatibility-проєкцією: `Processed` ⇔ `analyzed ∧ domain_completed`;
`Failed` ⇔ `needs_attention` через `failed|quarantined`; `Skipped` ⇔ `unsupported`. Точне відображення — P05.

## Orchestration (P14)

- **Replay run як job.** `RunService.CreateReplayAsync(scope {source_ids?, from, to})` → `processing.runs {kind replay, lane replay, state created,
  generation_id = нова неактивна, replays_run_id = live run, versions {pipeline_version, topology_version}, scope, checkpoint}` + `control_audit run:create`.
  **Один відкритий replay run** (`ux_processing_runs_open_replay`: created|running|paused|verified) — replay lane, її лічильники і звіт verify належать йому.
- **State machine (compare-and-set, 409 при гонці, кожен перехід — audit `run:*`):** `created → running` (start), `running ↔ paused`,
  `failed → running` (resume після виправлення), `created|running|paused|verified|failed → cancelled` (verified, який не хочуть активувати, звільняє
  єдиний replay-слот), `running|paused → verified` (verify), `verified → promoted`
  (promote), `promoted → rolled_back` (rollback); `failed` ставить publisher після `Replay:MaxBatchFailures` поспіль (CAS на running). `verified` →
  catchup → знову `running` (нова дельта потребує нової перевірки). Термінальні: cancelled, rolled_back; history `completed` — P16.
- **Checkpoint (контракт, jsonb `runs.checkpoint`, camelCase):** `{published, total, lastRawMessageId, lastPublishedAt, ingestCeiling, done, error, failures}`.
  Keyset — за `raw_message_id` (порядок ingestion, той, що бачив live; PK-індекс), у межах `published_at ∈ [from, to]` і `raw_message_id ≤ ingestCeiling`
  (max id при create). `scope`: `{source_ids, from, to, catchup_from?, verification?, promoted_from?}`.
- **Publisher** (`ReplayPublisher`, роль `replay`): `FOR UPDATE SKIP LOCKED` на running run (одна репліка на run), батч `Replay:BatchSize` → `raw.stored
  {lane replay, is_new:false, processing_run_id = run, producer replay@instance}` через outbox **в одній tx з checkpoint** (crash повторює ≤ 1 неопублікований
  батч; повтор → stage `noop` per (raw, run)); backpressure `Replay:MaxInFlight` по pending deliveries lane replay; `PrefetchByLane {replay: 2}` — live має
  зарезервовану місткість каналу; pause/cancel читаються між батчами; помилка батчу → `checkpoint.error/failures` без зміни стану, `failed` після N поспіль.
  Publisher потребує `relay` (outbox → брокер).
- **Delta catchup** = ingestion-дельта: `CatchUpAsync(watermark = now − Replay:WatermarkLag)` підіймає `scope.to` до watermark і `ingestCeiling` до поточного
  max id; той самий keyset-прохід дочитує все, що збережено після попереднього ceiling у (розширеному) вікні — включно з пізнім raw колектора зі старим
  `published_at`. Дозволено з running/paused/verified — **лише до promote** (після нього generation активна, а replay lane не має projection, тож
  записане туди не дійшло б до карти без Resync — review B2/Q6): останній catchup — безпосередньо перед promote; raw, оброблені live між ними, лишаються
  в попередній generation (за потреби — новий replay цього вікна).
- **Shadow без production effects.** Replay lane досягає normalizer/parser/llm-worker/finalizer/archive/incident-worker (v9); parser поза live не викликає
  LLM (`llm_skipped_lane`); track/alert-worker і projection без replay lane → без `targets`, треків, тривог, NOTIFY. `IncidentWriterHandler.ApplyShadowAsync`
  (lane replay): без Store/legacy/already-written guards, без `targets`/`legacy_target_id`/NOTIFY; лише incidents/links/revisions у generation run'а під
  `incident:kind` locks; ідемпотентність — `(raw_message_id, generation_id)` guard; run `cancelled|rolled_back|failed` → `noop run_*` (in-flight доставки
  скасованого run'а зливаються без ефекту).
- **Active generation.** Live/history writers пишуть в **active** generation (`EnsureGenerationAsync`: `run.generation_id ?? active ?? LiveGeneration`) під
  shared advisory lock `generation:active`; promote/rollback беруть його exclusive → запис ніколи не потрапляє в generation, деактивовану в ту саму мить
  (row lock тут не годиться: після конфлікту повторна оцінка «немає рядка»). Read-side (`IncidentQueries.Active`, admin list/review) — лише active.
- **Verify** (гейти → 409): scope опубліковано (`done`), 0 pending deliveries lane replay + 0 unconfirmed outbox lane replay і 0 quarantined **з моменту
  створення run'а** (залишки попередніх runs не блокують), `unanalyzed = distinct raw(events run) − extractions(run) = 0`; звіт у `scope.verification`: stages per outcome, extractions/observations, `incidents_in_generation`,
  `active_incidents_in_window`, `active_incidents_outside_scope` (зникнуть після promote), `active_incidents_missing_in_generation` (raw без incident у
  generation — типово LLM-only факти: replay не викликає модель).
- **Promote** (з verified): одна tx — деактивувати active, активувати generation run'а (`promoted_at`), run → promoted, `scope.promoted_from`; refused при
  `active_incidents_outside_scope > 0`, крім `force` (audited) — **відхилення від §11.5** «explicit replacement scope»: promote перемикає всю generation,
  partial scope = свідоме рішення оператора з reason. **Rollback** (з promoted): active ← `promoted_from` (або LiveGeneration), `rolled_back_at`, run →
  rolled_back; audit фіксує `incidentsWrittenSincePromote` — incidents, які live записав у promoted generation за вікно `[promoted_at, rolled_back_at]`,
  невидимі після відкату до повторного replay цього вікна. Нічого не видаляється. Fencing §11.6: старий процесор/legacy reset під час replay — заборонено
  (ADR-0009 cutover); publisher скасованого run'а не публікує (стан читається per батч під lock).
- **Supersede live run.** `ProcessingRuns.GetOpenRunAsync`: відкритий run іншої `pipeline_version`, створений **до старту цього процесу**, → `superseded`
  (`finished_at`), новий `running` з `supersedes_run_id`; run, відкритий пізніше (новіший деплой), приймається — mixed-version window без «хитання»; старий
  процес до рестарту тримає кешований id. Порівнюються `created_at` БД і час старту процесу — при розбіжності годинників хостів рішення може
  хитнутись; рестарт старішої збірки після новішого run'а знову supersede'ить його (прийнято: mixed-version window коротка, результати обох runs лишаються).
- **Межі P14:** tracks/alerts без `generation_id` — replay їх не будує і не ізолює (P16: generation-колонки + запити мапи); partial merge/replacement scope —
  після даних; history run `completed` — P16; legacy `ReprocessService.ResetAsync`/`ProcessingLoop` — cutover P16; projection checkpoint-таблиця і delta
  за `recorded_at` (ADR-0011) — P15/P16; `processing_status` Pending/backlog семантика (ADR-0009) — P16; backfill forecast / silent-source alarm
  (ADR-0012) — P15.

## Альтернативи

- Один глобальний ACK/лічильник «усі отримали» — неможливий у брокері та змішує транспорт із бізнес-завершенням.
- Replay поверх активної generation з `ON CONFLICT UPDATE` — переписує live результати, ламає rollback; відкинуто.
- Manifest, що очікує **всі** доменні воркери для кожного поста — постійні `noop` без користі; відкинуто.

## Наслідки

- Кожен consumer має повертати receipt навіть при `noop`; це +1 рядок на delivery (retention — ADR-0007).
- Замість reset — новий run/generation; legacy `ReprocessService.ResetAsync` з table fence лишається до cutover (P16).
- Analytics рахує root messages один раз на `(raw_message_id)`, а результати — на `(raw_message_id, run_id)`.

## Відкрите

| Питання | Задача |
|---|---|
| ~~Checkpoint format, pause/cancel, delta catchup до watermark, promote/rollback~~ — done (P14, «Orchestration»); partial replay scope (explicit merge) | після даних |
| Tracks/alerts у generation (replay lane track/alert-worker), history run `completed`, legacy reset cutover, projection checkpoint | P16 |
| Точний mapping `ProcessingStatus` ⇔ workflow stages — P05 не змінює `ProcessingStatus` (його далі пише legacy `ProcessingLoop`; стадії P05 працюють поруч у shadow-режимі і пишуть лише `stage_results`/outbox); stage `analyzed` з'явиться з finalizer'ом | P06 (analyzed) / P14–P16 (cutover) |
| Правила conservative merge при promote generation для incidents/tracks з контекстом поза інтервалом | після даних (P14: `active_incidents_outside_scope` gate + force) |
