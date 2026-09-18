from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Any, Protocol
from uuid import UUID

from psycopg import AsyncConnection, sql
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool

from .models import (
    CapturedWrite,
    EntityDefinition,
    EntityField,
    ExtractorDefinition,
    ExtractRequest,
    LlmSettings,
)

IDENTIFIER = re.compile(r"^[a-z][a-z0-9_]{0,62}$")


@dataclass(slots=True)
class ProcessingClaim:
    run_id: int
    state: str
    result: int | None = None


@dataclass(slots=True)
class LlmAudit:
    model: str
    outcome: str
    status_code: int | None
    duration_ms: int
    input_tokens: int | None
    cache_creation_input_tokens: int | None
    cache_read_input_tokens: int | None
    output_tokens: int | None
    estimated_cost_usd: Decimal | None
    request_text: str
    system_prompt: str
    response_text: str | None
    request_payload: dict[str, Any]
    response_payload: dict[str, Any] | None
    error: str | None


class Repository(Protocol):
    async def health(self) -> bool: ...

    async def begin_processing(self, request: ExtractRequest) -> ProcessingClaim: ...

    async def list_extractors(self) -> list[ExtractorDefinition]: ...

    async def completed_extractor_steps(self, run_id: int) -> dict[int, int]: ...

    async def list_entity_definitions(self) -> list[EntityDefinition]: ...

    async def commit_extractor_success(
        self,
        run_id: int,
        raw_message_id: int,
        extractor: ExtractorDefinition,
        writes: list[CapturedWrite],
        duration_ms: int,
        stdout: str,
        stderr: str,
    ) -> list[tuple[str, str]]: ...

    async def record_extractor_failure(
        self, run_id: int, extractor: ExtractorDefinition, duration_ms: int, error: str, stdout: str, stderr: str
    ) -> None: ...

    async def get_llm_settings(self, fallback_enabled: bool, fallback_model: str) -> LlmSettings: ...

    async def begin_llm_audit(self, run_id: int, request: ExtractRequest, audit: LlmAudit) -> int: ...

    async def commit_llm_result(
        self,
        run_id: int,
        request: ExtractRequest,
        writes: list[CapturedWrite],
        audit: LlmAudit,
        audit_id: int,
    ) -> list[tuple[str, str]]: ...

    async def audit_llm_failure(self, audit_id: int, audit: LlmAudit) -> None: ...

    async def complete_processing(self, run_id: int, result: int) -> None: ...

    async def fail_processing(self, run_id: int, error: str) -> None: ...


