from __future__ import annotations

from dataclasses import replace

from app.models import (
    CapturedWrite,
    EntityDefinition,
    ExtractorDefinition,
    ExtractRequest,
    LlmSettings,
)
from app.repository import LlmAudit, ProcessingClaim


class FakeRepository:
    def __init__(self) -> None:
        self.claim = ProcessingClaim(10, "new")
        self.extractors: list[ExtractorDefinition] = []
        self.definitions: list[EntityDefinition] = []
        self.llm_settings = LlmSettings(enabled=False)
        self.committed: list[CapturedWrite] = []
        self.failures: list[str] = []
        self.completed: int | None = None
        self.processing_error: str | None = None
        self.llm_audits: list[LlmAudit] = []
        self.completed_steps: dict[int, int] = {}
        self.llm_fallback: dict[str, str] | None = None

    async def health(self) -> bool:
        return True

    async def begin_processing(self, request: ExtractRequest) -> ProcessingClaim:
        return replace(self.claim)

    async def list_extractors(self) -> list[ExtractorDefinition]:
        return self.extractors

    async def completed_extractor_steps(self, run_id: int) -> dict[int, int]:
        return self.completed_steps

    async def list_entity_definitions(self) -> list[EntityDefinition]:
        return self.definitions

    async def commit_extractor_success(
        self,
        run_id: int,
        raw_message_id: int,
        extractor: ExtractorDefinition,
        writes: list[CapturedWrite],
        duration_ms: int,
        stdout: str,
        stderr: str,
    ) -> list[tuple[str, str]]:
        self.committed.extend(writes)
        return [(write.table, str(index + 1)) for index, write in enumerate(writes)]

    async def record_extractor_failure(
        self, run_id: int, extractor: ExtractorDefinition, duration_ms: int, error: str, stdout: str, stderr: str
    ) -> None:
        self.failures.append(f"{extractor.name}: {error}")

    async def get_llm_settings(self, fallback) -> LlmSettings:
        self.llm_fallback = dict(fallback)
        return self.llm_settings

    async def begin_llm_audit(self, run_id: int, request: ExtractRequest, audit: LlmAudit) -> int:
        self.llm_audits.append(audit)
        return len(self.llm_audits)

    async def commit_llm_result(
        self,
        run_id: int,
        request: ExtractRequest,
        writes: list[CapturedWrite],
        audit: LlmAudit,
        audit_id: int,
    ) -> list[tuple[str, str]]:
        self.llm_audits[audit_id - 1] = audit
        self.committed.extend(writes)
        self.completed = 1 if writes else 0
        return [(write.table, str(index + 1)) for index, write in enumerate(writes)]

    async def audit_llm_failure(self, audit_id: int, audit: LlmAudit) -> None:
        self.llm_audits[audit_id - 1] = audit

    async def complete_processing(self, run_id: int, result: int) -> None:
        self.completed = result

    async def fail_processing(self, run_id: int, error: str) -> None:
        self.processing_error = error
