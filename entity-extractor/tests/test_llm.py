import pytest

from app.llm import AnthropicEntityExtractor


def test_llm_response_maps_only_table_and_values() -> None:
    writes = AnthropicEntityExtractor._parse_writes(
        '{"entities":[{"table":"ee_explosions","values":{"label":"Вибух"}}]}'
    )

    assert len(writes) == 1
    assert writes[0].table == "ee_explosions"
    assert writes[0].values == {"label": "Вибух"}


def test_llm_empty_response_is_a_true_zero() -> None:
    assert AnthropicEntityExtractor._parse_writes('{"entities":[]}') == []


@pytest.mark.parametrize("payload", ["[]", '{"facts":[]}', '{"entities":[{"table":1,"values":{}}]}'])
def test_llm_response_contract_is_strict(payload: str) -> None:
    with pytest.raises(ValueError):
        AnthropicEntityExtractor._parse_writes(payload)