class PostgresRepository:
    def __init__(self, dsn: str) -> None:
        self.pool = AsyncConnectionPool(dsn, min_size=1, max_size=16, open=False, kwargs={"row_factory": dict_row})
        self._claims: dict[int, UUID] = {}

    async def open(self) -> None:
        await self.pool.open()
        await self.pool.wait()

    async def close(self) -> None:
        await self.pool.close()

    async def health(self) -> bool:
        try:
            async with self.pool.connection() as connection:
                return bool(await connection.execute("SELECT 1"))
        except Exception:
            return False

    async def begin_processing(self, request: ExtractRequest) -> ProcessingClaim:
        async with self.pool.connection() as connection, connection.transaction():
            inserted = await (
                await connection.execute(
                    """
                    INSERT INTO ee_processing_runs
                        (delivery_id, raw_message_id, status, started_at)
                    VALUES (%s, %s, 'processing', now())
                    ON CONFLICT (delivery_id) DO NOTHING
                    RETURNING processing_run_id, claim_token
                    """,
                    (request.delivery_id, request.raw_message_id),
                )
            ).fetchone()
            if inserted:
                run_id = int(inserted["processing_run_id"])
                self._claims[run_id] = inserted["claim_token"]
                return ProcessingClaim(run_id, "new")
            row = await (
                await connection.execute(
                    """
                    SELECT processing_run_id, status, result, started_at, claim_token
                    FROM ee_processing_runs
                    WHERE delivery_id = %s
                    """,
                    (request.delivery_id,),
                )
            ).fetchone()
            if row is None:
                raise RuntimeError("processing run disappeared after delivery-id conflict")
            if row["status"] == "processing":
                recovered = await (
                    await connection.execute(
                        """
                        UPDATE ee_processing_runs
                        SET started_at = now(), completed_at = NULL, error = NULL,
                            claim_token = gen_random_uuid()
                        WHERE processing_run_id = %s
                          AND status = 'processing'
                          AND claim_token = %s
                          AND started_at < now() - interval '15 minutes'
                        RETURNING processing_run_id, claim_token
                        """,
                        (row["processing_run_id"], row["claim_token"]),
                    )
                ).fetchone()
                if recovered:
                    run_id = int(row["processing_run_id"])
                    self._claims[run_id] = recovered["claim_token"]
                    return ProcessingClaim(run_id, "recovered")
            return ProcessingClaim(int(row["processing_run_id"]), str(row["status"]), row["result"])

    async def list_extractors(self) -> list[ExtractorDefinition]:
        async with self.pool.connection() as connection:
            rows = await (
                await connection.execute(
                    """
                    SELECT extractor_id, name, code, timeout_ms
                    FROM ee_extractors
                    WHERE enabled
                    ORDER BY execution_order, extractor_id
                    """
                )
            ).fetchall()
        return [ExtractorDefinition.model_validate(row) for row in rows]

    async def completed_extractor_steps(self, run_id: int) -> dict[int, int]:
        async with self.pool.connection() as connection:
            rows = await (
                await connection.execute(
                    """
                    SELECT extractor_id, writes_count
                    FROM ee_extractor_runs
                    WHERE processing_run_id = %s AND status = 'completed'
                    """,
                    (run_id,),
                )
            ).fetchall()
        return {int(row["extractor_id"]): int(row["writes_count"]) for row in rows}

    async def list_entity_definitions(self) -> list[EntityDefinition]:
        async with self.pool.connection() as connection:
            rows = await (
                await connection.execute(
                    """
                    SELECT entity_definition_id, entity_name, table_name, fields
                    FROM ee_entity_definitions
                    WHERE enabled
                    ORDER BY entity_definition_id
                    """
                )
            ).fetchall()
        return [self._definition(row) for row in rows]

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
        async with self.pool.connection() as connection, connection.transaction():
            await self._assert_claim(connection, run_id)
            definitions = await self._definitions_by_alias(connection)
            audit_row = await (
                await connection.execute(
                    """
                    INSERT INTO ee_extractor_runs
                        (processing_run_id, extractor_id, status, writes_count, duration_ms, stdout, stderr)
                    VALUES (%s, %s, 'completed', %s, %s, %s, %s)
                    ON CONFLICT (processing_run_id, extractor_id) DO UPDATE
                    SET status = EXCLUDED.status,
                        writes_count = EXCLUDED.writes_count,
                        duration_ms = EXCLUDED.duration_ms,
                        stdout = EXCLUDED.stdout,
                        stderr = EXCLUDED.stderr,
                        error = NULL
                    RETURNING extractor_run_id
                    """,
                    (run_id, extractor.extractor_id, len(writes), duration_ms, stdout, stderr),
                )
            ).fetchone()
            extractor_run_id = int(audit_row["extractor_run_id"])
            return await self._commit_writes(
                connection, run_id, extractor_run_id, raw_message_id, writes, definitions
            )

    async def record_extractor_failure(
        self, run_id: int, extractor: ExtractorDefinition, duration_ms: int, error: str, stdout: str, stderr: str
    ) -> None:
        async with self.pool.connection() as connection, connection.transaction():
            await self._assert_claim(connection, run_id)
            await connection.execute(
                """
                INSERT INTO ee_extractor_runs
                    (processing_run_id, extractor_id, status, writes_count, duration_ms, stdout, stderr, error)
                VALUES (%s, %s, 'failed', 0, %s, %s, %s, %s)
                ON CONFLICT (processing_run_id, extractor_id) DO UPDATE
                SET status = EXCLUDED.status,
                    writes_count = 0,
                    duration_ms = EXCLUDED.duration_ms,
                    stdout = EXCLUDED.stdout,
                    stderr = EXCLUDED.stderr,
                    error = EXCLUDED.error
                """,
                (run_id, extractor.extractor_id, duration_ms, stdout, stderr, error),
            )

    async def get_llm_settings(self, fallback_enabled: bool, fallback_model: str) -> LlmSettings:
        async with self.pool.connection() as connection:
            rows = await (
                await connection.execute("SELECT key, value FROM ee_get_llm_settings()")
            ).fetchall()
        values = {str(row["key"]): row["value"] for row in rows}
        return build_llm_settings(values, fallback_enabled, fallback_model)

    async def begin_llm_audit(self, run_id: int, request: ExtractRequest, audit: LlmAudit) -> int:
        async with self.pool.connection() as connection, connection.transaction():
            await self._assert_claim(connection, run_id)
            return await self._insert_llm_audit(connection, request, audit, 0)

    async def commit_llm_result(
        self,
        run_id: int,
        request: ExtractRequest,
        writes: list[CapturedWrite],
        audit: LlmAudit,
        audit_id: int,
    ) -> list[tuple[str, str]]:
        async with self.pool.connection() as connection, connection.transaction():
            await self._assert_claim(connection, run_id)
            definitions = await self._definitions_by_alias(connection)
            committed = await self._commit_writes(connection, run_id, None, request.raw_message_id, writes, definitions)
            await self._update_llm_audit(connection, audit_id, audit, len(committed))
            updated = await connection.execute(
                """
                UPDATE ee_processing_runs
                SET status = 'completed', result = %s, error = NULL, completed_at = now()
                WHERE processing_run_id = %s AND claim_token = %s AND status = 'processing'
                """,
                (1 if committed else 0, run_id, self._claim_token(run_id)),
            )
            if updated.rowcount != 1:
                raise RuntimeError(f"processing run {run_id} claim is no longer owned")
            self._claims.pop(run_id, None)
            return committed

    async def audit_llm_failure(self, audit_id: int, audit: LlmAudit) -> None:
        async with self.pool.connection() as connection:
            await self._update_llm_audit(connection, audit_id, audit, 0)

    async def complete_processing(self, run_id: int, result: int) -> None:
        async with self.pool.connection() as connection:
            updated = await connection.execute(
                """
                UPDATE ee_processing_runs
                SET status = 'completed', result = %s, error = NULL, completed_at = now()
                WHERE processing_run_id = %s AND claim_token = %s AND status = 'processing'
                """,
                (result, run_id, self._claim_token(run_id)),
            )
            if updated.rowcount != 1:
                raise RuntimeError(f"processing run {run_id} claim is no longer owned")
            self._claims.pop(run_id, None)

    async def fail_processing(self, run_id: int, error: str) -> None:
        async with self.pool.connection() as connection:
            updated = await connection.execute(
                """
                UPDATE ee_processing_runs
                SET status = 'failed', result = NULL, error = %s, completed_at = now()
                WHERE processing_run_id = %s AND claim_token = %s AND status = 'processing'
                """,
                (error, run_id, self._claim_token(run_id)),
            )
            if updated.rowcount != 1:
                raise RuntimeError(f"processing run {run_id} claim is no longer owned")
            self._claims.pop(run_id, None)

    def _claim_token(self, run_id: int) -> UUID:
        token = self._claims.get(run_id)
        if token is None:
            raise RuntimeError(f"processing run {run_id} has no active claim token")
        return token

    async def _assert_claim(self, connection: AsyncConnection[Any], run_id: int) -> None:
        row = await (
            await connection.execute(
                """
                UPDATE ee_processing_runs
                SET started_at = now()
                WHERE processing_run_id = %s AND claim_token = %s AND status = 'processing'
                RETURNING 1
                """,
                (run_id, self._claim_token(run_id)),
            )
        ).fetchone()
        if row is None:
            raise RuntimeError(f"processing run {run_id} claim is no longer owned")

    async def _definitions_by_alias(self, connection: AsyncConnection[Any]) -> dict[str, EntityDefinition]:
        rows = await (
            await connection.execute(
                """
                SELECT entity_definition_id, entity_name, table_name, fields
                FROM ee_entity_definitions
                WHERE enabled
                """
            )
        ).fetchall()
        definitions = [self._definition(row) for row in rows]
        return index_entity_definitions(definitions)

    @staticmethod
    def _definition(row: dict[str, Any]) -> EntityDefinition:
        raw_fields = row["fields"]
        if isinstance(raw_fields, str):
            raw_fields = json.loads(raw_fields)
        if isinstance(raw_fields, dict):
            raw_fields = raw_fields.get("fields", [{"name": key, "type": value} for key, value in raw_fields.items()])
        return EntityDefinition(
            entity_definition_id=row["entity_definition_id"],
            entity_name=row["entity_name"],
            table_name=row["table_name"],
            fields=[EntityField.model_validate(field) for field in (raw_fields or [])],
        )

    async def _commit_writes(
        self,
        connection: AsyncConnection[Any],
        run_id: int,
        extractor_run_id: int | None,
        raw_message_id: int,
        writes: list[CapturedWrite],
        definitions: dict[str, EntityDefinition],
    ) -> list[tuple[str, str]]:
        committed: list[tuple[str, str]] = []
        for captured in writes:
            definition = resolve_entity_definition(definitions, captured.table)
            columns, values, expressions = validate_write(definition, captured.values)
            id_column = f"{definition.entity_name}Id"
            query = sql.SQL("INSERT INTO {table} ({columns}) VALUES ({values}) RETURNING {id_column}").format(
                table=sql.Identifier(definition.table_name),
                columns=sql.SQL(", ").join([sql.Identifier("raw_message_id"), *map(sql.Identifier, columns)]),
                values=sql.SQL(", ").join([sql.Placeholder(), *expressions]),
                id_column=sql.Identifier(id_column),
            )
            row = await (await connection.execute(query, [raw_message_id, *values])).fetchone()
            entity_id = str(row[id_column])
            await connection.execute(
                """
                INSERT INTO ee_entity_writes
                    (processing_run_id, extractor_run_id, entity_definition_id, table_name, entity_id, created_at)
                VALUES (%s, %s, %s, %s, %s, now())
                """,
                (
                    run_id,
                    extractor_run_id,
                    definition.entity_definition_id,
                    definition.table_name,
                    entity_id,
                ),
            )
            committed.append((definition.table_name, entity_id))
        return committed

    async def _insert_llm_audit(
        self,
        connection: AsyncConnection[Any],
        request: ExtractRequest,
        audit: LlmAudit,
        facts_count: int,
    ) -> int:
        row = await (await connection.execute(
            """
            INSERT INTO llm_requests
                (raw_message_id, source_id, occurred_at, worker, model, prompt_version, outcome,
                 status_code, duration_ms, input_tokens, cache_creation_input_tokens,
                 cache_read_input_tokens, output_tokens, estimated_cost_usd, facts_count,
                 request_text, system_prompt, response_text, request_payload, response_payload, error)
            VALUES
                (%s, %s, now(), 'entity-extractor', %s, 'entity-extractor-1', %s,
                 %s, %s, %s, %s, %s, %s, %s, %s,
                 %s, %s, %s, %s::jsonb, %s::jsonb, %s)
            RETURNING llm_request_id
            """,
            (
                request.raw_message_id,
                request.source_id,
                audit.model,
                audit.outcome,
                audit.status_code,
                audit.duration_ms,
                audit.input_tokens,
                audit.cache_creation_input_tokens,
                audit.cache_read_input_tokens,
                audit.output_tokens,
                audit.estimated_cost_usd,
                facts_count,
                audit.request_text,
                audit.system_prompt,
                audit.response_text,
                json.dumps(audit.request_payload, ensure_ascii=False),
                json.dumps(audit.response_payload, ensure_ascii=False) if audit.response_payload is not None else None,
                audit.error,
            ),
        )).fetchone()
        if row is None:
            raise RuntimeError("LLM audit insert returned no id")
        return int(row["llm_request_id"])

    async def _update_llm_audit(
        self,
        connection: AsyncConnection[Any],
        audit_id: int,
        audit: LlmAudit,
        facts_count: int,
    ) -> None:
        row = await (await connection.execute(
            """
            SELECT ee_finalize_llm_request(
                %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s) AS updated
            """,
            (
                audit_id,
                audit.outcome,
                audit.status_code,
                audit.duration_ms,
                audit.input_tokens,
                audit.cache_creation_input_tokens,
                audit.cache_read_input_tokens,
                audit.output_tokens,
                audit.estimated_cost_usd,
                facts_count,
                audit.response_text,
                json.dumps(audit.response_payload, ensure_ascii=False) if audit.response_payload is not None else None,
                audit.error,
            ),
        )).fetchone()
        if row is None or not row["updated"]:
            raise RuntimeError(f"LLM audit {audit_id} was not found")


