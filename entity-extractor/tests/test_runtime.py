import os
import subprocess
import sys
import time

import pytest

from app.runtime import _terminate_process_tree, execute_extractor, validate_code


def test_validate_reports_syntax_location() -> None:
    result = validate_code("def extract(:\n  pass")

    assert not result.valid
    assert result.diagnostics[0].line == 1
    assert result.diagnostics[0].column > 0


def test_validate_requires_supported_entry_point() -> None:
    result = validate_code("def other():\n    return None")

    assert not result.valid
    assert "extract(message, write)" in result.diagnostics[0].message


def test_validate_rejects_wrong_entry_point_signature() -> None:
    result = validate_code("def extract(message):\n    return None")

    assert not result.valid
    assert "exactly" in result.diagnostics[0].message


async def test_function_receives_exact_two_argument_writer_and_captures_writes() -> None:
    code = """
def extract(message, write):
    write("explosions", {"place": message["text"], "confidence": 0.9})
"""
    result = await execute_extractor(code, {"text": "Київ"}, 15_000, 64_000, 2_048)

    assert result.error is None
    assert len(result.writes) == 1
    assert result.writes[0].table == "explosions"
    assert result.writes[0].values["place"] == "Київ"


async def test_class_entry_point_is_supported() -> None:
    code = """
class Extractor:
    def extract(self, message, write):
        if message["text"]:
            write("ee_alerts", {"active": True})
"""
    result = await execute_extractor(code, {"text": "тривога"}, 15_000, 64_000, 2_048)

    assert result.error is None
    assert result.writes[0].table == "ee_alerts"


async def test_timeout_contains_infinite_loop() -> None:
    result = await execute_extractor(
        "def extract(message, write):\n    while True:\n        pass",
        {},
        150,
        64_000,
        2_048,
    )

    assert result.timed_out
    assert "timeout" in (result.error or "")


async def test_exception_discards_captured_writes() -> None:
    code = """
def extract(message, write):
    write("ee_explosions", {"place": "Київ"})
    raise RuntimeError("bad parser")
"""
    result = await execute_extractor(code, {}, 15_000, 64_000, 2_048)

    assert result.writes == []
    assert "bad parser" in (result.error or "")


async def test_useful_helpers_imports_and_classes_remain_supported() -> None:
    code = """
import re

class Matcher:
    def __init__(self, pattern):
        self.pattern = re.compile(pattern, re.IGNORECASE)

    def matches(self, text):
        return bool(self.pattern.search(text))

def extract(message, write):
    if Matcher(r"вибух").matches(message["text"]):
        write("explosions", {"label": "Вибух"})
"""

    result = await execute_extractor(code, {"text": "Чутно ВИБУХ"}, 15_000, 64_000, 2_048)

    assert result.error is None
    assert result.writes[0].table == "explosions"


async def test_datetime_values_cross_the_child_process_boundary() -> None:
    code = """
from datetime import datetime, timezone

def extract(message, write):
    write("targets", {"occurredAt": datetime.now(timezone.utc)})
"""

    result = await execute_extractor(code, {}, 15_000, 64_000, 2_048)

    assert result.error is None
    assert isinstance(result.writes[0].values["occurredAt"], str)


@pytest.mark.parametrize("module", ["os", "socket", "subprocess"])
def test_validation_rejects_process_filesystem_and_network_imports(module: str) -> None:
    result = validate_code(f"import {module}\n\ndef extract(message, write):\n    pass")

    assert not result.valid
    assert "not allowed" in result.diagnostics[0].message


def test_validation_rejects_direct_proc_file_access() -> None:
    result = validate_code("def extract(message, write):\n    open('/proc/1/environ').read()")

    assert not result.valid
    assert "open" in result.diagnostics[0].message


def test_validation_rejects_transitive_module_handle_access() -> None:
    direct = validate_code("import typing\n\ndef extract(message, write):\n    return typing.sys.modules")
    imported = validate_code("from typing import sys\n\ndef extract(message, write):\n    pass")

    assert not direct.valid
    assert not imported.valid


def test_validation_rejects_traceback_frame_escape() -> None:
    result = validate_code(
        """
def extract(message, write):
    try:
        raise RuntimeError("escape")
    except RuntimeError as error:
        return error.__traceback__.tb_frame.f_back.f_globals
"""
    )

    assert not result.valid
    assert "not allowed" in result.diagnostics[0].message


@pytest.mark.skipif(os.name != "posix", reason="POSIX process groups provide the container containment contract")
def test_terminate_process_tree_prevents_descendant_from_surviving(tmp_path) -> None:
    marker = tmp_path / "descendant-survived"
    descendant = f"import time; time.sleep(0.7); open({str(marker)!r}, 'w').write('alive')"
    parent = (
        "import subprocess, sys, time; "
        f"child=subprocess.Popen([sys.executable, '-c', {descendant!r}]); "
        "print(child.pid, flush=True); time.sleep(30)"
    )
    process = subprocess.Popen(
        [sys.executable, "-c", parent],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    assert process.stdout is not None
    assert process.stdout.readline().strip().isdigit()

    _terminate_process_tree(process)
    time.sleep(1)

    assert process.poll() is not None
    assert not marker.exists()
