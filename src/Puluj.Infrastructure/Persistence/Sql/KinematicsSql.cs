namespace Puluj.Infrastructure.Persistence.Sql;

/// <summary>
/// The "magic stone": target-to-target links and source copy statistics live in the database as PL/pgSQL + PostGIS,
/// fired by triggers on `targets`. The application inserts facts and marks duplicates; the database links, counts and
/// rates. Installed by the AddKinematicLinks migration; re-run the Up script to update the functions in place.
/// </summary>
public static class KinematicsSql
{
    /// <summary>The linker alone: re-run (a migration with just this) to change how predecessors are weighed.</summary>
    public const string LinkTarget = """
-- ---------------------------------------------------------------------------------------------------------------
-- Kinematic predecessors of one target. Candidates: earlier targets of the same category within the class window,
-- not duplicates, not from the same message, placeable, and with some of their "future" still unassigned (see
-- `remaining`). For each candidate the fit is a product of five factors, each in 0..1:
--   time      1 while the trip fits at cruise speed (plus 10 min of dwell when the two areas overlap, 20 min when the
--             two reports name the same place: the same standing report repeated), decaying with a 20 min e-fold
--             when the object would have had to loiter; 0 when even the top speed plus 3 min of reporting slack
--             cannot cover the gap.
--   distance  a prior on how far apart the two reports' centres are: 1 / (1 + km / 60). The nearest compatible report
--             wins; two areas 60 km apart weigh half of a repeat of the same area, even when their slack overlaps.
--   course    1 when the object did not have to move (gap <= 10 km: nothing to judge); (1 + cos θ) / 2 between its
--             reported course and the bearing to the new report; 0.6 when it had to move but had no course.
--   class     same model 1, same family 0.9, same class 0.85, unknown 0.7; different models 0.2, different families
--             or classes 0.3.
--   remaining 1 minus the probability already assigned to the candidate's later continuations: one object has one
--             future, so a report that already "became" something else has little left for a new successor. This is
--             what keeps the fan-out of a target near 1 instead of a dozen branches.
-- Candidates under 0.1 or under half of the best one are dropped (a clear winner stands alone); at most three stay.
-- Their probabilities are the weights when they sum to <= 1 and the normalised weights otherwise, so one target never
-- has more than one predecessor's worth of probability: two equal candidates get 0.5 each, the rest is "a new object".
-- ---------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION puluj_link_target(p_target_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE inserted integer := 0;
BEGIN
    DELETE FROM target_links WHERE to_target_id = p_target_id AND kind = 0;
    WITH n AS (
        SELECT t.*, a.geom AS a_geom, a.centre AS a_centre, a.slack_km AS a_slack,
               COALESCE(p.v_max, 200) AS v_max, COALESCE(p.v_cruise, 175) AS v_cruise, COALESCE(p.window_min, 30) AS win
        FROM targets t
        JOIN target_anchors a ON a.target_id = t.target_id
        LEFT JOIN LATERAL puluj_class_params(t.target_class_id) p ON TRUE
        WHERE t.target_id = p_target_id AND t.event_type = 1 AND t.duplicate_of_target_id IS NULL AND t.target_category_id IS NOT NULL
    ),
    c AS MATERIALIZED (
        SELECT t.target_id AS from_id, t.observed_at AS c_observed, t.direction_deg AS c_dir,
               t.target_model_id, t.target_family_id, t.target_class_id,
               pa.geom AS c_geom, pa.centre AS c_centre, pa.slack_km AS c_slack,
               n.observed_at AS n_observed, n.target_model_id AS n_model, n.target_family_id AS n_family, n.target_class_id AS n_class,
               n.a_geom, n.a_centre, n.a_slack, n.v_max, n.v_cruise,
               -- the same place named again (as the position or as the destination): a standing report repeated
               ((n.location_place_id IS NOT NULL AND n.location_place_id IN (t.location_place_id, t.destination_place_id))
                OR (n.destination_place_id IS NOT NULL AND n.destination_place_id IN (t.location_place_id, t.destination_place_id))) AS same_place,
               GREATEST(0, 1 - COALESCE((SELECT SUM(l.probability) FROM target_links l WHERE l.from_target_id = t.target_id AND l.kind = 0), 0)) AS remaining
        FROM n
        JOIN targets t ON t.target_id <> n.target_id AND t.raw_message_id <> n.raw_message_id
            AND t.event_type = 1 AND t.duplicate_of_target_id IS NULL
            AND t.target_category_id = n.target_category_id
            AND t.observed_at < n.observed_at AND t.observed_at >= n.observed_at - make_interval(mins => n.win::int)
        JOIN target_anchors pa ON pa.target_id = t.target_id
    ),
    -- MATERIALIZED: without it the planner inlines the subqueries and evaluates each distance several times per candidate.
    m AS MATERIALIZED (
        SELECT from_id, gap, centre_km, remaining, same_place,
               EXTRACT(EPOCH FROM (n_observed - c_observed)) / 60.0 AS dt,
               -- the course only matters once the object really had to move (a bearing between two overlapping areas is noise)
               CASE WHEN c_dir IS NOT NULL AND gap > 10
                    THEN ABS(MOD((degrees(ST_Azimuth(c_centre, a_centre)) - c_dir + 540)::numeric, 360::numeric) - 180)
               END AS theta,
               v_max, v_cruise, target_model_id, target_family_id, target_class_id, n_model, n_family, n_class
        FROM (SELECT c.*,
                     -- sphere, not spheroid: a few metres per hundred km, at a third of the cost per candidate pair
                     GREATEST(0, ST_Distance(c_geom, a_geom, false) / 1000.0 - c_slack - a_slack) AS gap,
                     ST_Distance(c_centre, a_centre, false) / 1000.0 AS centre_km
              FROM c WHERE c.remaining > 0.05) g
    ),
    w AS MATERIALIZED (
        SELECT from_id, gap, dt, theta,
               need_cruise,
               CASE WHEN dt + 3 < gap / v_max * 60 + turn THEN 0
                    WHEN dt <= need_cruise + dwell THEN 1
                    ELSE EXP(-(dt - need_cruise - dwell) / 20.0) END
               * (1 / (1 + centre_km / 60.0))
               * CASE WHEN gap <= 10 THEN 1 WHEN theta IS NULL THEN 0.6 ELSE GREATEST(0.05, (1 + COS(RADIANS(theta))) / 2) END
               * CASE WHEN n_model IS NOT NULL AND n_model = target_model_id THEN 1
                      WHEN n_model IS NOT NULL AND target_model_id IS NOT NULL THEN 0.2
                      WHEN n_family IS NOT NULL AND n_family = target_family_id THEN 0.9
                      WHEN n_family IS NOT NULL AND target_family_id IS NOT NULL THEN 0.3
                      WHEN n_class IS NOT NULL AND target_class_id IS NOT NULL AND n_class <> target_class_id THEN 0.3
                      WHEN n_class IS NOT NULL AND n_class = target_class_id THEN 0.85
                      ELSE 0.7 END
               * remaining AS weight
        FROM (SELECT m.*,
                     COALESCE(theta, 0) / 180.0 * 4.0 AS turn,
                     gap / v_cruise * 60 + COALESCE(theta, 0) / 180.0 * 4.0 AS need_cruise,
                     CASE WHEN same_place THEN 20 WHEN gap <= 10 THEN 10 ELSE 0 END AS dwell
              FROM m) x
    ),
    top AS (
        SELECT *, SUM(weight) OVER () AS total
        FROM (SELECT * FROM (SELECT w.*, MAX(weight) OVER () AS best FROM w) b
              WHERE weight >= 0.1 AND weight >= 0.5 * best
              ORDER BY weight DESC LIMIT 3) s
    ),
    ins AS (
        INSERT INTO target_links (from_target_id, to_target_id, kind, probability, distance_km, minutes_apart, heading_diff_deg, required_minutes, created_at)
        SELECT from_id, p_target_id, 0,
               ROUND((CASE WHEN total > 1 THEN weight / total ELSE weight END)::numeric, 3),
               ROUND(gap::numeric, 1), ROUND(dt::numeric, 1), ROUND(theta::numeric, 0), ROUND(need_cruise::numeric, 1), now()
        FROM top
        RETURNING 1
    )
    SELECT COUNT(*) INTO inserted FROM ins;
    RETURN inserted;
END $$;

""";