def validate_write(definition: EntityDefinition, values: dict[str, Any]) -> tuple[list[str], list[Any], list[sql.Composable]]:
    if not definition.table_name.startswith("ee_") or not IDENTIFIER.fullmatch(definition.table_name):
        raise ValueError(f"invalid registered table identifier {definition.table_name!r}")
    fields = {field.name: field for field in definition.fields}
    automatic = {"rawMessageId", f"{definition.entity_name}Id"}
    supplied_automatic = sorted(set(values) & automatic)
    if supplied_automatic:
        raise ValueError(f"automatic fields cannot be written: {', '.join(supplied_automatic)}")
    unknown = sorted(set(values) - set(fields))
    if unknown:
        raise ValueError(f"unknown fields for {definition.table_name}: {', '.join(unknown)}")
    columns: list[str] = []
    parameters: list[Any] = []
    expressions: list[sql.Composable] = []
    for name, value in values.items():
        field = fields[name]
        column = field.column_name or _snake(field.name)
        if not IDENTIFIER.fullmatch(column):
            raise ValueError(f"invalid registered column identifier {column!r}")
        converted, expression = _value(field, value)
        columns.append(column)
        parameters.append(converted)
        expressions.append(expression)
    for field in definition.fields:
        if field.required and field.name not in automatic and field.name not in values:
            raise ValueError(f"missing required field {field.name!r} for {definition.table_name}")
    return columns, parameters, expressions


