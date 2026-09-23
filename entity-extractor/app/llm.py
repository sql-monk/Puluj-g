from __future__ import annotations

import json
import time
from dataclasses import dataclass
from decimal import Decimal
from typing import Any

import httpx

from .models import CapturedWrite, EntityDefinition, ExtractRequest, LlmSettings
from .repository import LlmAudit

ANTHROPIC = "anthropic"
OPENAI = "openai"
OLLAMA = "ollama"
PROVIDERS = {ANTHROPIC: "Anthropic", OPENAI: "OpenAI", OLLAMA: "Ollama"}
DEFAULT_BASE_URLS = {
    ANTHROPIC: "https://api.anthropic.com",
    OPENAI: "https://api.openai.com/v1",
    OLLAMA: "http://localhost:11434/v1",
}
API_KEY_VARIABLES = {ANTHROPIC: "ANTHROPIC_API_KEY", OPENAI: "OPENAI_API_KEY"}


def provider_of(settings: LlmSettings) -> str | None:
    """Anthropic | OpenAI | Ollama, case-insensitive; empty means Anthropic, anything else is None."""
    value = (settings.provider or "").strip().lower() or ANTHROPIC
    return value if value in PROVIDERS else None


class LlmError(RuntimeError):
    def __init__(self, message: str, audit: LlmAudit, retryable: bool = True):
        super().__init__(message)
        self.audit = audit
        self.retryable = retryable  # False for configuration errors another attempt would only repeat


@dataclass(slots=True)
class LlmResult:
    writes: list[CapturedWrite]
    audit: LlmAudit


