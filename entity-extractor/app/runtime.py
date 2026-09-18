from __future__ import annotations

import ast
import builtins
import contextlib
import io
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import traceback
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .models import CapturedWrite, Diagnostic, ValidateResponse

ALLOWED_IMPORT_ROOTS = frozenset(
    {
        "calendar",
        "collections",
        "dataclasses",
        "datetime",
        "decimal",
        "enum",
        "functools",
        "itertools",
        "json",
        "math",
        "operator",
        "re",
        "statistics",
        "string",
        "typing",
        "unicodedata",
        "zoneinfo",
    }
)
DANGEROUS_CALLS = frozenset(
    {"__import__", "breakpoint", "compile", "delattr", "dir", "eval", "exec", "getattr", "globals", "help", "input", "locals", "open", "setattr", "vars"}
)
DANGEROUS_ATTRIBUTES = frozenset(
    {
        "__base__",
        "__bases__",
        "__builtins__",
        "__class__",
        "__closure__",
        "__code__",
        "__dict__",
        "__getattribute__",
        "__globals__",
        "__loader__",
        "__mro__",
        "__spec__",
        "__subclasses__",
        "__traceback__",
        "builtins",
        "f_back",
        "f_builtins",
        "f_code",
        "f_globals",
        "f_locals",
        "gi_frame",
        "importlib",
        "modules",
        "os",
        "socket",
        "subprocess",
        "sys",
        "tb_frame",
        "tb_next",
    }
)


@dataclass(slots=True)
class RuntimeResult:
    writes: list[CapturedWrite] = field(default_factory=list)
    stdout: str = ""
    stderr: str = ""
    duration_ms: int = 0
    error: str | None = None
    timed_out: bool = False


def validate_code(code: str) -> ValidateResponse:
    try:
        tree = ast.parse(code, mode="exec")
    except SyntaxError as exc:
        return ValidateResponse(
            valid=False,
            diagnostics=[Diagnostic(line=exc.lineno or 1, column=exc.offset or 1, message=exc.msg)],
        )
    policy_diagnostic = _policy_diagnostic(tree)
    if policy_diagnostic is not None:
        return ValidateResponse(valid=False, diagnostics=[policy_diagnostic])
    function = next(
        (node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == "extract"),
        None,
    )
    extractor_class = next((node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "Extractor"), None)
    method = (
        next(
            (
                node
                for node in extractor_class.body
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == "extract"
            ),
            None,
        )
        if extractor_class
        else None
    )
    if function is None and method is None:
        return ValidateResponse(
            valid=False,
            diagnostics=[Diagnostic(line=1, column=1, message="Define extract(message, write) or Extractor.extract(message, write)")],
        )
    entry_point = function or method
    expected = 2 if function is not None else 3
    assert entry_point is not None
    arguments = entry_point.args
    if arguments.vararg or arguments.kwarg or len(arguments.posonlyargs) + len(arguments.args) != expected:
        return ValidateResponse(
            valid=False,
            diagnostics=[
                Diagnostic(
                    line=entry_point.lineno,
                    column=entry_point.col_offset + 1,
                    message=(
                        "extract must accept exactly (message, write)"
                        if function is not None
                        else "Extractor.extract must accept exactly (self, message, write)"
                    ),
                )
            ],
        )
    return ValidateResponse(valid=True)


def _policy_diagnostic(tree: ast.AST) -> Diagnostic | None:
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            denied = next((item.name for item in node.names if item.name.split(".", 1)[0] not in ALLOWED_IMPORT_ROOTS), None)
            if denied:
                return Diagnostic(
                    line=node.lineno,
                    column=node.col_offset + 1,
                    message=f"import {denied!r} is not allowed in entity extractors",
                )
        elif isinstance(node, ast.ImportFrom):
            root = (node.module or "").split(".", 1)[0]
            unsafe_member = any(
                item.name == "*" or item.name.startswith("_") or item.name in DANGEROUS_ATTRIBUTES
                for item in node.names
            )
            if node.level or root not in ALLOWED_IMPORT_ROOTS or unsafe_member:
                return Diagnostic(
                    line=node.lineno,
                    column=node.col_offset + 1,
                    message=f"import from {node.module!r} is not allowed in entity extractors",
                )
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in DANGEROUS_CALLS:
            return Diagnostic(
                line=node.lineno,
                column=node.col_offset + 1,
                message=f"call to {node.func.id!r} is not allowed in entity extractors",
            )
        elif isinstance(node, ast.Attribute) and (node.attr.startswith("_") or node.attr in DANGEROUS_ATTRIBUTES):
            return Diagnostic(
                line=node.lineno,
                column=node.col_offset + 1,
                message=f"attribute {node.attr!r} is not allowed in entity extractors",
            )
    return None


