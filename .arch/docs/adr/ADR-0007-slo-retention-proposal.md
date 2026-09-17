# ADR-0007 — SLO, quotas і retention: proposal

Статус: **proposed** (P01). Це **пропозиція орієнтирів**, не виміряні результати і не затверджені SLO.
Абсолютні значення затверджує P16 після baseline на цільовому середовищі; retention/disk — P03 (owner Data + Ops).
Вимоги: plan §3.2, §9.2, §13 «Навантаження», §15.1, §15.2.

## Baseline, від якого відштовхуємось (P00, legacy pipeline, один testhost, Testcontainers PostGIS)

| Профіль | 1 worker | 4 workers | 4/1 |
|---|---:|---:|---:|
| Mixed sources/types | 22.81 r/s | 56.75 r/s | 2.49× |
| Single category | 19.86 r/s | 26.59 r/s | **1.34×** (serial ceiling Store) |
| No facts | 78.58 r/s | 199.42 r/s | 2.54× |
| Live-only p95 (30 roots) | — | 853 ms | — |
| Live p95 при одночасній history | — | **4 213 ms** | ×4.94 (орієнтир ≤ +20% не виконано) |

Джерело: `docs/evidence/message-platform/P00-handoff.md`, `P00-baseline.json`. Це legacy без брокера;
нова платформа мусить бути виміряна тим самим методом (committed roots, retries окремо).

## SLO — орієнтири (цілі прототипу, plan §13)

| Показник | Орієнтир | Вимір |
|---|---|---|
| Live end-to-end: `received_at` → `analyzed_at` (rules, без LLM) | p95 ≤ 5 s, p99 ≤ 15 s | `analytics.message_lifecycle` |
| Live: `analyzed_at` → `visible_at` (projection) | p95 ≤ 2 s | lifecycle |
| Live p95 при одночасному history/replay | ≤ +20% від live-only | окремий профіль §13 |
| Throughput незалежного CPU/DB workload | 4 workers ≥ 2× від 1 | committed roots/s |
| Oldest message age у required черзі (live) | ≤ 60 s, alarm при 5 хв | broker metrics |
| Outbox unconfirmed age | alarm > 60 s; critical > 10 хв | `messaging.outbox` |
| Expected delivery без receipt | alarm > 15 хв (live), > 24 год (history/replay) | reconciliation |
| DLQ depth / quarantined per hour | alarm > 0 нових за годину без acknowledgement | deliveries |
| Required consumer offline (stale heartbeat + backlog) | alarm > 2 хв | heartbeat поза шиною |
| LLM: budget/day, rate limit, p95 latency | budget з `.env`; alarm 80% | cost_daily |

Однокатегорійний сценарій звітується окремо: якщо serial ceiling лишився (P09 не виніс hot rows),
брокер сам по собі orієнтир 2× не закриває.

## Quotas live/history/replay (усі рівні, §15.2)

| Рівень | Live | History | Replay |
|---|---|---|---|
| Черги (prefetch × consumers на підписку) | зарезервований мінімум (proposal 50% consumers) | ≤ 30% | ≤ 20%, pausable |
| DB pool | окремі pools або `max` на lane | обмежений | обмежений |
| CPU | пріоритет процесів/контейнерів live | — | окремий контейнер за потреби |
| LLM | окремий budget і rate limiter на lane; live не блокується history | shared budget з cap | shadow — stub або окремий budget |

Пріоритет черги без ресурсного бюджету не гарантує live SLO (P00 ×4.94).

## Retention (proposal, deletion eligibility)

| Дані | Retention | Eligibility для видалення |
|---|---|---|
| `raw_messages` (оригінал) | **зберігається** (безстроково) | Ніколи автоматично; лише policy/legal з audit |
| `messaging.events` для `replay_source` | ≥ raw; proposal безстроково для envelope, payload → `payload_ref` після 180 днів | Лише якщо replay з цього періоду не потрібен і є backup |
| `messaging.events` інші | 180 днів | Після terminal receipts усіх required |
| `messaging.outbox` | до confirm + archive receipt; потім cleanup (grace 7 днів) | Ніколи для unconfirmed |
| `messaging.inbox` | 30 днів після `completed_at` (≥ max redelivery window) | — |
| `processing.deliveries` | replay_source: = events; інші 90 днів | Лише terminal |
| `processing.attempts` | 90 днів; `interrupted`/`failed` з quarantine — до розв'язання + 90 | — |
| `processing.quarantine` | до розв'язання + 90 днів | Після admin resolve |
| DLQ у брокері | без TTL для required; джерело істини — `processing.quarantine` (ADR-0004 W6a-2), broker DLQ — операційна копія; після durable transfer — ACK | Тільки після durable transfer |
| `analytics.message_*` | rebuildable; raw rows 400 днів, daily агрегати безстроково | Rebuild з events/lifecycle |
| LLM request audit (`llm_requests`) | 400 днів (cost/аудит) | — |

Required черги: **без** message TTL і drop-oldest. При ліміті диска брокера — backpressure на relay
(outbox росте; checkpoint collector продовжує, бо outbox+checkpoint — DB-транзакція), alarm; pause collector
лише за явним high-water policy outbox (P04), ручне рішення.

## Обсяг/диск (метод, не числа)

Оцінити після P02 spike: середній envelope+payload (Telegram ≈ 1–4 KiB, alerts ≈ 1 KiB) × події на пост
(≈ 6–9 подій) × пости/день × retention. Backup: PostgreSQL PITR включає `messaging`/`processing`;
брокер не є джерелом істини, його backup не потрібен (тільки definitions export).

## Відкрите

| Питання | Задача |
|---|---|
| Абсолютні SLO після baseline нової платформи на цільовому середовищі; canary thresholds | P16 |
| Retention: P03 реалізував лише outbox cleanup (confirmed + `Messaging:Reconciliation:OutboxGrace` 7 днів; `replay_source` — лише після archive receipt) та inbox cleanup (`InboxRetention` 30 днів, quarantined лишаються); `events`/`deliveries`/`attempts`/`quarantine` без автоматичного видалення — deletion job, backup-перевірка, партиціювання | P16 / Ops |
| LLM budget/rate limits per lane | P06 |
| Quality thresholds (precision/recall) — не в цьому ADR | P08 |
