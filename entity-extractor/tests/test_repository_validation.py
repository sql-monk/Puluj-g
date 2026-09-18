from datetime import datetime, timezone

import pytest

from app.models import EntityDefinition, EntityField
from app.repository import (
    build_llm_settings,
    index_entity_definitions,
    resolve_entity_definition,
    validate_write,
)


def definition() -> EntityDefinition:
    return EntityDefinition(
        entity_definition_id=1,
        entity_name="explosion",
        table_name="ee_explosions",
        fields=[
            EntityField(name="place", type="text", required=True),
            EntityField(name="confidence", type="decimal"),
            EntityField(name="occurredAt", column_name="occurred_at", type="datetime"),
            EntityField(name="position", type="point"),
        ],
    )


def test_registered_values_are_converted() -> None:
    columns, values, expressions = validate_write(
        definition(),
        {
            "place": "Київ",
            "confidence": 0.9,
            "occurredAt": "2026-09-18T05:00:00Z",
            "position": {"type": "Point", "coordinates": [30.52, 50.45]},
        },
    )

    assert columns == ["place", "confidence", "occurred_at", "position"]
    assert values[0] == "Київ"
    assert values[2] == datetime(2026, 9, 18, 5, tzinfo=timezone.utc)
    assert len(expressions) == 4


@pytest.mark.parametrize(
    ("values", "message"),
    [
        ({"unknown": 1, "place": "Київ"}, "unknown fields"),
        ({"place": 1}, "must be text"),
        ({"confidence": 0.5}, "missing required"),
        ({"place": "Київ", "position": {"type": "LineString", "coordinates": []}}, "GeoJSON Point"),
    ],
)
def test_invalid_writes_are_rejected(values: dict, message: str) -> None:
    with pytest.raises(ValueError, match=message):
        validate_write(definition(), values)
def test_table_must_use_entity_extractor_prefix() -> None:
    invalid = definition().model_copy(update={"table_name": "legacy_explosions"})

    with pytest.raises(ValueError, match="invalid registered table"):
        validate_write(invalid, {"place": "Київ"})


@pytest.mark.parametrize("alias", ["ee_explosions", "explosions", "explosion"])
def test_registered_entity_resolves_physical_and_logical_aliases(alias: str) -> None:
    definitions = index_entity_definitions([definition()])

    resolved = resolve_entity_definition(definitions, alias)

    assert resolved.table_name == "ee_explosions"


def test_unregistered_entity_alias_is_rejected() -> None:
    definitions = index_entity_definitions([definition()])

    with pytest.raises(ValueError, match="not registered and enabled"):
        resolve_entity_definition(definitions, "targets")


def test_llm_database_settings_override_environment_fallbacks() -> None:
    fallback = build_llm_settings({}, True, "compose-model")
    overridden = build_llm_settings(
        {"Llm:Enabled": "false", "Llm:Model": "database-model"},
        True,
        "compose-model",
    )

    assert fallback.enabled is True
    assert fallback.model == "compose-model"
    assert overridden.enabled is False
    assert overridden.model == "database-model"