def index_entity_definitions(definitions: list[EntityDefinition]) -> dict[str, EntityDefinition]:
    aliases: dict[str, EntityDefinition] = {}
    for definition in definitions:
        if not definition.table_name.startswith("ee_") or not IDENTIFIER.fullmatch(definition.table_name):
            raise ValueError(f"invalid registered table identifier {definition.table_name!r}")
        candidates = {
            definition.table_name,
            definition.table_name.removeprefix("ee_"),
            definition.entity_name,
            _snake(definition.entity_name),
        }
        for alias in candidates:
            existing = aliases.get(alias)
            if existing is not None and existing.entity_definition_id != definition.entity_definition_id:
                raise ValueError(f"entity alias {alias!r} is ambiguous")
            aliases[alias] = definition
    return aliases


def resolve_entity_definition(
    definitions: dict[str, EntityDefinition], alias: str
) -> EntityDefinition:
    definition = definitions.get(alias)
    if definition is None:
        raise ValueError(f"entity/table alias {alias!r} is not registered and enabled")
    return definition


def _value(field: EntityField, value: Any) -> tuple[Any, sql.Composable]:
    kind = field.type.lower()
    if value is None:
        if field.required:
            raise ValueError(f"field {field.name!r} cannot be null")
        return None, sql.Placeholder()
    if kind == "text":
        if not isinstance(value, str):
            raise ValueError(f"field {field.name!r} must be text")
        return value, sql.Placeholder()
    if kind == "integer":
        if not isinstance(value, int) or isinstance(value, bool):
            raise ValueError(f"field {field.name!r} must be integer")
        return value, sql.Placeholder()
    if kind == "decimal":
        if not isinstance(value, (int, float, Decimal)) or isinstance(value, bool):
            raise ValueError(f"field {field.name!r} must be decimal")
        return Decimal(str(value)), sql.Placeholder()
    if kind == "boolean":
        if not isinstance(value, bool):
            raise ValueError(f"field {field.name!r} must be boolean")
        return value, sql.Placeholder()
    if kind == "datetime":
        if isinstance(value, str):
            try:
                value = datetime.fromisoformat(value.replace("Z", "+00:00"))
            except ValueError as exc:
                raise ValueError(f"field {field.name!r} must be an ISO datetime") from exc
        if not isinstance(value, datetime):
            raise ValueError(f"field {field.name!r} must be datetime")
        return value, sql.Placeholder()
    if kind == "json":
        try:
            return json.dumps(value, ensure_ascii=False), sql.SQL("{}::jsonb").format(sql.Placeholder())
        except (TypeError, ValueError) as exc:
            raise ValueError(f"field {field.name!r} must be JSON serializable") from exc
    if kind in {"point", "line", "polygon"}:
        geojson = _geojson(kind, value, field.name)
        return json.dumps(geojson), sql.SQL("ST_SetSRID(ST_GeomFromGeoJSON({}), 4326)").format(sql.Placeholder())
    raise ValueError(f"field {field.name!r} has unsupported type {field.type!r}")