    /// <summary>
    /// The anchor of every located target, computed once when the target is inserted: the linker joins this table
    /// instead of rebuilding the oblast polygon of every candidate for every new target.
    /// </summary>
    /// <summary>
    /// Refreshes the target-insert trigger without rebuilding historical anchors. Kept separately so a migration can
    /// repair databases that still have the retired source-statistics implementation of this function.
    /// </summary>
    public const string OnTargetInsert = """
CREATE OR REPLACE FUNCTION puluj_on_target_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.event_type = 1 THEN
        INSERT INTO target_anchors (target_id, geom, centre, slack_km)
        SELECT NEW.target_id, a.geom, ST_Centroid(a.geom), a.slack_km FROM puluj_target_anchor(NEW) a WHERE a.geom IS NOT NULL
        ON CONFLICT (target_id) DO NOTHING;
        PERFORM puluj_link_target(NEW.target_id);
    END IF;
    RETURN NULL;
END $$;
""";

    /// <summary>
    /// Refreshes the duplicate-link trigger after the retired source-copy statistics tables have been removed.
    /// Kept separately so upgrades replace a function body installed by an earlier migration.
    /// </summary>
    public const string OnTargetDuplicate = """

CREATE OR REPLACE FUNCTION puluj_on_target_duplicate() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE o targets;
BEGIN
    IF NEW.duplicate_of_target_id IS NULL OR OLD.duplicate_of_target_id IS NOT DISTINCT FROM NEW.duplicate_of_target_id THEN
        RETURN NULL;
    END IF;
    DELETE FROM target_links WHERE (to_target_id = NEW.target_id OR from_target_id = NEW.target_id) AND kind = 0;
    SELECT * INTO o FROM targets WHERE target_id = NEW.duplicate_of_target_id;
    IF o.target_id IS NULL THEN RETURN NULL; END IF;
    INSERT INTO target_links (from_target_id, to_target_id, kind, probability, created_at)
    VALUES (o.target_id, NEW.target_id, 4, 1, now())
    ON CONFLICT (from_target_id, to_target_id) DO UPDATE SET kind = 4, probability = 1;
    RETURN NULL;
END $$;
""";

