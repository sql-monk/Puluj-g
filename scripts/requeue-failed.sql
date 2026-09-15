-- Puts Failed raw messages (processing_status = 2) back into the queue: attempts reset, claim cleared. For messages
-- that ended Failed for a reason that is gone — a deadlock with an older processor over the same database (15.09:
-- RawMessage 99794), a parser bug since fixed. Their derived rows were never stored (the failed transaction rolled
-- back to the savepoint), so nothing has to be deleted; the processors claim them like any other Pending row.
-- Since the transient-failure change, a deadlock or serialization failure no longer counts as an attempt.
--
--   docker compose -p puluj-g -f deploy/docker-compose.yml exec -T postgis psql -U puluj -d puluj -f - < scripts/requeue-failed.sql
--
-- Restrict the WHERE (raw_message_id IN (...), a source, a period) when only some of them deserve another try.
BEGIN;
UPDATE raw_messages
SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
WHERE processing_status = 2;
COMMIT;
SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1;