class EntityLlmExtractor:
    """The LLM fallback over the provider chosen by Llm:Provider: the Anthropic Messages API, or the OpenAI
    chat-completions API that OpenAI and Ollama (its /v1 endpoint) both speak. The provider is read per request,
    so switching it in the admin UI needs no restart."""

    def __init__(
        self,
        anthropic_base_url: str = DEFAULT_BASE_URLS[ANTHROPIC],
        environment_api_keys: dict[str, str | None] | None = None,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self.anthropic_base_url = anthropic_base_url.rstrip("/")
        self.environment_api_keys = {key.lower(): value for key, value in (environment_api_keys or {}).items()}
        self.transport = transport  # a test seam; production uses the default network transport

    def intent(
        self,
        request: ExtractRequest,
        definitions: list[EntityDefinition],
        settings: LlmSettings,
    ) -> LlmAudit:
        system_prompt = self._system_prompt(definitions)
        request_text = request.text or json.dumps(request.raw_payload, ensure_ascii=False)
        payload = self._payload(provider_of(settings) or ANTHROPIC, settings, system_prompt, request_text)
        return self._audit(
            settings, request_text, system_prompt, payload, None, "started", None, time.monotonic(), None
        )

    async def extract(
        self,
        request: ExtractRequest,
        definitions: list[EntityDefinition],
        settings: LlmSettings,
    ) -> LlmResult | None:
        if not settings.enabled:
            return None
        provider = provider_of(settings)
        intent = self.intent(request, definitions, settings)
        system_prompt = intent.system_prompt
        request_text = intent.request_text
        payload = intent.request_payload
        started = time.monotonic()

        def fail(error: str) -> LlmError:
            audit = self._audit(settings, request_text, system_prompt, payload, None, "failure", None, started, error)
            return LlmError(error, audit, retryable=False)

        if provider is None:
            raise fail(f"Llm:Provider '{settings.provider}' is not one of Anthropic, OpenAI, Ollama")
        api_key = settings.api_key or self.environment_api_keys.get(provider)
        if provider in API_KEY_VARIABLES and not api_key:
            raise fail(f"Llm:ApiKey / {API_KEY_VARIABLES[provider]} is not configured")
        url, headers = self._endpoint(provider, settings, api_key)
        try:
            async with httpx.AsyncClient(timeout=settings.timeout_seconds, transport=self.transport) as client:
                response = await client.post(url, headers=headers, json=payload)
            try:
                response_payload = response.json()
            except ValueError:
                response_payload = {"body": response.text}
            if not response.is_success:
                detail = _error_message(response_payload)
                error = f"LLM provider returned HTTP {response.status_code}" + (f": {detail}" if detail else "")
                audit = self._audit(
                    settings,
                    request_text,
                    system_prompt,
                    payload,
                    response_payload,
                    "failure",
                    response.status_code,
                    started,
                    error,
                )
                raise LlmError(error, audit)
            response_text: str | None = None
            try:
                response_text = self._response_text(provider, response_payload)
                writes = self._parse_writes(response_text)
            except (ValueError, TypeError, json.JSONDecodeError) as exc:
                audit = self._audit(
                    settings,
                    request_text,
                    system_prompt,
                    payload,
                    response_payload,
                    "invalid_response",
                    response.status_code,
                    started,
                    str(exc),
                    response_text,
                )
                raise LlmError(f"invalid LLM entity response: {exc}", audit) from exc
            return LlmResult(
                writes=writes,
                audit=self._audit(
                    settings,
                    request_text,
                    system_prompt,
                    payload,
                    response_payload,
                    "success" if writes else "empty",
                    response.status_code,
                    started,
                    None,
                    response_text,
                ),
            )
        except httpx.TimeoutException as exc:
            audit = self._audit(
                settings,
                request_text,
                system_prompt,
                payload,
                None,
                "timeout",
                None,
                started,
                str(exc),
            )
            raise LlmError("LLM request timed out", audit) from exc
        except httpx.HTTPError as exc:
            audit = self._audit(
                settings,
                request_text,
                system_prompt,
                payload,
                None,
                "failure",
                None,
                started,
                f"{url}: {exc}",
            )
            raise LlmError(f"LLM request failed: {exc}", audit) from exc

    def _endpoint(self, provider: str, settings: LlmSettings, api_key: str | None) -> tuple[str, dict[str, str]]:
        if provider == ANTHROPIC:
            return f"{self.anthropic_base_url}/v1/messages", {
                "x-api-key": api_key or "",
                "anthropic-version": "2023-06-01",
                "content-type": "application/json",
            }
        base_url = (settings.base_url or "").strip() or DEFAULT_BASE_URLS[provider]
        headers = {"content-type": "application/json"}
        if api_key:
            headers["authorization"] = f"Bearer {api_key}"
        return f"{base_url.rstrip('/')}/chat/completions", headers

    @staticmethod
    def _payload(provider: str, settings: LlmSettings, system_prompt: str, request_text: str) -> dict[str, Any]:
        max_tokens = max(64, min(settings.max_output_tokens, 8192))
        if provider == ANTHROPIC:
            return {
                "model": settings.model,
                "max_tokens": max_tokens,
                "system": system_prompt,
                "messages": [{"role": "user", "content": request_text}],
            }
        payload: dict[str, Any] = {
            "model": settings.model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": request_text},
            ],
            # The registry is dynamic, so no fixed schema: JSON mode, the shape is checked by _parse_writes.
            "response_format": {"type": "json_object"},
        }
        if provider == OLLAMA:
            payload["max_tokens"] = max_tokens
            payload["temperature"] = 0
        else:
            payload["max_completion_tokens"] = max_tokens  # current OpenAI models reject max_tokens
        return payload

    @staticmethod
    def _system_prompt(definitions: list[EntityDefinition]) -> str:
        schema = [
            {
                "table": definition.table_name,
                "fields": [
                    {"name": field.name, "type": field.type}
                    for field in definition.fields
                    if field.name not in {"rawMessageId", f"{definition.entity_name}Id"}
                ],
            }
            for definition in definitions
        ]
        return (
            "Extract only concrete entities supported by the supplied registry. "
            "Return strict JSON as {\"entities\":[{\"table\":\"ee_explosions\",\"values\":{...}}]}. "
            "Return {\"entities\":[]} when the message contains no supported entity. Do not invent facts.\n"
            f"Registry: {json.dumps(schema, ensure_ascii=False)}"
        )

    @staticmethod
    def _response_text(provider: str, payload: dict[str, Any]) -> str:
        if provider == ANTHROPIC:
            content = payload.get("content")
            if not isinstance(content, list):
                raise ValueError("provider response has no content array")
            text = "".join(
                item.get("text", "") for item in content if isinstance(item, dict) and item.get("type") == "text"
            ).strip()
        else:
            choices = payload.get("choices")
            if not isinstance(choices, list) or not choices or not isinstance(choices[0], dict):
                raise ValueError("provider response has no choices")
            message = choices[0].get("message")
            if not isinstance(message, dict):
                raise ValueError("provider response has no message")
            if isinstance(message.get("refusal"), str) and message["refusal"]:
                raise ValueError(f"the model refused: {message['refusal']}")
            text = message.get("content") if isinstance(message.get("content"), str) else ""
            text = text.strip()
        if not text:
            raise ValueError("provider response contains no text")
        return text

    @staticmethod
    def _parse_writes(text: str) -> list[CapturedWrite]:
        stripped = text.strip()
        if stripped.startswith("```"):
            lines = stripped.splitlines()
            stripped = "\n".join(lines[1:-1])
        payload = json.loads(stripped)
        if not isinstance(payload, dict) or not isinstance(payload.get("entities"), list):
            raise ValueError("response must contain an entities array")
        writes: list[CapturedWrite] = []
        for item in payload["entities"]:
            if not isinstance(item, dict) or not isinstance(item.get("table"), str) or not isinstance(item.get("values"), dict):
                raise ValueError("each entity must contain string table and object values")
            writes.append(CapturedWrite(table=item["table"], values=item["values"]))
        return writes

    @staticmethod
    def _usage(provider: str, response_payload: dict[str, Any] | None) -> tuple[int | None, int | None, int | None, int | None]:
        """(input, cache write, cache read, output) in Anthropic's terms: input excludes cached reads."""
        usage = response_payload.get("usage", {}) if isinstance(response_payload, dict) else {}
        if not isinstance(usage, dict):
            return None, None, None, None
        if provider == ANTHROPIC:
            return (
                _optional_int(usage.get("input_tokens")),
                _optional_int(usage.get("cache_creation_input_tokens")),
                _optional_int(usage.get("cache_read_input_tokens")),
                _optional_int(usage.get("output_tokens")),
            )
        prompt = _optional_int(usage.get("prompt_tokens"))
        details = usage.get("prompt_tokens_details")
        cached = _optional_int(details.get("cached_tokens")) if isinstance(details, dict) else None
        input_tokens = None if prompt is None else max(0, prompt - (cached or 0))
        return input_tokens, None, cached, _optional_int(usage.get("completion_tokens"))

    @staticmethod
    def _audit(
        settings: LlmSettings,
        request_text: str,
        system_prompt: str,
        request_payload: dict[str, Any],
        response_payload: dict[str, Any] | None,
        outcome: str,
        status_code: int | None,
        started: float,
        error: str | None,
        response_text: str | None = None,
    ) -> LlmAudit:
        provider = provider_of(settings)
        input_tokens, cache_creation, cache_read, output_tokens = EntityLlmExtractor._usage(
            provider or ANTHROPIC, response_payload
        )
        cost = None
        if input_tokens is not None:
            cost = Decimal(0) if provider == OLLAMA else (  # a local model: nothing billed
                Decimal(input_tokens) * Decimal(str(settings.input_price))
                + Decimal(cache_creation or 0) * Decimal(str(settings.cache_write_price))
                + Decimal(cache_read or 0) * Decimal(str(settings.cache_read_price))
                + Decimal(output_tokens or 0) * Decimal(str(settings.output_price))
            ) / Decimal(1_000_000)
        return LlmAudit(
            model=settings.model if provider in {None, ANTHROPIC} else f"{provider}/{settings.model}",
            outcome=outcome,
            status_code=status_code,
            duration_ms=int((time.monotonic() - started) * 1000),
            input_tokens=input_tokens,
            cache_creation_input_tokens=cache_creation,
            cache_read_input_tokens=cache_read,
            output_tokens=output_tokens,
            estimated_cost_usd=cost,
            request_text=request_text,
            system_prompt=system_prompt,
            response_text=response_text,
            request_payload=request_payload,
            response_payload=response_payload,
            error=error,
        )


def _error_message(payload: Any) -> str | None:
    """{"error":{"type":..,"message":..}} (Anthropic, OpenAI) or {"error":"..."} (Ollama)."""
    error = payload.get("error") if isinstance(payload, dict) else None
    if isinstance(error, str):
        return error
    if isinstance(error, dict) and isinstance(error.get("message"), str):
        return f"{error['type']}: {error['message']}" if isinstance(error.get("type"), str) else error["message"]
    return None


def _optional_int(value: Any) -> int | None:
    return int(value) if isinstance(value, int) else None
