import pytest

from app.models import EntityDefinition, EntityField
from app.places import name_candidates, place_reference, region_candidates
from app.repository import required_columns, validate_write


@pytest.mark.parametrize(
    ("inflected", "variant"),
    [
        ("Носівку", "носівк"),
        ("Славутича", "славутич"),
        ("Кременчуці", "кременчук"),
        ("Коростеня", "коростень"),
        ("Києва", "київ"),
        ("Ірпеня", "ірпінь"),
        ("Ріпок", "ріпки"),
        ("Сумах", "суми"),
        ("Білої Церкви", "біл церкв"),
        ("Старого Салтова", "стар салтів"),
        ("Ніжинського району", "ніжинськ район"),
        ("м. Запоріжжя", "запоріжжя"),
    ],
)
def test_inflected_names_reach_the_gazetteer_variant(inflected: str, variant: str) -> None:
    assert variant in name_candidates(inflected)


def test_region_names_match_the_oblast_variants() -> None:
    assert {"харківськ обл", "харківськ"} <= set(region_candidates("Харківська обл."))
    assert "брянщин" in region_candidates("Брянщини")


def definition() -> EntityDefinition:
    return EntityDefinition(
        entity_definition_id=1,
        entity_name="target",
        table_name="ee_targets",
        fields=[EntityField(name="label", type="text"), EntityField(name="geometry", type="point"),
                EntityField(name="route", type="line")],
    )


def test_a_place_reference_is_resolved_by_the_database_on_insert() -> None:
    columns, values, expressions = validate_write(
        definition(), {"geometry": {"place": "Кременчук", "hint": ["Полтавська обл."], "required": True}}
    )
    assert columns == ["geometry"]
    assert '"names"' in values[0] and '"hints"' in values[0]
    assert "ee_place_geometry" in repr(expressions[0])
    assert required_columns(definition(), {"geometry": {"place": "Кременчук", "required": True}}) == ["geometry"]
    assert required_columns(definition(), {"geometry": {"place": "Кременчук"}}) == []


def test_a_line_reference_takes_places_or_coordinates() -> None:
    reference = place_reference("line", {"from": {"lon": 38.17, "lat": 46.05}, "to": {"place": "Одеса"}}, "route")
    assert reference["from"] == {"lon": 38.17, "lat": 46.05}
    assert "одес" in reference["to"]["names"]


@pytest.mark.parametrize(
    "value",
    [{"place": ""}, {"place": "Київ", "region": 1}, {"place": "Київ", "bogus": 1}, {"place": "Київ", "required": "yes"}],
)
def test_malformed_references_are_rejected(value: dict) -> None:
    with pytest.raises(ValueError):
        validate_write(definition(), {"geometry": value})
