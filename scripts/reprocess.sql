-- Rebuilds every derived table from the raw messages: targets, tracks, their links and revisions, alerts, source
-- statistics. Use after a parser / correlation / linker change to re-run the pipeline over what has already been
-- collected. The raw messages themselves are kept; the processors re-claim them in publication order (oldest first,
-- whichever channel they came from) and drain the backlog without waiting; processing is deterministic without an LLM
-- key and with one processor at Processing__Concurrency=1. The same thing from the admin panel: POST /api/admin/ops/reprocess.
--
--   docker exec -i puluj-pg psql -U puluj -d puluj -f - < scripts/reprocess.sql
--
-- Fence the raw table first: in-flight messages finish, new claims/ingestion wait until commit. Then reset raw rows
-- and take Store (AdvisoryLocks.Store = 0x50554C554A01) before truncating derived state.
-- Track ids change; anything that stored one (a bookmark, a screenshot) will not match afterwards.
BEGIN;
LOCK TABLE raw_messages IN ACCESS EXCLUSIVE MODE;
UPDATE raw_messages
SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
WHERE processing_status <> 0; -- Failed ones too: a parser fix is one of the reasons to be here
SELECT pg_advisory_xact_lock(88327283100161); -- 0x50554C554A01
TRUNCATE track_targets, target_track_revisions, target_tracks, targets, air_alerts, processing_errors
    RESTART IDENTITY CASCADE;
COMMIT;
SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1;
