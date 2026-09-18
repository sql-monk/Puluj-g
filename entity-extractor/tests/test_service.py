from datetime import datetime, timezone
from decimal import Decimal

import pytest

from app.config import Settings
from app.llm import LlmError, LlmResult
from app.models import (
    CapturedWrite,
    EntityDefinition,
    EntityField,
    ExtractorDefinition,
    ExtractRequest,
    LlmSettings,
)
from app.repository import LlmAudit, ProcessingClaim
from app.runtime import RuntimeResult
from app.service import EntityExtractorService, ExtractionFailed
from tests.fakes import FakeRepository


class FakeLlm:
    def __init__(self, result: LlmResult | None = None) -> None:
        self.result = result
        self.calls = 0

    def intent(self, request, definitions, settings):
        started = audit()
        started.outcome = "started"
        return started

    async def extract(self, request, definitions, settings):
        self.calls += 1
        return self.result


class RetryLlm(FakeLlm):
    async def extract(self, request, definitions, settings):
        self.calls += 1
        if self.calls < 3:
            failed = audit()
            failed.outcome = "failure"
            failed.status_code = 503
            raise LlmError("temporary provider failure", failed)
        return self.result


def request() -> ExtractRequest:
    return ExtractRequest(deliveryId="11111111-1111-1111-1111-111111111111", rawMessageId=42, sourceId=2, text="Вибух у Києві")


def extractor(extractor_id: int, name: str) -> ExtractorDefinition:
    return ExtractorDefinition(
        extractor_id=extractor_id,
        name=name,
        code="def extract(message, write): pass",
        timeout_ms=1000,
    )


def audit() -> LlmAudit:
    return LlmAudit(
        model="test",
        outcome="success",
        status_code=200,
        duration_ms=5,
        input_tokens=1,
        cache_creation_input_tokens=0,
        cache_read_input_tokens=0,
        output_tokens=1,
        estimated_cost_usd=Decimal("0.01"),
        request_text="text",
        system_prompt="system",
        response_text='{"entities":[]}',
        request_payload={},
        response_payload={},
        error=None,
    )


async def test_one_committed_extractor_write_wins_over_another_failure(monkeypatch) -> None:
    repository = FakeRepository()
    repository.extractors = [extractor(1, "good"), extractor(2, "bad")]
    results = iter(
        [
            RuntimeResult(writes=[CapturedWrite(table="ee_explosions", values={"place": "Київ"})]),
            RuntimeResult(error="boom"),
        ]
    )

    async def execute(*args, **kwargs):
        return next(results)

    monkeypatch.setattr("app.service.execute_extractor", execute)
    llm = FakeLlm()
    service = EntityExtractorService(repository, Settings(), llm)  # type: ignore[arg-type]

    assert await service.process(request()) == 1
    assert repository.completed == 1
    assert repository.failures == ["bad: boom"]
    assert llm.calls == 0


async def test_rule_failure_without_write_is_technical_error(monkeypatch) -> None:
    repository = FakeRepository()
    repository.extractors = [extractor(1, "bad")]

    async def execute(*args, **kwargs):
        return RuntimeResult(error="boom")

    monkeypatch.setattr("app.service.execute_extractor", execute)
    service = EntityExtractorService(repository, Settings(), FakeLlm())  # type: ignore[arg-type]

    with pytest.raises(ExtractionFailed, match="boom"):
        await service.process(request())
    assert repository.completed is None
    assert "boom" in (repository.processing_error or "")


