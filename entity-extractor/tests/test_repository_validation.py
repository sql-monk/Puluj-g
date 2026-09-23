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
    environment = {"Llm:Enabled": "true", "Llm:Anthropic:Model": "compose-model"}
    fallback = build_llm_settings({}, environment)
    overridden = build_llm_settings({"Llm:Enabled": "false", "Llm:Anthropic:Model": "database-model"}, environment)

    assert fallback.enabled is True
    assert fallback.model == "compose-model"
    assert overridden.enabled is False
    assert overridden.model == "database-model"


def test_every_provider_keeps_its_own_key_and_the_switch_picks_one() -> None:
    rows = {
        "Llm:Enabled": "true",
        "Llm:Anthropic:ApiKey": "sk-ant",
        "Llm:Anthropic:Model": "claude-opus-5",
        "Llm:OpenAI:ApiKey": "sk-openai",
        "Llm:OpenAI:Model": "gpt-test",
        "Llm:OpenAI:InputUsdPerMillionTokens": "1.5",
        "Llm:Ollama:Model": "qwen3:8b",
        "Llm:Ollama:BaseUrl": "http://ollama:11434/v1",
    }

    anthropic = build_llm_settings({**rows, "Llm:Provider": "Anthropic"})
    openai = build_llm_settings({**rows, "Llm:Provider": "openai"})
    ollama = build_llm_settings({**rows, "Llm:Provider": "Ollama"})

    assert (anthropic.provider, anthropic.api_key, anthropic.model, anthropic.input_price) == ("Anthropic", "sk-ant", "claude-opus-5", 5)
    assert (openai.provider, openai.api_key, openai.model, openai.input_price) == ("OpenAI", "sk-openai", "gpt-test", 1.5)
    assert (ollama.provider, ollama.api_key, ollama.model, ollama.base_url) == ("Ollama", None, "qwen3:8b", "http://ollama:11434/v1")


def test_an_unknown_provider_is_kept_for_the_extractor_to_report() -> None:
    assert build_llm_settings({"Llm:Provider": "Gemini"}).provider == "Gemini"
    assert build_llm_settings({}).provider == "Anthropic"
