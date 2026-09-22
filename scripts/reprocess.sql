-- Rebuilds every derived table from the raw messages: targets, tracks, their links and revisions, alerts, source
-- statistics. Use after a parser / correlation / linker change to re-run the pipeline over what has already been
-- collected. The raw messages themselves are kept; the processors re-claim them in publication order (oldest first,
-- whichever channel they came from) and drain the backlog without waiting; processing is deterministic without an LLM
-- key and with one processor at Processing__Concurrency=1. The same thing from the admin panel: POST /api/admin/ops/reprocess.
--
--   docker exec -i puluj-pg psql -U puluj -d puluj -f - < scripts/reprocess.sql
--
-- Same sequence as ReprocessService, because a long ACCESS EXCLUSIVE lock on raw_messages stalls the collectors'
-- inserts until they time out and restart:
--   1. pause processing (durable marker: if the script dies half-way, the processors stay away until it is re-run);
--   2. finished rows back to Pending with no table lock (row locks on finished rows only; nobody else touches them);
--   3. the fence: LOCK raw_messages for the few rows that changed meanwhile and to wait out in-flight processors,
--      take Store (AdvisoryLocks.Store = 0x50554C554A01) at session level so it outlives the commit;
--   4. delete the derived tables holding only Store: a processor that claims a row waits for Store, then writes fresh;
--   5. release Store, resume processing.
-- Lock order raw table/rows -> Store is shared with processor and watchdog.
-- Track ids change; anything that stored one (a bookmark, a screenshot) will not match afterwards.

-- 1. Pause: the processors stop claiming within their poll interval. A pause a history load owns is left alone
--    (the load runs its own reset when it finishes), and step 5 removes only a pause this script wrote.
INSERT INTO app_settings (key, value, is_secret, updated_at)
VALUES ('Runtime:Processing:Paused', 'reprocess: scripts/reprocess.sql is running; run it again if it did not finish', false, now())
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now() WHERE app_settings.value LIKE 'reprocess:%';

-- 2. Finished rows (Processed, Failed, Skipped) back to Pending outside the fence. Failed ones too: a parser fix is
--    one of the reasons to be here. InProgress rows belong to a processor and are left to the fence.
UPDATE raw_messages
SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
WHERE processing_status IN (1, 2, 3);

-- 3. Fence: in-flight messages finish, new claims/ingestion wait until this commit — milliseconds, not minutes.
BEGIN;
LOCK TABLE raw_messages IN ACCESS EXCLUSIVE MODE;
UPDATE raw_messages
SET processing_status = 0, attempts = 0, processed_at = NULL, claimed_by = NULL, claimed_at = NULL
WHERE processing_status <> 0;
SELECT pg_advisory_lock(88327283100161); -- Store, session level: still held after COMMIT
COMMIT;

-- 4. Derived state, children before parents, with Store held and the raw table free for the collectors.
BEGIN;
DELETE FROM target_links;
DELETE FROM track_targets;
DELETE FROM target_track_revisions;
DELETE FROM target_tracks;
DELETE FROM targets;
DELETE FROM air_alerts;
DELETE FROM processing_errors;
COMMIT;

-- 5. Let the processors in.
SELECT pg_advisory_unlock(88327283100161);
DELETE FROM app_settings WHERE key = 'Runtime:Processing:Paused' AND value LIKE 'reprocess:%';
SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1;
