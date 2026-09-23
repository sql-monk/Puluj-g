from __future__ import annotations

import asyncio
import time
from collections import defaultdict, deque
from datetime import datetime, timedelta, timezone

from .config import Settings
from .llm import EntityLlmExtractor, LlmError
from .models import ExtractRequest
from .repository import Repository, index_entity_definitions, resolve_entity_definition, validate_write
from .runtime import execute_extractor


class ExtractionInProgress(RuntimeError):
    pass


class ExtractionFailed(RuntimeError):
    pass


class EntityExtractorService:
    def __init__(self, repository: Repository, settings: Settings, llm: EntityLlmExtractor) -> None:
        self.repository = repository
        self.settings = settings
        self.llm = llm
        self._semaphore = asyncio.Semaphore(settings.concurrency)
        self._delivery_locks: defaultdict[object, asyncio.Lock] = defaultdict(asyncio.Lock)
        self._active = 0
        self._llm_gate = asyncio.Lock()
        self._llm_calls: deque[float] = deque()
        self._llm_paused_until = 0.0

    @property
    def active_count(self) -> int:
        return self._active

    async def process(self, request: ExtractRequest) -> int:
        lock = self._delivery_locks[request.delivery_id]
        try:
            async with lock:
                async with self._semaphore:
                    self._active += 1
                    try:
                        return await self._process(request)
                    finally:
                        self._active -= 1
        finally:
            if not lock.locked():
                self._delivery_locks.pop(request.delivery_id, None)

    async def _process(self, request: ExtractRequest) -> int:
        claim = await self.repository.begin_processing(request)
        if claim.state == "completed" and claim.result in (0, 1):
            return int(claim.result)
        if claim.state not in {"new", "recovered"}:
            raise ExtractionInProgress(f"delivery {request.delivery_id} is already {claim.state}")

        completed_steps = await self.repository.completed_extractor_steps(claim.run_id)
        writes_count = sum(completed_steps.values())
        failures: list[str] = []
        extractors = await self.repository.list_extractors()
        for extractor in extractors:
            if extractor.extractor_id in completed_steps:
                continue
            result = await execute_extractor(
                extractor.code,
                request.extractor_message(),
                extractor.timeout_ms or self.settings.default_timeout_ms,
                self.settings.max_output_bytes,
                self.settings.max_memory_mb,
            )
            if result.error:
                await self.repository.record_extractor_failure(
                    claim.run_id,
                    extractor,
                    result.duration_ms,
                    result.error,
                    result.stdout,
                    result.stderr,
                )
                failures.append(f"{extractor.name}: {result.error}")
                continue
            try:
                committed = await self.repository.commit_extractor_success(
                    claim.run_id,
                    request.raw_message_id,
                    extractor,
                    result.writes,
                    result.duration_ms,
                    result.stdout,
                    result.stderr,
                )
                writes_count += len(committed)
            except Exception as exc:
                error = f"write failed: {exc}"
                await self.repository.record_extractor_failure(
                    claim.run_id,
                    extractor,
                    result.duration_ms,
                    error,
                    result.stdout,
                    result.stderr,
                )
                failures.append(f"{extractor.name}: {error}")

        if writes_count:
            await self.repository.complete_processing(claim.run_id, 1)
            return 1
        if failures:
            error = "; ".join(failures)
            await self.repository.fail_processing(claim.run_id, error)
            raise ExtractionFailed(error)

        definitions = await self.repository.list_entity_definitions()
        # The LLM section of the admin UI (app_settings Llm:*) wins; the Compose environment fills in what it never set.
        llm_settings = await self.repository.get_llm_settings(self.settings.llm_environment)
        if not llm_settings.enabled:
            await self.repository.complete_processing(claim.run_id, 0)
            return 0
        gate_reason = await self._llm_gate_reason(request, llm_settings)
        if gate_reason == "message is older than the configured LLM window":
            await self.repository.complete_processing(claim.run_id, 0)
            return 0
        if gate_reason is not None:
            await self.repository.fail_processing(claim.run_id, gate_reason)
            raise ExtractionFailed(gate_reason)
        attempt = 0
        while True:
            attempt += 1
            intent = self.llm.intent(request, definitions, llm_settings)
            try:
                audit_id = await self.repository.begin_llm_audit(claim.run_id, request, intent)
            except Exception as exc:
                await self.repository.fail_processing(claim.run_id, str(exc))
                raise ExtractionFailed(f"could not persist LLM request audit: {exc}") from exc
            try:
                llm_result = await self.llm.extract(request, definitions, llm_settings)
                break
            except LlmError as exc:
                await self.repository.audit_llm_failure(audit_id, exc.audit)
                retryable = exc.retryable and (exc.audit.status_code is None or exc.audit.status_code >= 500)
                if retryable and attempt < max(1, llm_settings.max_attempts) and await self._reserve_retry_call(llm_settings):
                    await asyncio.sleep(min(2 ** (attempt - 1), 5))
                    continue
                await self._record_llm_failure(exc.audit.status_code, llm_settings.failure_pause_seconds)
                await self.repository.fail_processing(claim.run_id, str(exc))
                raise ExtractionFailed(str(exc)) from exc
            except Exception as exc:
                intent.outcome = "failed"
                intent.error = str(exc)
                await self.repository.audit_llm_failure(audit_id, intent)
                await self.repository.fail_processing(claim.run_id, str(exc))
                raise ExtractionFailed(str(exc)) from exc
        if llm_result is None:
            intent.outcome = "empty"
            await self.repository.audit_llm_failure(audit_id, intent)
            await self.repository.complete_processing(claim.run_id, 0)
            return 0

        try:
            by_alias = index_entity_definitions(definitions)
            for captured in llm_result.writes:
                definition = resolve_entity_definition(by_alias, captured.table)
                validate_write(definition, captured.values)
            committed = await self.repository.commit_llm_result(
                claim.run_id, request, llm_result.writes, llm_result.audit, audit_id
            )
        except Exception as exc:
            llm_result.audit.outcome = "invalid_response"
            llm_result.audit.error = str(exc)
            try:
                await self.repository.audit_llm_failure(audit_id, llm_result.audit)
            finally:
                await self.repository.fail_processing(claim.run_id, str(exc))
            raise ExtractionFailed(str(exc)) from exc
        result = 1 if committed else 0
        async with self._llm_gate:
            self._llm_paused_until = 0.0
        return result

    async def _llm_gate_reason(self, request: ExtractRequest, settings) -> str | None:
        if settings.max_message_age_hours > 0 and request.published_at is not None:
            published = request.published_at
            if published.tzinfo is None:
                published = published.replace(tzinfo=timezone.utc)
            if datetime.now(timezone.utc) - published > timedelta(hours=settings.max_message_age_hours):
                return "message is older than the configured LLM window"
        now = time.monotonic()
        async with self._llm_gate:
            if now < self._llm_paused_until:
                return "LLM circuit breaker is paused"
            while self._llm_calls and now - self._llm_calls[0] >= 60:
                self._llm_calls.popleft()
            limit = max(1, settings.max_calls_per_minute)
            if len(self._llm_calls) >= limit:
                return "LLM rate limit is exhausted"
            self._llm_calls.append(now)
            return None

    async def _record_llm_failure(self, status_code: int | None, failure_pause_seconds: float) -> None:
        pause = 60.0 if status_code == 429 else max(0.0, failure_pause_seconds) if status_code in {400, 401, 403} else 0.0
        if pause <= 0:
            return
        async with self._llm_gate:
            self._llm_paused_until = max(self._llm_paused_until, time.monotonic() + pause)

    async def _reserve_retry_call(self, settings) -> bool:
        now = time.monotonic()
        async with self._llm_gate:
            while self._llm_calls and now - self._llm_calls[0] >= 60:
                self._llm_calls.popleft()
            if len(self._llm_calls) >= max(1, settings.max_calls_per_minute):
                return False
            self._llm_calls.append(now)
            return True
