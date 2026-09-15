# ADR-0005 — Runs, generations, lanes і completion semantics

Статус: **proposed** (P01). Machine-readable: [`completion-manifest.json`](../../contracts/messaging/completion-manifest.json).
Вимоги: plan §4, §5.2, §11, §15.1 «Completion semantics», §15.2. Реалізація orchestration — P14; receipts — P03.

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
| `analytics_caught_up` | `message-analytics` має receipts для `raw.stored` і `message.analysis.completed` |
| `needs_attention` | будь-який `quarantined`, або outcome `needs_review|failed`, або expected без receipt довше SLO |

`RawMessage.ProcessingStatus` лишається compatibility-проєкцією: `Processed` ⇔ `analyzed ∧ domain_completed`;
`Failed` ⇔ `needs_attention` через `failed|quarantined`; `Skipped` ⇔ `unsupported`. Точне відображення — P05.

## Альтернативи

- Один глобальний ACK/лічильник «усі отримали» — неможливий у брокері та змішує транспорт із бізнес-завершенням.
- Replay поверх активної generation з `ON CONFLICT UPDATE` — переписує live результати, ламає rollback; відкинуто.
- Manifest, що очікує **всі** доменні воркери для кожного поста — постійні `noop` без користі; відкинуто.

## Наслідки

- Кожен consumer має повертати receipt навіть при `noop`; це +1 рядок на delivery (retention — ADR-0007).
- Замість reset — новий run/generation; legacy `ReprocessService.ResetAsync` з table fence лишається до P14.
- Analytics рахує root messages один раз на `(raw_message_id)`, а результати — на `(raw_message_id, run_id)`.

## Відкрите

| Питання | Задача |
|---|---|
| Checkpoint format, pause/cancel, delta catchup до watermark, promote/rollback, partial replay scope | P14 |
| Точний mapping `ProcessingStatus` ⇔ workflow stages | P05 |
| Правила conservative merge при promote generation для incidents/tracks з контекстом поза інтервалом | P14 + P09/P10 |