def _apply_limits(max_memory_mb: int, timeout_ms: int) -> None:
    try:
        import resource

        memory = max_memory_mb * 1024 * 1024
        resource.setrlimit(resource.RLIMIT_AS, (memory, memory))
        cpu_seconds = max(1, (timeout_ms + 999) // 1000)
        resource.setrlimit(resource.RLIMIT_CPU, (cpu_seconds, cpu_seconds + 1))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        resource.setrlimit(resource.RLIMIT_FSIZE, (0, 0))
        if hasattr(resource, "RLIMIT_NPROC"):
            resource.setrlimit(resource.RLIMIT_NPROC, (0, 0))
        if hasattr(resource, "RLIMIT_NOFILE"):
            resource.setrlimit(resource.RLIMIT_NOFILE, (16, 16))
    except (ImportError, OSError, ValueError):
        # Windows has no resource module; the parent still enforces wall-clock timeout.
        pass


def run_user_code(
    code: str,
    message: dict[str, Any],
    max_output_bytes: int,
    max_memory_mb: int,
    timeout_ms: int,
) -> dict[str, Any]:
    started = time.monotonic()
    writes: list[dict[str, Any]] = []
    stdout = io.StringIO()
    stderr = io.StringIO()
    try:
        # User code starts only after inherited application credentials are removed.
        os.environ.clear()
        os.environ["PYTHONIOENCODING"] = "utf-8"
        _apply_limits(max_memory_mb, timeout_ms)
        validation = validate_code(code)
        if not validation.valid:
            diagnostic = validation.diagnostics[0]
            raise ValueError(f"line {diagnostic.line}:{diagnostic.column}: {diagnostic.message}")

        def write(table_name: str, values: dict[str, Any]) -> None:
            if not isinstance(table_name, str) or not table_name:
                raise TypeError("write table_name must be a non-empty string")
            if not isinstance(values, dict):
                raise TypeError("write values must be a dictionary")
            candidate = {"table": table_name, "values": values}
            json.dumps(candidate, ensure_ascii=False, default=_json_default)
            writes.append(candidate)
            if len(json.dumps(writes, ensure_ascii=False, default=_json_default).encode("utf-8")) > max_output_bytes:
                raise ValueError("captured writes exceed the configured output limit")

        namespace: dict[str, Any] = {
            "__name__": "puluj_dynamic_extractor",
            "__builtins__": _safe_builtins(),
        }
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            exec(compile(code, "<extractor>", "exec"), namespace, namespace)
            target = namespace.get("extract")
            if target is None:
                extractor_type = namespace.get("Extractor")
                if not isinstance(extractor_type, type):
                    raise TypeError("Define extract(message, write) or Extractor.extract(message, write)")
                target = extractor_type().extract
            result = target(message, write)
            if hasattr(result, "__await__"):
                import asyncio

                asyncio.run(result)
        payload = {
            "writes": writes,
            "stdout": stdout.getvalue()[-max_output_bytes:],
            "stderr": stderr.getvalue()[-max_output_bytes:],
            "duration_ms": int((time.monotonic() - started) * 1000),
            "error": None,
        }
    except BaseException as exc:  # child must report user-code failures, including SystemExit
        payload = {
            "writes": [],
            "stdout": stdout.getvalue()[-max_output_bytes:],
            "stderr": stderr.getvalue()[-max_output_bytes:],
            "duration_ms": int((time.monotonic() - started) * 1000),
            "error": "".join(traceback.format_exception_only(type(exc), exc)).strip(),
        }
    return payload


def _safe_builtins() -> dict[str, Any]:
    names = {
        "ArithmeticError",
        "AssertionError",
        "AttributeError",
        "BaseException",
        "Exception",
        "IndexError",
        "KeyError",
        "LookupError",
        "NameError",
        "NotImplementedError",
        "OverflowError",
        "RuntimeError",
        "StopIteration",
        "TypeError",
        "ValueError",
        "ZeroDivisionError",
        "__build_class__",
        "abs",
        "all",
        "any",
        "bool",
        "bytes",
        "callable",
        "chr",
        "classmethod",
        "dict",
        "divmod",
        "enumerate",
        "filter",
        "float",
        "format",
        "frozenset",
        "hash",
        "hex",
        "int",
        "isinstance",
        "issubclass",
        "iter",
        "len",
        "list",
        "map",
        "max",
        "min",
        "next",
        "object",
        "oct",
        "ord",
        "pow",
        "print",
        "property",
        "range",
        "repr",
        "reversed",
        "round",
        "set",
        "slice",
        "sorted",
        "staticmethod",
        "str",
        "sum",
        "super",
        "tuple",
        "type",
        "zip",
    }
    safe = {name: getattr(builtins, name) for name in names}
    safe["__import__"] = _restricted_import
    return safe


def _restricted_import(
    name: str,
    globals: dict[str, Any] | None = None,
    locals: dict[str, Any] | None = None,
    fromlist: tuple[str, ...] = (),
    level: int = 0,
) -> Any:
    del globals, locals
    root = name.split(".", 1)[0]
    if level or root not in ALLOWED_IMPORT_ROOTS:
        raise ImportError(f"import {name!r} is not allowed in entity extractors")
    return builtins.__import__(name, {}, {}, fromlist, 0)


def _json_default(value: Any) -> Any:
    if hasattr(value, "isoformat"):
        return value.isoformat()
    raise TypeError(f"{type(value).__name__} is not JSON serializable")


def _execute_blocking(
    code: str,
    message: dict[str, Any],
    timeout_ms: int,
    max_output_bytes: int,
    max_memory_mb: int,
) -> RuntimeResult:
    validation = validate_code(code)
    if not validation.valid:
        diagnostic = validation.diagnostics[0]
        return RuntimeResult(error=f"line {diagnostic.line}:{diagnostic.column}: {diagnostic.message}")
    started = time.monotonic()
    child_input = json.dumps(
        {
            "code": code,
            "message": message,
            "maxOutputBytes": max_output_bytes,
            "maxMemoryMb": max_memory_mb,
            "timeoutMs": timeout_ms,
        },
        ensure_ascii=False,
        default=_json_default,
    )
    popen_options: dict[str, Any] = {"start_new_session": True} if os.name == "posix" else {}
    if os.name == "nt":
        popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    with tempfile.TemporaryDirectory(prefix="puluj-ee-") as working_directory:
        process = subprocess.Popen(
            [sys.executable, "-m", "app.child_runner"],
            stdin=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env={
                "PYTHONIOENCODING": "utf-8",
                "PYTHONDONTWRITEBYTECODE": "1",
                "PYTHONPATH": str(Path(__file__).resolve().parent.parent),
            },
            cwd=working_directory,
            **popen_options,
        )
        try:
            process_stdout, process_stderr = process.communicate(child_input, timeout=timeout_ms / 1000)
        except subprocess.TimeoutExpired:
            _terminate_process_tree(process)
            return RuntimeResult(
                duration_ms=int((time.monotonic() - started) * 1000),
                error=f"extractor exceeded {timeout_ms} ms timeout",
                timed_out=True,
            )
    if process.returncode != 0:
        return RuntimeResult(
            duration_ms=int((time.monotonic() - started) * 1000),
            stderr=process_stderr[-max_output_bytes:],
            error=f"extractor process exited with code {process.returncode}",
        )
    try:
        payload = json.loads(process_stdout)
    except json.JSONDecodeError:
        return RuntimeResult(
            duration_ms=int((time.monotonic() - started) * 1000),
            stderr=process_stderr[-max_output_bytes:],
            error="extractor process returned an invalid result",
        )
    return RuntimeResult(
        writes=[CapturedWrite.model_validate(item) for item in payload["writes"]],
        stdout=payload["stdout"],
        stderr=payload["stderr"],
        duration_ms=payload["duration_ms"],
        error=payload["error"],
    )


def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    try:
        if os.name == "posix":
            os.killpg(os.getpgid(process.pid), signal.SIGKILL)
        elif os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        else:
            process.kill()
    except (OSError, subprocess.SubprocessError):
        if process.poll() is None:
            process.kill()
    finally:
        try:
            process.communicate(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()


async def execute_extractor(
    code: str,
    message: dict[str, Any],
    timeout_ms: int,
    max_output_bytes: int,
    max_memory_mb: int,
) -> RuntimeResult:
    import asyncio

    return await asyncio.to_thread(
        _execute_blocking,
        code,
        message,
        timeout_ms,
        max_output_bytes,
        max_memory_mb,
    )
