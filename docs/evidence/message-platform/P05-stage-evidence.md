# P05 — evidence стадій normalizer / parser (plan §4, §12 хвиля 4, ADR-0005)

Джерело чисел: `messaging-crash-evidence.json` (спільний fixture P03–P05; кожне значення після assert на committed стан). Середовище: Windows 11,
.NET SDK 10.0.401, Docker 29.8.0, `postgis/postgis:17-3.5` (gazetteer/taxonomy/event kinds засіяні), `rabbitmq:4.3-management` (4.3.5). TRX:
`test-results/p05-stages-run1.trx` (25/3 — хибні очікування тестів: unit v4, S08 порядок ключів jsonb/`location.text`, S01 гонка stop-relay),
`p05-stages-run2.trx` (28/0, стадії + unit), `p05-messaging-run3.trx` (46/5/1 — P03-тести з очікуванням «лише archive» expected для `raw.stored`),
`p05-messaging-run4.trx` (51/0/1, весь проєкт після оновлення очікувань під v4; skip — P04-C06 W1c), `p05-messaging-run5.trx` (**53/0/1**, після
review-правок: parity S08 включно з `parser_metadata`, unit `StageMapperTests` ×2, failed-стадія переписується, title у location.text).

Конфігурація тестів: `Messaging:Ingress:Enabled=true`, `Llm:Enabled=true` (без ключа — лише рішення про fallback, викликів немає), ролі relay,
archive, raw-writer, normalizer, parser у одному процесі; legacy `RawMessageProcessor` доступний для parity (loop не запущений).

| Test | Сценарій | Committed outcome (assert) | Результат |
|---|---|---|---|
| P05-S01 | Текст із ціллю через ingress → raw-writer → normalizer → parser | `stage_results`: normalize `completed` (text_kind text), parse `facts` (facts_count ≥ 1); `targets` 0, `air_alerts` 0, raw лишається Pending (0); attempts normalizer/parser `succeeded` зі `stage_result_id`; `message.normalized` (causation = raw.stored, той самий correlation, norm-1, uk) і `parse.completed` валідні проти envelope + payload schema; fact `target.observed`/`target`, place_id, `rule_version rule-0.1`, `versions.normalization norm-1`; `attempt_id` = `stage_results.outputs.attempt_id`; `llm.requested` 0; expected finalizer 1 (paused, P06); outbox unconfirmed 0 (не unroutable) | pass |
| P05-S02 | Structured alerts `31:start`/`31:end` (Київська область) | `air_alerts` 0, `targets` 0; normalize `structured` ×2 (`alerts_in_ua.alert.started/finished`); parse `facts` ×2, версія `alerts_in_ua-adapter-1`; факти `alert.air_raid.started` / `…ended`, category alert, confidence confirmed, location region з place_id, `evidence.structured_field=alert`, `parser_metadata.sourceAlertId=31`, `startedAt` | pass |
| P05-S03 | No-text (payload `dev.ingest`, без тексту) | normalize `empty` → parse `unsupported` (method none, version `empty`), facts [] | pass |
| P05-S04 | No-facts («Доброго ранку, друзі!») | parse `no_facts` без `fallback_reason` (не схоже на звіт), `llm.requested` 0, receipt parser `completed` | pass |
| P05-S05 | Multi-fact (2 рядки з цілями) | facts 2 з різними `segment_index`, `facts_count` у stage outputs = 2, усі `target.observed` | pass |
| P05-S06 | Повторний `raw.stored{is_new:false}` того самого raw | normalizer receipts completed 1 + noop 1; `stage_results` normalize 1; `message.normalized` 1; `parse.completed` 1 | pass |
| P05-S07 | Звіт без збігу правил: live / stale (−10 днів) / history lane | live → `needs_llm` + `llm.requested` (fencing 1, `rules_context.attempt_id` = attempt parse), expected llm-worker 1 (paused); stale → `no_facts{llm_skipped_stale}`; history → `no_facts{llm_skipped_lane}`; stage parse `needs_llm` з `llm_request_id` | pass |
| P05-S08 | Parity: той самий raw через стадії і через legacy `RawMessageProcessor.ProcessAsync` | facts 2 = legacy targets 2; field-by-field через один `FactMapper` (event_kind_code, category, effective_at, location, confidence, усі attributes включно з `parser_metadata`; лише `language` не зберігається в рядку) — **0 розбіжностей** | pass |
| Unit | `StageMapperTests` | `FactMapper` детермінований (той самий Target → той самий canonical JSON), містить усі 27 legacy-полів, `Canonical` сортує ключі на всіх рівнях, без gazetteer не вигадує `location.text`; structured adapter для невідомої громади → `location {unknown, text: title}`, `TargetId` 0 (не збережено), `parser_metadata.placeResolved=false` | pass (22/22 unit) |
| Contracts | 6 нових fixtures (`message.normalized.{structured,empty}`, `parse.completed.{no-facts,unsupported,needs-llm,multi-fact}`) + `$defs/evidence` additive | 60/60 | pass |

Оновлені очікування P03-тестів під v4 (не дефекти коду): C01/G03/C06 — expected set `raw.stored` тепер {archive, normalizer}; C07 — waiver archive не
знімає рядки normalizer; C08 — unbind обох черг `raw.stored` (з однією bound чергою publish confirmed — саме висновок P02 C08). Analytics-тест
`AnalysisRunnerTests` вставляв raw без identity-колонок — після P04 B1 (без default) це падає навмисно; тест доповнено.

Не тестовано: реальний LLM (P06), finalizer/fact writer (P06), cutover legacy loop (P14/P16), history/replay run ізоляція стадій (той самий raw у
двох lanes → два run → два набори stage_results — за ADR-0005, покрито лише unit-логікою run per lane).
