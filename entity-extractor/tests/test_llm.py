import json

import httpx
import pytest

from app.llm import EntityLlmExtractor, LlmError
from app.models import EntityDefinition, EntityField, ExtractRequest, LlmSettings


def test_llm_response_maps_only_table_and_values() -> None:
    writes = EntityLlmExtractor._parse_writes(
        '{"entities":[{"table":"ee_explosions","values":{"label":"Вибух"}}]}'
    )

    assert len(writes) == 1
    assert writes[0].table == "ee_explosions"
    assert writes[0].values == {"label": "Вибух"}


def test_llm_empty_response_is_a_true_zero() -> None:
    assert EntityLlmExtractor._parse_writes('{"entities":[]}') == []


@pytest.mark.parametrize("payload", ["[]", '{"facts":[]}', '{"entities":[{"table":1,"values":{}}]}'])
def test_llm_response_contract_is_strict(payload: str) -> None:
    with pytest.raises(ValueError):
        EntityLlmExtractor._parse_writes(payload)


def request() -> ExtractRequest:
    return ExtractRequest(deliveryId="11111111-1111-1111-1111-111111111111", rawMessageId=42, sourceId=2, text="Вибух у Києві")


def definitions() -> list[EntityDefinition]:
    return [
        EntityDefinition(
            entity_definition_id=1,
            entity_name="explosion",
            table_name="ee_explosions",
            fields=[EntityField(name="label", type="text")],
        )
    ]


class Recorder:
    def __init__(self, status: int, body: dict) -> None:
        self.status = status
        self.body = body
        self.requests: list[httpx.Request] = []

    def transport(self) -> httpx.MockTransport:
        def handle(request: httpx.Request) -> httpx.Response:
            self.requests.append(request)
            return httpx.Response(self.status, json=self.body)

        return httpx.MockTransport(handle)


ANSWER = '{"entities":[{"table":"ee_explosions","values":{"label":"Вибух"}}]}'


async def test_openai_is_called_over_chat_completions_with_bearer_key() -> None:
    recorder = Recorder(
        200,
        {
            "id": "chatcmpl-1",
            "choices": [{"message": {"role": "assistant", "content": ANSWER, "refusal": None}}],
            "usage": {"prompt_tokens": 1200, "completion_tokens": 30, "prompt_tokens_details": {"cached_tokens": 1000}},
        },
    )
    llm = EntityLlmExtractor(environment_api_keys={"openai": "sk-env"}, transport=recorder.transport())
    settings = LlmSettings(enabled=True, provider="OpenAI", model="gpt-test", input_price=1, cache_read_price=0.1, output_price=4)

    result = await llm.extract(request(), definitions(), settings)

    assert result is not None
    assert result.writes[0].values == {"label": "Вибух"}
    sent = recorder.requests[0]
    assert str(sent.url) == "https://api.openai.com/v1/chat/completions"
    assert sent.headers["authorization"] == "Bearer sk-env"
    body = json.loads(sent.content)
    assert body["model"] == "gpt-test"
    assert body["messages"][0]["role"] == "system"
    assert body["response_format"] == {"type": "json_object"}
    assert "max_completion_tokens" in body and "max_tokens" not in body
    assert result.audit.model == "openai/gpt-test"
    assert (result.audit.input_tokens, result.audit.cache_read_input_tokens, result.audit.output_tokens) == (200, 1000, 30)
    assert result.audit.estimated_cost_usd is not None and float(result.audit.estimated_cost_usd) == pytest.approx(0.00042)


async def test_ollama_needs_no_key_and_costs_nothing() -> None:
    recorder = Recorder(
        200,
        {"choices": [{"message": {"role": "assistant", "content": ANSWER}}], "usage": {"prompt_tokens": 10, "completion_tokens": 5}},
    )
    llm = EntityLlmExtractor(transport=recorder.transport())
    settings = LlmSettings(enabled=True, provider="ollama", model="qwen3:8b", base_url="http://host.docker.internal:11434/v1/")

    result = await llm.extract(request(), definitions(), settings)

    assert result is not None
    sent = recorder.requests[0]
    assert str(sent.url) == "http://host.docker.internal:11434/v1/chat/completions"
    assert "authorization" not in sent.headers
    body = json.loads(sent.content)
    assert body["max_tokens"] and body["temperature"] == 0
    assert result.audit.model == "ollama/qwen3:8b"
    assert result.audit.estimated_cost_usd == 0


async def test_anthropic_stays_on_the_messages_api() -> None:
    recorder = Recorder(
        200,
        {"content": [{"type": "text", "text": ANSWER}], "usage": {"input_tokens": 100, "output_tokens": 20}},
    )
    llm = EntityLlmExtractor(environment_api_keys={"anthropic": "sk-ant"}, transport=recorder.transport())

    result = await llm.extract(request(), definitions(), LlmSettings(enabled=True, provider=None, model="claude-opus-5"))

    assert result is not None
    sent = recorder.requests[0]
    assert str(sent.url) == "https://api.anthropic.com/v1/messages"
    assert sent.headers["x-api-key"] == "sk-ant"
    assert result.audit.model == "claude-opus-5"
    assert result.audit.input_tokens == 100


async def test_provider_error_message_is_kept() -> None:
    recorder = Recorder(404, {"error": 'model "qwen3:8b" not found, try pulling it first'})
    llm = EntityLlmExtractor(transport=recorder.transport())

    with pytest.raises(LlmError) as failure:
        await llm.extract(request(), definitions(), LlmSettings(enabled=True, provider="Ollama", model="qwen3:8b"))

    assert failure.value.audit.status_code == 404
    assert 'model "qwen3:8b" not found' in str(failure.value)


async def test_openai_refusal_is_an_invalid_response() -> None:
    recorder = Recorder(200, {"choices": [{"message": {"role": "assistant", "content": None, "refusal": "no"}}]})
    llm = EntityLlmExtractor(environment_api_keys={"openai": "sk"}, transport=recorder.transport())

    with pytest.raises(LlmError) as failure:
        await llm.extract(request(), definitions(), LlmSettings(enabled=True, provider="OpenAI"))

    assert failure.value.audit.outcome == "invalid_response"


@pytest.mark.parametrize(
    ("settings", "message"),
    [
        (LlmSettings(enabled=True, provider="Gemini"), "not one of Anthropic, OpenAI, Ollama"),
        (LlmSettings(enabled=True, provider="OpenAI"), "OPENAI_API_KEY is not configured"),
    ],
)
async def test_configuration_errors_are_not_retried_and_never_call_out(settings: LlmSettings, message: str) -> None:
    recorder = Recorder(200, {})
    llm = EntityLlmExtractor(transport=recorder.transport())

    with pytest.raises(LlmError) as failure:
        await llm.extract(request(), definitions(), settings)

    assert message in str(failure.value)
    assert failure.value.retryable is False
    assert recorder.requests == []
