using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// What the entity extractor needs to put its entities on the map.
/// <list type="bullet">
/// <item><c>ee_place_id</c> / <c>ee_place_geometry</c>: extractors run without database access, so a geometry value may
/// name a place (<c>{"place": "Кременчук", "region": ...}</c>); the host inserts <c>ee_place_geometry(ref, kind)</c>,
/// which finds it in <c>places</c>. SECURITY DEFINER: <c>puluj_ee</c> gets the geometry, not the gazetteer.</item>
/// <item>No event without a time: the rows written before the host filled <c>occurred_at</c> take their message's
/// publication time, and every entity table gets an index on it (the map reads the latest rows by time).</item>
/// <item>A launch becomes a point at its launch site (<c>ee_launches.geometry</c> was a line nobody could draw).</item>
/// <item>An alert is a state, not an event: <c>ee_alerts.state_key</c> names the area, and the map shows only the
/// latest row of each key (<c>map_settings.keyField</c>), so a finished alert leaves the map.</item>
/// </list>
/// The ee_* tables hold a few thousand rows: plain DDL, no lock worth shaping.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260924060000_EntityPlacesAndEventTime")]
public partial class EntityPlacesAndEventTime : Migration
{
    private static readonly string[] EntityTables =
        ["ee_targets", "ee_tracks", "ee_alerts", "ee_impacts", "ee_explosions", "ee_air_defense_actions", "ee_launches", "ee_takeoffs"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION ee_place_id(p_ref jsonb)
            RETURNS integer
            LANGUAGE plpgsql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
            DECLARE
                names text[];
                regions text[];
                hints text[];
                levels integer[];
                region_id integer;
                hint_ids integer[] := ARRAY[]::integer[];
                best record;
                rival integer;
            BEGIN
                IF p_ref IS NULL OR jsonb_typeof(p_ref->'names') IS DISTINCT FROM 'array' THEN
                    RETURN NULL;
                END IF;
                names := ARRAY(SELECT jsonb_array_elements_text(p_ref->'names'));
                IF cardinality(names) = 0 THEN
                    RETURN NULL;
                END IF;
                IF jsonb_typeof(p_ref->'regions') = 'array' THEN
                    regions := ARRAY(SELECT jsonb_array_elements_text(p_ref->'regions'));
                    SELECT place_id INTO region_id FROM places
                    WHERE level = 1 AND name_variants && regions
                    ORDER BY (lower(name) = ANY(regions)) DESC, place_id
                    LIMIT 1;
                    IF region_id IS NULL THEN
                        RETURN NULL;  -- a region the gazetteer does not know cannot vouch for any candidate
                    END IF;
                END IF;
                IF jsonb_typeof(p_ref->'hints') = 'array' THEN
                    hints := ARRAY(SELECT jsonb_array_elements_text(p_ref->'hints'));
                    hint_ids := ARRAY(SELECT place_id FROM places WHERE level = 1 AND name_variants && hints);
                END IF;
                IF jsonb_typeof(p_ref->'levels') = 'array' THEN
                    levels := ARRAY(SELECT (jsonb_array_elements_text(p_ref->'levels'))::integer);
                END IF;

                -- Ranked: inside the required region, inside a hinted region, the literal name, the most significant
                -- level (oblast < raion < hromada < city < town < village), then population.
                SELECT p.place_id, p.level,
                       coalesce(region_id IN (p.place_id, p.parent_id, pp.parent_id), false) AS in_region,
                       (p.place_id = ANY(hint_ids) OR p.parent_id = ANY(hint_ids) OR pp.parent_id = ANY(hint_ids)) AS in_hint,
                       (lower(p.name) = ANY(names) OR names[1] = ANY(p.name_variants)) AS literal
                INTO best
                FROM places p
                LEFT JOIN places pp ON pp.place_id = p.parent_id
                WHERE p.name_variants && names
                  AND (levels IS NULL AND p.level BETWEEN 1 AND 7 OR p.level = ANY(levels))
                ORDER BY in_region DESC, in_hint DESC, literal DESC, p.level, coalesce(p.population, 0) DESC, p.place_id
                LIMIT 1;
                IF NOT FOUND OR (region_id IS NOT NULL AND NOT best.in_region) THEN
                    RETURN NULL;
                END IF;
                -- An unqualified village name ("Андріївка") exists in many oblasts: refuse to guess between equals.
                IF best.level >= 6 THEN
                    SELECT p.place_id INTO rival
                    FROM places p
                    LEFT JOIN places pp ON pp.place_id = p.parent_id
                    WHERE p.name_variants && names
                      AND p.place_id <> best.place_id
                      AND p.level >= 6
                      AND (levels IS NULL OR p.level = ANY(levels))
                      AND coalesce(region_id IN (p.place_id, p.parent_id, pp.parent_id), false) = best.in_region
                      AND (p.place_id = ANY(hint_ids) OR p.parent_id = ANY(hint_ids) OR pp.parent_id = ANY(hint_ids)) = best.in_hint
                      AND (lower(p.name) = ANY(names) OR names[1] = ANY(p.name_variants)) = best.literal
                    LIMIT 1;
                    IF FOUND THEN
                        RETURN NULL;
                    END IF;
                END IF;
                RETURN best.place_id;
            END
            $function$;

            CREATE OR REPLACE FUNCTION ee_place_geometry(p_ref jsonb, p_kind text)
            RETURNS geometry
            LANGUAGE plpgsql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
            DECLARE
                found_id integer;
                place record;
                area geometry;
                start_point geometry;
                end_point geometry;
            BEGIN
                IF p_ref IS NULL OR jsonb_typeof(p_ref) <> 'object' THEN
                    RETURN NULL;
                END IF;
                IF p_kind = 'line' THEN
                    start_point := ee_place_geometry(p_ref->'from', 'point');
                    end_point := ee_place_geometry(p_ref->'to', 'point');
                    IF start_point IS NULL OR end_point IS NULL OR ST_Equals(start_point, end_point) THEN
                        RETURN NULL;
                    END IF;
                    RETURN ST_MakeLine(start_point, end_point);
                END IF;
                IF p_ref ? 'lon' AND p_ref ? 'lat' THEN
                    RETURN ST_SetSRID(ST_MakePoint((p_ref->>'lon')::double precision, (p_ref->>'lat')::double precision), 4326);
                END IF;

                found_id := ee_place_id(p_ref);
                IF found_id IS NULL THEN
                    RETURN NULL;
                END IF;
                SELECT geometry, centroid::geometry AS centroid, radius_km INTO place FROM places WHERE place_id = found_id;
                IF p_kind = 'point' THEN
                    RETURN place.centroid;
                END IF;
                IF p_kind <> 'polygon' THEN
                    RAISE EXCEPTION 'unsupported place geometry kind %', p_kind;
                END IF;
                area := place.geometry;
                IF GeometryType(area) = 'POINT' THEN
                    -- A settlement is a point in the gazetteer: its hromada is the area an alert or a threat covers.
                    SELECT h.geometry INTO area FROM places h
                    WHERE h.level = 3 AND ST_Intersects(h.geometry, place.geometry)
                    ORDER BY ST_Area(h.geometry)
                    LIMIT 1;
                    IF area IS NULL THEN
                        area := ST_Buffer(place.centroid::geography, greatest(place.radius_km, 1) * 1000)::geometry;
                    END IF;
                END IF;
                IF GeometryType(area) = 'MULTIPOLYGON' THEN
                    -- Polygon fields are Polygon columns; the mainland part of a region is what the map needs.
                    SELECT d.geom INTO area FROM ST_Dump(area) d ORDER BY ST_Area(d.geom) DESC LIMIT 1;
                END IF;
                RETURN area;
            END
            $function$;

            REVOKE ALL ON FUNCTION ee_place_id(jsonb) FROM PUBLIC;
            REVOKE ALL ON FUNCTION ee_place_geometry(jsonb, text) FROM PUBLIC;
            DO $grant$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_ee') THEN
                    GRANT EXECUTE ON FUNCTION ee_place_geometry(jsonb, text) TO puluj_ee;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'puluj_admin') THEN
                    GRANT EXECUTE ON FUNCTION ee_place_geometry(jsonb, text) TO puluj_admin;
                END IF;
            END
            $grant$;

