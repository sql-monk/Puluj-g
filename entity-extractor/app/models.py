from datetime import datetime
from typing import Annotated, Any
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class ExtractRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="ignore")

    delivery_id: Annotated[UUID, Field(alias="deliveryId")]
    raw_message_id: Annotated[int, Field(alias="rawMessageId", ge=1)]
    source_id: Annotated[int, Field(alias="sourceId", ge=1)]
    source_code: Annotated[str | None, Field(alias="sourceCode", max_length=128)] = None
    published_at: Annotated[datetime | None, Field(alias="publishedAt")] = None
    received_at: Annotated[datetime | None, Field(alias="receivedAt")] = None
    text: Annotated[str | None, Field(max_length=2_000_000)] = None
    raw_payload: Annotated[Any | None, Field(alias="rawPayload")] = None
    url: Annotated[str | None, Field(max_length=4_096)] = None

    def extractor_message(self) -> dict[str, Any]:
        return self.model_dump(by_alias=True, mode="json", exclude={"delivery_id"})


class ValidateRequest(BaseModel):
    code: str = Field(min_length=1, max_length=1_000_000)


class TestRequest(ValidateRequest):
    message: dict[str, Any]
    timeout_ms: Annotated[int | None, Field(alias="timeoutMs", ge=100, le=60_000)] = None


class Diagnostic(BaseModel):
    line: int
    column: int
    message: str


class ValidateResponse(BaseModel):
    valid: bool
    diagnostics: list[Diagnostic] = Field(default_factory=list)


class CapturedWrite(BaseModel):
    table: str
    values: dict[str, Any]


class TestResponse(BaseModel):
    result: int | None
    writes: list[CapturedWrite] = Field(default_factory=list)
    stdout: str = ""
    stderr: str = ""
    duration_ms: int = Field(alias="durationMs")
    error: str | None = None


class ExtractorDefinition(BaseModel):
    extractor_id: int
    name: str
    code: str
    timeout_ms: int


class EntityField(BaseModel):
    name: str
    type: str
    column_name: str | None = None
    required: bool = False


class EntityDefinition(BaseModel):
    entity_definition_id: int
    entity_name: str
    table_name: str
    fields: list[EntityField]


class LlmSettings(BaseModel):
    enabled: bool = False
    provider: str | None = None  # Anthropic | OpenAI | Ollama; None = the Llm__Provider environment fallback
    model: str = "claude-opus-5"
    api_key: str | None = None
    base_url: str | None = None  # OpenAI-compatible endpoint (OpenAI, Ollama); None = the provider default
    timeout_seconds: int = 20
    max_output_tokens: int = 1024
    max_calls_per_minute: int = 20
    max_message_age_hours: float = 72
    failure_pause_seconds: float = 900
    max_attempts: int = 3
    input_price: float = 5.0
    output_price: float = 25.0
    cache_write_price: float = 6.25
    cache_read_price: float = 0.5
