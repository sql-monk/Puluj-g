import secrets
from contextlib import asynccontextmanager
from typing import AsyncIterator

from fastapi import Depends, FastAPI, Header, HTTPException, Response, status

from .config import Settings, get_settings
from .llm import EntityLlmExtractor
from .models import ExtractRequest, TestRequest, TestResponse, ValidateRequest, ValidateResponse
from .repository import (
    PostgresRepository,
    Repository,
    index_entity_definitions,
    resolve_entity_definition,
    validate_write,
)
from .runtime import execute_extractor, validate_code
from .service import EntityExtractorService, ExtractionFailed, ExtractionInProgress


def create_app(repository: Repository | None = None, settings: Settings | None = None) -> FastAPI:
    app_settings = settings or get_settings()
    app_repository = repository or PostgresRepository(app_settings.postgres_dsn)
    owns_repository = repository is None

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        if owns_repository:
            await app_repository.open()  # type: ignore[attr-defined]
        try:
            yield
        finally:
            if owns_repository:
                await app_repository.close()  # type: ignore[attr-defined]

    application = FastAPI(title="Puluj Entity Extractor", version="0.1.0", lifespan=lifespan)
    llm = EntityLlmExtractor(
        app_settings.anthropic_base_url,
        {"anthropic": app_settings.anthropic_api_key, "openai": app_settings.openai_api_key},
    )
    service = EntityExtractorService(app_repository, app_settings, llm)
    application.state.repository = app_repository
    application.state.extractor_service = service

    async def require_admin(x_admin_token: str | None = Header(default=None)) -> None:
        if app_settings.admin_token and not _token_matches(app_settings.admin_token, x_admin_token):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="invalid admin token")

    async def require_service(
        x_entity_extractor_token: str | None = Header(default=None, alias="X-Entity-Extractor-Token"),
    ) -> None:
        if app_settings.service_token and not _token_matches(app_settings.service_token, x_entity_extractor_token):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="invalid entity extractor token")

    @application.get("/health")
    async def health(response: Response) -> dict[str, object]:
        database = await app_repository.health()
        if not database:
            response.status_code = status.HTTP_503_SERVICE_UNAVAILABLE
        return {"status": "healthy" if database else "unhealthy", "database": database, "active": service.active_count}

    @application.post("/extract", response_model=int, dependencies=[Depends(require_service)])
    async def extract(request: ExtractRequest) -> int:
        try:
            return await service.process(request)
        except ExtractionInProgress as exc:
            raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
        except ExtractionFailed as exc:
            raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=str(exc)) from exc

    async def validate(request: ValidateRequest, _: None = Depends(require_admin)) -> ValidateResponse:
        return validate_code(request.code)

    async def test(request: TestRequest, _: None = Depends(require_admin)) -> TestResponse:
        result = await execute_extractor(
            request.code,
            request.message,
            request.timeout_ms or app_settings.default_timeout_ms,
            app_settings.max_output_bytes,
            app_settings.max_memory_mb,
        )
        error = result.error
        if error is None:
            try:
                definitions = index_entity_definitions(await app_repository.list_entity_definitions())
                for captured in result.writes:
                    validate_write(resolve_entity_definition(definitions, captured.table), captured.values)
            except ValueError as exc:
                error = str(exc)
        return TestResponse(
            result=None if error else (1 if result.writes else 0),
            writes=result.writes,
            stdout=result.stdout,
            stderr=result.stderr,
            durationMs=result.duration_ms,
            error=error,
        )

    application.post("/admin/validate", response_model=ValidateResponse)(validate)
    application.post("/admin/test", response_model=TestResponse)(test)
    application.post("/admin/extractors/validate", response_model=ValidateResponse, include_in_schema=False)(validate)
    application.post("/admin/extractors/test", response_model=TestResponse, include_in_schema=False)(test)
    return application


def _token_matches(expected: str, actual: str | None) -> bool:
    return secrets.compare_digest(expected.encode("utf-8"), (actual or "").encode("utf-8"))


app = create_app()
