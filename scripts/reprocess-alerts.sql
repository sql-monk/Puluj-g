-- Rebuilds the air alerts from the stored alerts.in.ua messages (e.g. after the gazetteer learned raions and
-- hromadas, so the alerts move from the oblast polygon to the polygon the feed actually names).
-- Run: docker exec -i puluj-pg psql -U puluj -d puluj -f - < scripts/reprocess-alerts.sql
-- The processors re-handle the messages in publication order within a minute. Same order as reprocess.sql: raw rows
-- first (exclusive table fence includes new/Pending rows), then Store, then the deletes.
BEGIN;
LOCK TABLE raw_messages IN ACCESS EXCLUSIVE MODE;
UPDATE raw_messages SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
 WHERE raw_payload->>'kind' IN ('alert.started', 'alert.finished');
SELECT pg_advisory_xact_lock(88327283100161); -- AdvisoryLocks.Store = 0x50554C554A01
DELETE FROM target_links l USING targets t
 WHERE (l.from_target_id = t.target_id OR l.to_target_id = t.target_id) AND t.identification_source = 'alerts.in.ua';
DELETE FROM track_targets tt USING targets t WHERE tt.target_id = t.target_id AND t.identification_source = 'alerts.in.ua';
DELETE FROM targets WHERE identification_source = 'alerts.in.ua';
DELETE FROM air_alerts a USING sources s WHERE a.source_id = s.source_id AND s.code = 'alerts_in_ua';
COMMIT;
