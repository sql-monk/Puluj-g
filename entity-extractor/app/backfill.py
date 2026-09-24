"""Run the entity extractors over the message history.

The live path (``POST /extract``) audits every message and every extractor call in its own transactions — right for a
message a minute, prohibitive for the seven million collected since 2022. The backfill walks ``raw_messages`` by id in
batches instead: one sandbox child runs every extractor over a whole batch, place references are resolved once per
distinct place, and each batch is one transaction that replaces the entity rows of its messages and moves the
checkpoint (``app_settings`` ``EntityExtractor:Backfill:NextRawMessageId``). Stop it at any moment and start it again:
it continues after the last committed batch, and re-running a range writes the same rows instead of duplicates.

It reads ``raw_messages`` without locks and writes only the ``ee_*`` tables, so the collectors never wait on it. It
connects as the database owner (it deletes entity rows and writes the checkpoint), e.g. from the host::

    DATABASE_URL=postgresql://puluj:<password>@localhost:5442/puluj python -m app.backfill

Nothing is audited in ee_processing_runs / ee_extractor_runs and the LLM fallback never runs here.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
import time
from collections import OrderedDict, defaultdict
from collections.abc import Iterator
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

import psycopg
from psycopg import sql
from psycopg.rows import dict_row

from .config import Settings
from .models import CapturedWrite, EntityDefinition
from .places import is_place_reference, place_reference, requires_geometry
from .repository import (
    PostgresRepository,
    _snake,
    index_entity_definitions,
    resolve_entity_definition,
    validate_write,
)
from .runtime import execute_batch_blocking
from .service import with_event_time

CHECKPOINT_KEY = "EntityExtractor:Backfill:NextRawMessageId"
PLACE_CACHE_SIZE = 200_000
log = logging.getLogger("puluj.backfill")


@dataclass(slots=True)
class Batch:
    first_id: int
    last_id: int
    messages: list[dict[str, Any]]


@dataclass(slots=True)
class Totals:
    messages: int = 0
    entities: int = 0
    dropped: int = 0
    failed_calls: int = 0
    skipped_messages: int = 0


class PlaceCache:
    """(geometry kind, reference JSON) -> EWKB hex or None: a few thousand places repeat across millions of rows."""

    def __init__(self, connection: psycopg.Connection[Any]) -> None:
        self.connection = connection
        self.values: OrderedDict[tuple[str, str], str | None] = OrderedDict()

    def resolve(self, keys: set[tuple[str, str]]) -> None:
        missing = [key for key in keys if key not in self.values]
        if missing:
            rows = self.connection.execute(
                """
                SELECT ee_place_geometry(u.ref::jsonb, u.kind)::text AS geometry
                FROM unnest(%s::text[], %s::text[]) WITH ORDINALITY AS u(ref, kind, position)
                ORDER BY u.position
                """,
                ([ref for _, ref in missing], [kind for kind, _ in missing]),
            ).fetchall()
            for key, row in zip(missing, rows, strict=True):
                self.values[key] = row["geometry"]
            while len(self.values) > PLACE_CACHE_SIZE:
                self.values.popitem(last=False)

    def get(self, key: tuple[str, str]) -> str | None:
        return self.values.get(key)


def load_extractors(connection: psycopg.Connection[Any], include_disabled: bool) -> list[tuple[int, str, str]]:
    rows = connection.execute(
        """
        SELECT extractor_id, name, code FROM ee_extractors
        WHERE enabled OR %s
        ORDER BY execution_order, extractor_id
        """,
        (include_disabled,),
    ).fetchall()
    return [(int(row["extractor_id"]), row["name"], row["code"]) for row in rows]


def load_definitions(connection: psycopg.Connection[Any]) -> list[EntityDefinition]:
    rows = connection.execute(
        """
        SELECT entity_definition_id, entity_name, table_name, fields
        FROM ee_entity_definitions WHERE enabled ORDER BY entity_definition_id
        """
    ).fetchall()
    return [PostgresRepository._definition(row) for row in rows]


def default_last_id(connection: psycopg.Connection[Any]) -> int:
    """The newest message the live path has certainly seen: later ones are its to handle, not the backfill's."""
    row = connection.execute(
        "SELECT max(raw_message_id) AS id FROM raw_messages WHERE received_at < now() - interval '10 minutes'"
    ).fetchone()
    return int(row["id"] or 0)


def read_checkpoint(connection: psycopg.Connection[Any]) -> int | None:
    row = connection.execute("SELECT value FROM app_settings WHERE key = %s", (CHECKPOINT_KEY,)).fetchone()
    return int(row["value"]) if row and row["value"] else None