async def test_true_zero_runs_llm_and_commits_its_write(monkeypatch) -> None:
    repository = FakeRepository()
    repository.llm_settings = LlmSettings(enabled=True)
    repository.extractors = [extractor(1, "empty")]
    repository.definitions = [
        EntityDefinition(
            entity_definition_id=1,
            entity_name="explosion",
            table_name="ee_explosions",
            fields=[EntityField(name="place", type="text")],
        )
    ]

    async def execute(*args, **kwargs):
        return RuntimeResult()

    monkeypatch.setattr("app.service.execute_extractor", execute)
    llm = FakeLlm(
        LlmResult(
            writes=[CapturedWrite(table="ee_explosions", values={"place": "Київ"})],
            audit=audit(),
        )
    )
    service = EntityExtractorService(
        repository,
        Settings(llm_enabled=True, llm_model="compose-model"),
        llm,
    )  # type: ignore[arg-type]

    assert await service.process(request()) == 1
    assert repository.completed == 1
    assert repository.llm_audits[0].outcome == "success"
    assert repository.llm_fallback == (True, "compose-model")


async def test_old_message_is_not_sent_to_llm(monkeypatch) -> None:
    repository = FakeRepository()
    repository.llm_settings = LlmSettings(enabled=True, max_message_age_hours=1)
    llm = FakeLlm()
    service = EntityExtractorService(repository, Settings(), llm)  # type: ignore[arg-type]
    old = request().model_copy(update={"published_at": datetime(2020, 1, 1, tzinfo=timezone.utc)})

    assert await service.process(old) == 0
    assert llm.calls == 0
    assert repository.completed == 0


async def test_llm_rate_limit_suppresses_excess_calls() -> None:
    repository = FakeRepository()
    repository.llm_settings = LlmSettings(enabled=True, max_calls_per_minute=1)
    llm = FakeLlm()
    service = EntityExtractorService(repository, Settings(), llm)  # type: ignore[arg-type]

    assert await service.process(request()) == 0
    repository.completed = None
    with pytest.raises(ExtractionFailed, match="rate limit"):
        await service.process(request().model_copy(update={"delivery_id": "22222222-2222-2222-2222-222222222222"}))
    assert llm.calls == 1
    assert repository.processing_error == "LLM rate limit is exhausted"


async def test_llm_retries_transient_failures_up_to_max_attempts(monkeypatch) -> None:
    repository = FakeRepository()
    repository.llm_settings = LlmSettings(enabled=True, max_attempts=3, max_calls_per_minute=10)
    repository.definitions = [
        EntityDefinition(
            entity_definition_id=1,
            entity_name="explosion",
            table_name="ee_explosions",
            fields=[EntityField(name="place", type="text")],
        )
    ]
    monkeypatch.setattr("app.service.asyncio.sleep", lambda _: _completed_sleep())
    llm = RetryLlm(
        LlmResult(
            writes=[CapturedWrite(table="ee_explosions", values={"place": "Київ"})],
            audit=audit(),
        )
    )
    service = EntityExtractorService(repository, Settings(), llm)  # type: ignore[arg-type]

    assert await service.process(request()) == 1
    assert llm.calls == 3
    assert [row.outcome for row in repository.llm_audits] == ["failure", "failure", "success"]


async def _completed_sleep() -> None:
    return None


async def test_completed_delivery_returns_stored_scalar_without_execution(monkeypatch) -> None:
    repository = FakeRepository()
    repository.claim = ProcessingClaim(10, "completed", 1)

    async def execute(*args, **kwargs):
        raise AssertionError("extractor should not execute")

    monkeypatch.setattr("app.service.execute_extractor", execute)
    service = EntityExtractorService(repository, Settings(), FakeLlm())  # type: ignore[arg-type]

    assert await service.process(request()) == 1


async def test_recovery_skips_committed_extractor_steps(monkeypatch) -> None:
    repository = FakeRepository()
    repository.claim = ProcessingClaim(10, "recovered")
    repository.extractors = [extractor(1, "done"), extractor(2, "remaining")]
    repository.completed_steps = {1: 1}
    calls = 0

    async def execute(*args, **kwargs):
        nonlocal calls
        calls += 1
        return RuntimeResult()

    monkeypatch.setattr("app.service.execute_extractor", execute)
    service = EntityExtractorService(repository, Settings(), FakeLlm())  # type: ignore[arg-type]

    assert await service.process(request()) == 1
    assert calls == 1