def _geojson(kind: str, value: Any, field_name: str) -> dict[str, Any]:
    expected = {"point": "Point", "line": "LineString", "polygon": "Polygon"}[kind]
    if not isinstance(value, dict) or value.get("type") != expected or "coordinates" not in value:
        raise ValueError(f"field {field_name!r} must be GeoJSON {expected}")
    return value


def _snake(value: str) -> str:
    value = re.sub(r"(?<!^)(?=[A-Z])", "_", value).lower()
    value = re.sub(r"[^a-z0-9_]+", "_", value).strip("_")
    if not IDENTIFIER.fullmatch(value):
        raise ValueError(f"invalid identifier {value!r}")
    return value


def _int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _duration_seconds(value: Any, default: float) -> float:
    if value is None:
        return default
    if isinstance(value, (int, float)):
        return float(value)
    text = str(value).strip()
    try:
        days = 0
        if "." in text:
            day_text, text = text.split(".", 1)
            days = int(day_text)
        hours, minutes, seconds = text.split(":", 2)
        return days * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except (TypeError, ValueError):
        return default


def build_llm_settings(
    values: dict[str, Any], fallback_enabled: bool, fallback_model: str
) -> LlmSettings:
    return LlmSettings(
        enabled=(
            str(values["Llm:Enabled"]).lower() == "true"
            if "Llm:Enabled" in values
            else fallback_enabled
        ),
        model=str(values["Llm:Model"]) if "Llm:Model" in values else fallback_model,
        api_key=values.get("Llm:ApiKey"),
        timeout_seconds=_int(values.get("Llm:TimeoutSeconds"), 20),
        max_output_tokens=_int(values.get("Llm:MaxOutputTokens"), 1024),
        max_calls_per_minute=_int(values.get("Llm:MaxCallsPerMinute"), 20),
        max_message_age_hours=_float(values.get("Llm:MaxMessageAgeHours"), 72),
        failure_pause_seconds=_duration_seconds(values.get("Llm:FailurePause"), 900),
        max_attempts=_int(values.get("Llm:MaxAttempts"), 3),
        input_price=_float(values.get("Llm:InputUsdPerMillionTokens"), 5),
        output_price=_float(values.get("Llm:OutputUsdPerMillionTokens"), 25),
        cache_write_price=_float(values.get("Llm:CacheWriteUsdPerMillionTokens"), 6.25),
        cache_read_price=_float(values.get("Llm:CacheReadUsdPerMillionTokens"), 0.5),
    )
