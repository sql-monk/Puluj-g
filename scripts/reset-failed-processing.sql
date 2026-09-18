-- Returns Failed raw messages (processing_status = 2) to Pending: attempts reset, claim cleared. Use when
-- the failure reason is gone. Derived rows were never committed, so the direct processor can claim them normally.
--
--   docker compose -p puluj-g -f deploy/docker-compose.yml exec -T postgis psql -U puluj -d puluj -f - < scripts/reset-failed-processing.sql
--
-- Restrict the WHERE (raw_message_id IN (...), a source, a period) when only some messages deserve another try.
BEGIN;
UPDATE raw_messages
SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
WHERE processing_status = 2;
COMMIT;
SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1;
