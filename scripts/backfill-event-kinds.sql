-- Plan §8.2 / P07: manual batch backfill of targets.event_kind_id from the legacy event_type.
-- Idempotent (only NULL rows), safe to rerun. The Worker does the same on start (EventKindBackfill); use this when a
-- maintenance window is preferred. Each batch is its own transaction under the Store advisory lock (P00 writer rule).
-- Adjust :batch and loop until "updated 0". Requires event_kinds to be seeded (Worker seeders or the migrate role).
--
-- Mapping mirrors EventKindLegacyMap (Domain): 0 Unknown, 1 TargetObserved, 10 AirRaidAlert, 11 AlertCancelled,
-- 12 TargetCancelled, 20 ExplosionReport, 21 AirDefenseActivity. Other values stay NULL and show up in the report.

\set batch 5000

BEGIN;
SELECT pg_advisory_xact_lock(88327283100161); -- AdvisoryLocks.Store = 0x50554C554A01
WITH candidates AS (
    SELECT target_id FROM targets
    WHERE event_kind_id IS NULL AND event_type IN (0, 1, 10, 11, 12, 20, 21) -- unmapped values would loop forever
    ORDER BY target_id LIMIT :batch
)
UPDATE targets t
SET event_kind_id = k.event_kind_id
FROM candidates c
JOIN targets src ON src.target_id = c.target_id
JOIN event_kinds k ON k.code = CASE src.event_type
    WHEN 0 THEN 'unknown.unclassified'
    WHEN 1 THEN 'target.observed'
    WHEN 10 THEN 'alert.air_raid.started'
    WHEN 11 THEN 'alert.air_raid.ended'
    WHEN 12 THEN 'target.cancelled'
    WHEN 20 THEN 'impact.explosion.reported'
    WHEN 21 THEN 'air_defence.activity'
END
WHERE t.target_id = c.target_id;
COMMIT;

-- Report: coverage and unresolved enum values.
SELECT count(*) AS total,
       count(event_kind_id) AS mapped,
       count(*) - count(event_kind_id) AS without_kind
FROM targets;
SELECT event_type, count(*) AS unresolved FROM targets WHERE event_kind_id IS NULL GROUP BY event_type ORDER BY 1;
