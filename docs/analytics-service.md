# Аналітичний сервіс і життєвий цикл повідомлень

## Призначення і межа схеми

`Puluj.Analytics.Worker` мігрує й обслуговує схему `analytics`; її history — `analytics.__EFMigrationsHistory`. `Puluj.Analytics` читає `raw_messages`, `sources`, `targets` та processing/messaging дані через SQL, не мапуючи їх як власні EF-сутності. Виняток — `analytics.message_lifecycle`: DDL належить migration основного `PulujDbContext`, а analytics worker її читає/оновлює.

![Потік даних та межа analytics schema](diagrams/analytics-data-flow-schema-boundary.png)

Редагована схема: [analytics-data-flow-schema-boundary.drawio](diagrams/analytics-data-flow-schema-boundary.drawio).

## Text/copy reporting

Loop бере raw rows у порядку ID, старші за `Analytics:SafetyLag`, і рухає watermark транзакційно разом з index/pairs. PostgreSQL advisory lock не дає двом instances виконувати один run. Після crash незакомічена batch повторюється; це не exactly-once гарантія для зовнішніх систем.

Пара виникає лише коли обидва повідомлення мають сумісні **розпарсені факти**: event type/category і перевірка часу, місця, напрямку та кількості у `EventWindow`. Text fingerprints, Jaccard і containment — діагностичні докази вже прийнятої semantic pair. Повідомлення без фактів не парується, тож `near`, `verbatim`, `forward` — аналітична оцінка/класифікація, не доказ копіювання чи першоджерела. «Original» обирається за ранішим published time (при рівності — меншим raw ID): це технічне упорядкування, не авторський факт.

Read-only admin API: `/api/admin/analytics/status`, `/report`, `/recent`, `/pairs/{copierId}/{originalId}`. Status показує watermark, backlog, heartbeat, runs та instance status; звіт має джерела, пари, forwards і `track_firsts`. Значення залежать від надходження, parsing і watermark, тому не гарантують completeness/freshness у реальному часі.

## Lifecycle projection, backfill і reconciliation

`analytics.message_lifecycle` поєднує durable evidence: root з raw, analysis з extraction/stage/observation, domain completion з archived events/deliveries, зв'язки incident/track/alert та LLM cost. Запис має `run_id` і `source_of_truth`; невідоме лишається unknown. Історичний raw без stage/events і старший за grace потрапляє до synthetic `legacy` run з недоступними timing/completion, а не з вигаданими значеннями. Backfill не перезаписує event-sourced values: заповнює лише порожні поля або upsert-ить root.

![Проєкція lifecycle та reconciliation](diagrams/message-lifecycle-projection-reconciliation.png)

Редагована схема: [message-lifecycle-projection-reconciliation.drawio](diagrams/message-lifecycle-projection-reconciliation.drawio).

Worker у кожному циклі виконує bounded backfill за cursor, потім sweep reconciliation у recent window. Reconciliation лічить raw/posts/edits проти projection, backfill-ить не більш як 2000 missing roots за раз і заповнює late analysis/domain completion лише за наявними durable records. Вона не доводить повноту джерела і може відобразити затримку delivery/event.

`POST /api/admin/analytics/lifecycle/backfill` і `/reconcile` потребують actor/reason та записують `ControlAudit`; `backfill?reset=true` скидає cursor, але не видаляє rows. Це write-операції з можливими DB load, зміною derived view та конкуренцією з worker. Спершу оцінюють status/progress, після — reconciliation report; відповідь API не означає завершення всієї історії.

![Provenance та freshness звіту](diagrams/analytics-report-provenance-freshness.png)

Редагована схема: [analytics-report-provenance-freshness.drawio](diagrams/analytics-report-provenance-freshness.drawio).

## Перевірка і межі

`tests/Puluj.Analytics.Tests` перевіряє normalizer, semantic copy detection, pair delays, runner і lifecycle; `web/e2e/A06-lifecycle.e2e.ts` перевіряє операторський lifecycle UI. `POST /api/admin/analytics/reset` видаляє analytics index/copies/track_firsts і повертає watermark для rebuild; endpoint існує, але не вимагає actor/reason і не пише `ControlAudit`, тому це не безпечна рутинна дія.
