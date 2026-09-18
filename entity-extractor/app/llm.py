from __future__ import annotations

import json
import time
from dataclasses import dataclass
from decimal import Decimal
from typing import Any

import httpx

from .models import CapturedWrite, EntityDefinition, ExtractRequest, LlmSettings
from .repository import LlmAudit


class LlmError(RuntimeError):
    def __init__(self, message: str, audit: LlmAudit):
        super().__init__(message)
        self.audit = audit


@dataclass(slots=True)
class LlmResult:
    writes: list[CapturedWrite]
    audit: LlmAudit


class AnthropicEntityExtractor:
    def __init__(self, base_url: str, environment_api_key: str | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.environment_api_key = environment_api_key

    def intent(
        self,
        request: ExtractRequest,
        definitions: list[EntityDefinition],
        settings: LlmSettings,
    ) -> LlmAudit:
        system_prompt = self._system_prompt(definitions)
        request_text = request.text or json.dumps(request.raw_payload, ensure_ascii=False)
        payload = {
            "model": settings.model,
            "max_tokens": max(64, min(settings.max_output_tokens, 8192)),
            "system": system_prompt,
            "messages": [{"role": "user", "content": request_text}],
        }
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
        api_key = settings.api_key or self.environment_api_key
        intent = self.intent(request, definitions, settings)
        system_prompt = intent.system_prompt
        request_text = intent.request_text
        payload = intent.request_payload
        started = time.monotonic()
        if not api_key:
            audit = self._audit(
                settings,
                request_text,
                system_prompt,
                payload,
                None,
                "failure",
                None,
                started,
                "Llm:ApiKey / ANTHROPIC_API_KEY is not configured",
            )
            raise LlmError(audit.error or "LLM API key is missing", audit)
        try:
            async with httpx.AsyncClient(timeout=settings.timeout_seconds) as client:
                response = await client.post(
                    f"{self.base_url}/v1/messages",
                    headers={
                        "x-api-key": api_key,
                        "anthropic-version": "2023-06-01",
                        "content-type": "application/json",
                    },
                    json=payload,
                )
            try:
                response_payload = response.json()
            except ValueError:
                response_payload = {"body": response.text}
            if not response.is_success:
                error = f"LLM provider returned HTTP {response.status_code}"
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
                response_text = self._response_text(response_payload)
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
                str(exc),
            )
            raise LlmError(f"LLM request failed: {exc}", audit) from exc

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
    def _response_text(payload: dict[str, Any]) -> str:
        content = payload.get("content")
        if not isinstance(content, list):
            raise ValueError("provider response has no content array")
        text = "".join(
            item.get("text", "") for item in content if isinstance(item, dict) and item.get("type") == "text"
        ).strip()
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
        usage = response_payload.get("usage", {}) if isinstance(response_payload, dict) else {}
        input_tokens = _optional_int(usage.get("input_tokens"))
        cache_creation = _optional_int(usage.get("cache_creation_input_tokens"))
        cache_read = _optional_int(usage.get("cache_read_input_tokens"))
        output_tokens = _optional_int(usage.get("output_tokens"))
        cost = None
        if input_tokens is not None:
            cost = (
                Decimal(input_tokens) * Decimal(str(settings.input_price))
                + Decimal(cache_creation or 0) * Decimal(str(settings.cache_write_price))
                + Decimal(cache_read or 0) * Decimal(str(settings.cache_read_price))
                + Decimal(output_tokens or 0) * Decimal(str(settings.output_price))
            ) / Decimal(1_000_000)
        return LlmAudit(
            model=settings.model,
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


def _optional_int(value: Any) -> int | None:
    return int(value) if isinstance(value, int) else None
