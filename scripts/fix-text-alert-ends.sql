-- One-off after the TextAlertSink fix of 2026-09-15: intervals that a replayed old "відбій" closed before they had
-- started (ended_at < started_at) are reopened; the watchdog expires them at started_at + 3 h unless a real
-- "відбій" inside the interval is still to be processed. Run once: docker compose -p puluj-g -f deploy/docker-compose.yml exec -T postgis psql -U puluj -d puluj -f - < scripts/fix-text-alert-ends.sql
BEGIN;
SELECT pg_advisory_xact_lock(88327283100161); -- AdvisoryLocks.Store; this repair does not read raw_messages
UPDATE air_alerts SET ended_at = NULL, end_raw_message_id = NULL
WHERE source_alert_id LIKE 'text:%' AND ended_at < started_at;
COMMIT;