def batches(connection: psycopg.Connection[Any], first_id: int, last_id: int, size: int) -> Iterator[Batch]:
    cursor = first_id
    while cursor <= last_id:
        rows = connection.execute(
            """
            SELECT r.raw_message_id, r.source_id, s.code AS source_code, r.published_at, r.received_at,
                   r.raw_text, r.raw_payload, r.url
            FROM raw_messages r
            JOIN sources s ON s.source_id = r.source_id
            WHERE r.raw_message_id >= %s AND r.raw_message_id <= %s
            ORDER BY r.raw_message_id
            LIMIT %s
            """,
            (cursor, last_id, size),
        ).fetchall()
        if not rows:
            return
        yield Batch(cursor, int(rows[-1]["raw_message_id"]), [_message(row) for row in rows])
        cursor = int(rows[-1]["raw_message_id"]) + 1


def _message(row: dict[str, Any]) -> dict[str, Any]:
    """The same shape ``ExtractRequest.extractor_message`` gives the live extractors."""
    return {
        "rawMessageId": int(row["raw_message_id"]),
        "sourceId": int(row["source_id"]),
        "sourceCode": row["source_code"],
        "publishedAt": _iso(row["published_at"]),
        "receivedAt": _iso(row["received_at"]),
        "text": row["raw_text"],
        "rawPayload": row["raw_payload"],
        "url": row["url"],
    }


def _iso(value: datetime | None) -> str | None:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z") if value else None


def run_extractors(
    extractors: list[tuple[int, str, str]], batch: Batch, settings: Settings, timeout_ms: int, totals: Totals
) -> dict[int, list[CapturedWrite]]:
    """rawMessageId -> writes of every extractor that succeeded on it. A batch whose child dies or times out is
    split until the message responsible is alone; that message is skipped and gets no entities."""
    result = execute_batch_blocking(
        [(extractor_id, code) for extractor_id, _, code in extractors],
        batch.messages,
        timeout_ms,
        settings.max_output_bytes,
        max(settings.max_memory_mb, 512),
        sparse=True,
    )
    if result.error is not None:
        if len(batch.messages) == 1:
            log.warning("message %s skipped: %s", batch.first_id, result.error)
            totals.skipped_messages += 1
            return {}
        half = len(batch.messages) // 2
        writes: dict[int, list[CapturedWrite]] = {}
        for part in (batch.messages[:half], batch.messages[half:]):
            piece = Batch(part[0]["rawMessageId"], part[-1]["rawMessageId"], part)
            writes.update(run_extractors(extractors, piece, settings, timeout_ms, totals))
        return writes
    names = {extractor_id: name for extractor_id, name, _ in extractors}
    writes = defaultdict(list)
    for (index, extractor_id), call in sorted(result.results.items()):
        message_id = batch.messages[index]["rawMessageId"]
        if call.error:
            totals.failed_calls += 1
            log.debug("message %s, extractor %s: %s", message_id, names.get(extractor_id), call.error)
            continue
        writes[message_id].extend(call.writes)
    return writes