            -- A launch is where it started: the direction is rarely known, and without one a line cannot be drawn.
            DO $launch$
            BEGIN
                IF (SELECT type FROM geometry_columns WHERE f_table_name = 'ee_launches' AND f_geometry_column = 'geometry') = 'LINESTRING' THEN
                    ALTER TABLE ee_launches ALTER COLUMN geometry TYPE geometry(Point, 4326) USING ST_StartPoint(geometry);
                END IF;
            END
            $launch$;
            UPDATE ee_entity_definitions
            SET fields = (SELECT jsonb_agg(CASE WHEN f->>'name' = 'geometry' THEN f || '{"type":"point"}'::jsonb ELSE f END ORDER BY o)
                          FROM jsonb_array_elements(fields) WITH ORDINALITY AS e(f, o)),
                map_settings = coalesce(map_settings, '{}'::jsonb) || '{"renderer":"icon"}'::jsonb,
                updated_at = now()
            WHERE table_name = 'ee_launches' AND fields @> '[{"name":"geometry","type":"line"}]'::jsonb;

            ALTER TABLE ee_alerts ADD COLUMN IF NOT EXISTS state_key text;
            CREATE INDEX IF NOT EXISTS ix_ee_alerts_state_key_occurred_at ON ee_alerts (state_key, occurred_at DESC);
            UPDATE ee_entity_definitions
            SET fields = fields || '[{"name":"stateKey","type":"text","required":false}]'::jsonb, updated_at = now()
            WHERE table_name = 'ee_alerts' AND NOT fields @> '[{"name":"stateKey"}]'::jsonb;
            """);

        foreach (var table in EntityTables)
        {
            migrationBuilder.Sql($"""
                DO $table$
                BEGIN
                    IF to_regclass('public.{table}') IS NOT NULL THEN
                        UPDATE {table} t SET occurred_at = r.published_at
                        FROM raw_messages r
                        WHERE r.raw_message_id = t.raw_message_id AND t.occurred_at IS NULL;
                        CREATE INDEX IF NOT EXISTS ix_{table}_occurred_at ON {table} (occurred_at DESC);
                    END IF;
                END
                $table$;
                """);
        }

        // The map colours and the alert state: keyField picks the latest row per area, statusField hides the ended.
        migrationBuilder.Sql("""
            UPDATE ee_entity_definitions d
            SET map_settings = coalesce(d.map_settings, '{}'::jsonb) || s.settings, updated_at = now()
            FROM (VALUES
                ('ee_alerts', '{"keyField":"stateKey","statusField":"status","lifetimeMinutes":1440,"color":"#ef4444","opacity":0.25,"width":1}'::jsonb),
                ('ee_targets', '{"statusField":"status","color":"#dc2626"}'::jsonb),
                ('ee_tracks', '{"color":"#f97316","width":2,"dash":"dashed"}'::jsonb),
                ('ee_explosions', '{"color":"#b91c1c"}'::jsonb),
                ('ee_impacts', '{"color":"#7c2d12"}'::jsonb),
                ('ee_air_defense_actions', '{"color":"#2563eb"}'::jsonb),
                ('ee_launches', '{"color":"#9333ea"}'::jsonb),
                ('ee_takeoffs', '{"color":"#0f766e"}'::jsonb)
            ) AS s(table_name, settings)
            WHERE d.table_name = s.table_name;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE ee_entity_definitions
            SET map_settings = map_settings - 'keyField' - 'statusField' - 'lifetimeMinutes' - 'color' - 'opacity' - 'width' - 'dash',
                updated_at = now()
            WHERE table_name IN ('ee_alerts', 'ee_targets', 'ee_tracks', 'ee_explosions', 'ee_impacts', 'ee_air_defense_actions', 'ee_launches', 'ee_takeoffs');
            UPDATE ee_entity_definitions
            SET fields = (SELECT coalesce(jsonb_agg(f), '[]'::jsonb) FROM jsonb_array_elements(fields) f WHERE f->>'name' <> 'stateKey'),
                updated_at = now()
            WHERE table_name = 'ee_alerts';
            ALTER TABLE ee_launches ALTER COLUMN geometry TYPE geometry(LineString, 4326) USING NULL;
            UPDATE ee_entity_definitions
            SET fields = (SELECT jsonb_agg(CASE WHEN f->>'name' = 'geometry' THEN f || '{"type":"line"}'::jsonb ELSE f END ORDER BY o)
                          FROM jsonb_array_elements(fields) WITH ORDINALITY AS e(f, o)),
                map_settings = map_settings || '{"renderer":"line"}'::jsonb
            WHERE table_name = 'ee_launches';
            DROP INDEX IF EXISTS ix_ee_alerts_state_key_occurred_at;
            ALTER TABLE ee_alerts DROP COLUMN IF EXISTS state_key;
            DROP FUNCTION IF EXISTS ee_place_geometry(jsonb, text);
            DROP FUNCTION IF EXISTS ee_place_id(jsonb);
            """);
        foreach (var table in EntityTables)
            migrationBuilder.Sql($"DROP INDEX IF EXISTS ix_{table}_occurred_at;");
    }
}