    public const string Anchors = """
CREATE TABLE IF NOT EXISTS target_anchors (
    target_id bigint PRIMARY KEY REFERENCES targets (target_id) ON DELETE CASCADE,
    geom geography NOT NULL,
    centre geography NOT NULL,
    slack_km double precision NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_target_anchors_geom ON target_anchors USING gist (geom);
""" + OnTargetInsert + """
-- Anchors of the targets already in the database (the trigger covers everything from here on).
INSERT INTO target_anchors (target_id, geom, centre, slack_km)
SELECT t.target_id, a.geom, ST_Centroid(a.geom), a.slack_km
FROM targets t CROSS JOIN LATERAL puluj_target_anchor(t) a
WHERE t.event_type = 1 AND a.geom IS NOT NULL
ON CONFLICT (target_id) DO NOTHING;
""";

    public const string Up = """
-- ---------------------------------------------------------------------------------------------------------------
-- Anchor of a target for kinematics: the oblast polygon for region-level reports (exact, no slack), the reported
-- point with its accuracy for precise ones, or the approach to the named destination (>= 40 km) when only that is
-- known. NULL when the target cannot be placed at all.
-- ---------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION puluj_target_anchor(t targets, OUT geom geography, OUT slack_km double precision)
LANGUAGE plpgsql STABLE AS $$
DECLARE p places;
BEGIN
    IF t.location IS NOT NULL THEN
        IF t.location_kind IN (2, 5) AND t.location_place_id IS NOT NULL THEN
            SELECT * INTO p FROM places WHERE place_id = t.location_place_id;
            IF p.place_id IS NOT NULL AND GeometryType(p.geometry) IN ('POLYGON', 'MULTIPOLYGON') THEN
                -- ~1 km tolerance: a report "on the oblast" is no finer, and the distance between two polygons later
                -- costs vertices-squared.
                geom := ST_SimplifyPreserveTopology(p.geometry, 0.01)::geography; slack_km := 0; RETURN;
            END IF;
        END IF;
        geom := t.location; slack_km := COALESCE(t.location_accuracy_km, 0); RETURN;
    END IF;
    IF t.destination_place_id IS NOT NULL THEN
        SELECT * INTO p FROM places WHERE place_id = t.destination_place_id;
        IF p.place_id IS NOT NULL THEN
            geom := p.centroid; slack_km := GREATEST(p.radius_km, 40); RETURN;
        END IF;
    END IF;
    geom := NULL; slack_km := NULL;
END $$;

-- Speed and search window of a class, from target_classes.metadata (the same numbers the client uses for ETA).
CREATE OR REPLACE FUNCTION puluj_class_params(class_id integer, OUT v_max double precision, OUT v_cruise double precision, OUT window_min double precision)
LANGUAGE sql STABLE AS $$
    SELECT COALESCE((c.metadata->>'speedKmhMax')::float, 200),
           (COALESCE((c.metadata->>'speedKmhMin')::float, COALESCE((c.metadata->>'speedKmhMax')::float, 200))
            + COALESCE((c.metadata->>'speedKmhMax')::float, 200)) / 2,
           COALESCE((c.metadata->>'correlationWindowMinutes')::float, 30)
    FROM target_classes c WHERE c.target_class_id = class_id
$$;

""" + Anchors + LinkTarget + """
-- ---------------------------------------------------------------------------------------------------------------
-- Predecessor chain of a target: at every step the most probable predecessor. Feeds the crumbs on the map and the
-- "was at" line in the card.
-- ---------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION puluj_target_chain(p_target_id bigint, p_steps integer)
RETURNS TABLE(step integer, target_id bigint, probability double precision)
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE chain AS (
        SELECT 0 AS step, p_target_id AS target_id, 1::float AS probability
        UNION ALL
        SELECT chain.step + 1, l.from_target_id, l.probability
        FROM chain
        JOIN LATERAL (
            SELECT from_target_id, probability FROM target_links
            WHERE to_target_id = chain.target_id AND kind = 0
            ORDER BY probability DESC, from_target_id DESC LIMIT 1
        ) l ON TRUE
        WHERE chain.step < p_steps
    )
    SELECT step, target_id, probability FROM chain
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Duplicate targets remain linked for map/history traversal, without attributing one source to another.
-- ---------------------------------------------------------------------------------------------------------------
""" + OnTargetDuplicate + """

DROP TRIGGER IF EXISTS trg_targets_insert_kinematics ON targets;
CREATE TRIGGER trg_targets_insert_kinematics AFTER INSERT ON targets
    FOR EACH ROW EXECUTE FUNCTION puluj_on_target_insert();
DROP TRIGGER IF EXISTS trg_targets_duplicate ON targets;
CREATE TRIGGER trg_targets_duplicate AFTER UPDATE OF duplicate_of_target_id ON targets
    FOR EACH ROW EXECUTE FUNCTION puluj_on_target_duplicate();

-- Backfill map links for existing duplicate facts and recent moving targets.
DELETE FROM target_links WHERE kind IN (0, 1, 2, 3);
INSERT INTO target_links (from_target_id, to_target_id, kind, probability, created_at)
SELECT t.duplicate_of_target_id, t.target_id, 4, 1, now() FROM targets t WHERE t.duplicate_of_target_id IS NOT NULL
ON CONFLICT DO NOTHING;
SELECT COUNT(puluj_link_target(target_id)) FROM (SELECT target_id FROM targets WHERE event_type = 1 AND duplicate_of_target_id IS NULL AND observed_at > now() - interval '24 hours' ORDER BY target_id) t;
""";

    public const string Down = """
DROP TABLE IF EXISTS target_anchors;
DROP TRIGGER IF EXISTS trg_targets_insert_kinematics ON targets;
DROP TRIGGER IF EXISTS trg_targets_duplicate ON targets;
DROP FUNCTION IF EXISTS puluj_on_target_duplicate();
DROP FUNCTION IF EXISTS puluj_on_target_insert();
DROP FUNCTION IF EXISTS puluj_target_chain(bigint, integer);
DROP FUNCTION IF EXISTS puluj_link_target(bigint);
DROP FUNCTION IF EXISTS puluj_class_params(integer);
DROP FUNCTION IF EXISTS puluj_target_anchor(targets);
""";
}