def commit_batch(
    connection: psycopg.Connection[Any],
    batch: Batch,
    writes: dict[int, list[CapturedWrite]],
    definitions: list[EntityDefinition],
    places: PlaceCache,
    totals: Totals,
    checkpoint: bool,
) -> None:
    by_alias = index_entity_definitions(definitions)
    published = {message["rawMessageId"]: message["publishedAt"] or message["receivedAt"] for message in batch.messages}
    rows: dict[tuple[str, tuple[str, ...]], list[list[Any]]] = defaultdict(list)
    templates: dict[tuple[str, tuple[str, ...]], sql.Composable] = {}
    pending: list[tuple[EntityDefinition, int, list[str], list[Any], list[sql.Composable], dict[int, tuple[str, str]], set[int]]] = []
    for message_id, captured_writes in writes.items():
        fallback = datetime.fromisoformat(published[message_id].replace("Z", "+00:00"))
        for captured in with_event_time(captured_writes, definitions, fallback):
            try:
                definition = resolve_entity_definition(by_alias, captured.table)
                columns, values, expressions = validate_write(definition, captured.values)
            except ValueError as exc:
                totals.failed_calls += 1
                log.debug("message %s: invalid write: %s", message_id, exc)
                continue
            place_columns: dict[int, tuple[str, str]] = {}
            required: set[int] = set()
            for field in definition.fields:
                kind = field.type.lower()
                value = captured.values.get(field.name)
                if field.name not in captured.values or not is_place_reference(kind, value):
                    continue
                index = columns.index(field.column_name or _snake(field.name))
                place_columns[index] = (kind, json.dumps(place_reference(kind, value, field.name), ensure_ascii=False))
                if requires_geometry(kind, value):
                    required.add(index)
            pending.append((definition, message_id, columns, values, expressions, place_columns, required))

    places.resolve({key for *_, place_columns, _ in pending for key in place_columns.values()})
    for definition, message_id, columns, values, expressions, place_columns, required in pending:
        values = list(values)
        expressions = list(expressions)
        dropped = False
        for index, key in place_columns.items():
            geometry = places.get(key)
            if geometry is None and index in required:
                dropped = True
                break
            values[index] = geometry
            expressions[index] = sql.SQL("{}::geometry").format(sql.Placeholder())
        if dropped:
            totals.dropped += 1
            continue
        shape = (definition.table_name, tuple(columns))
        if shape not in templates:
            templates[shape] = sql.SQL("INSERT INTO {table} ({columns}) VALUES ({values})").format(
                table=sql.Identifier(definition.table_name),
                columns=sql.SQL(", ").join([sql.Identifier("raw_message_id"), *map(sql.Identifier, columns)]),
                values=sql.SQL(", ").join([sql.Placeholder(), *expressions]),
            )
        rows[shape].append([message_id, *values])

    message_ids = [message["rawMessageId"] for message in batch.messages]
    with connection.transaction():
        for definition in definitions:
            id_column = sql.Identifier(f"{definition.entity_name}Id")
            deleted = connection.execute(
                sql.SQL("DELETE FROM {table} WHERE raw_message_id = ANY(%s) RETURNING {id}").format(
                    table=sql.Identifier(definition.table_name), id=id_column
                ),
                (message_ids,),
            ).fetchall()
            if deleted:
                connection.execute(
                    "DELETE FROM ee_entity_writes WHERE table_name = %s AND entity_id = ANY(%s)",
                    (definition.table_name, [row[f"{definition.entity_name}Id"] for row in deleted]),
                )
        for shape, parameters in rows.items():
            with connection.cursor() as cursor:
                cursor.executemany(templates[shape], parameters)
            totals.entities += len(parameters)
        if checkpoint:
            connection.execute(
                """
                INSERT INTO app_settings (key, value, is_secret, updated_at) VALUES (%s, %s, false, now())
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """,
                (CHECKPOINT_KEY, str(batch.last_id + 1)),
            )
    totals.messages += len(batch.messages)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m app.backfill", description=__doc__.split("\n\n")[0])
    parser.add_argument("--from-id", type=int, help="first raw_message_id (default: the checkpoint, else the oldest)")
    parser.add_argument("--to-id", type=int, help="last raw_message_id (default: the newest the live path has seen)")
    parser.add_argument("--batch", type=int, default=500, help="messages per sandbox child and transaction")
    parser.add_argument("--workers", type=int, default=4, help="sandbox children running at once")
    parser.add_argument("--timeout", type=int, default=300_000, help="ms one batch child may run")
    parser.add_argument("--include-disabled", action="store_true", help="run disabled extractors too")
    parser.add_argument("--no-checkpoint", action="store_true", help="neither read nor move the checkpoint")
    parser.add_argument("--dsn", help="PostgreSQL URI (default: ConnectionStrings__Puluj / DATABASE_URL)")
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stdout)

    settings = Settings()
    dsn = args.dsn or settings.postgres_dsn
    # autocommit: every read is its own short snapshot, every batch its own explicit transaction
    with (
        psycopg.connect(dsn, row_factory=dict_row, autocommit=True) as reader,
        psycopg.connect(dsn, row_factory=dict_row, autocommit=True) as writer,
    ):
        extractors = load_extractors(reader, args.include_disabled)
        definitions = load_definitions(reader)
        if not extractors:
            log.error("no %sextractors in ee_extractors", "" if args.include_disabled else "enabled ")
            return 1
        checkpoint = None if args.no_checkpoint else read_checkpoint(reader)
        first_id = args.from_id if args.from_id is not None else checkpoint
        if first_id is None:
            first_id = int(reader.execute("SELECT coalesce(min(raw_message_id), 1) AS id FROM raw_messages").fetchone()["id"])
        last_id = args.to_id if args.to_id is not None else default_last_id(reader)
        log.info("extractors: %s", ", ".join(name for _, name, _ in extractors))
        log.info("raw_message_id %s..%s, batches of %s, %s workers", first_id, last_id, args.batch, args.workers)

        totals = Totals()
        places = PlaceCache(writer)
        started = time.monotonic()
        in_flight: list[tuple[Batch, Future[dict[int, list[CapturedWrite]]]]] = []
        with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
            source = batches(reader, first_id, last_id, args.batch)
            exhausted = False
            while True:
                while not exhausted and len(in_flight) < max(1, args.workers) * 2:
                    batch = next(source, None)
                    if batch is None:
                        exhausted = True
                        break
                    in_flight.append((batch, pool.submit(run_extractors, extractors, batch, settings, args.timeout, totals)))
                if not in_flight:
                    break
                batch, future = in_flight.pop(0)  # commit in id order: the checkpoint never skips a batch
                commit_batch(writer, batch, future.result(), definitions, places, totals, not args.no_checkpoint)
                elapsed = max(time.monotonic() - started, 0.001)
                rate = totals.messages / elapsed
                remaining = max(last_id - batch.last_id, 0)
                log.info(
                    "up to %s: %s messages, %s entities (%s without a known place), %s failed calls, %.0f msg/s%s",
                    batch.last_id, totals.messages, totals.entities, totals.dropped, totals.failed_calls, rate,
                    f", ~{remaining / max(rate, 1) / 60:.0f} min of ids left" if remaining else "",
                )
        log.info("done: %s messages, %s entities, %s skipped messages", totals.messages, totals.entities, totals.skipped_messages)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
