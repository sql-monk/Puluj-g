"""Build the entity extractors stored in ``ee_extractors`` from the files next to this script.

The sandbox imports nothing but a standard-library allowlist, so each stored extractor is ``common.py`` followed by
its own file. Usage (prints SQL; pipe it into psql as the owner of ``ee_extractors``)::

    python entity-extractor/extractors/install.py | docker exec -i puluj-g-postgis-1 psql -U puluj -d puluj

``--disable`` stores the code but leaves the extractors switched off.
"""

from __future__ import annotations

import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
# ee_extractors.name -> execution order; the names are the ones the AddEntityExtractor migration seeded.
EXTRACTORS = {
    "alert": 10,
    "target": 20,
    "track": 30,
    "explosion": 40,
    "impact": 50,
    "airDefenseAction": 60,
    "launch": 70,
    "takeoff": 80,
}
TIMEOUT_MS = 5000


def build(name: str) -> str:
    common = (HERE / "common.py").read_text(encoding="utf-8")
    own = (HERE / f"{name}.py").read_text(encoding="utf-8")
    return f"{common.rstrip()}\n\n\n# ---- {name} ----\n{own}"


def _literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def sql(enabled: bool = True) -> str:
    statements = ["BEGIN;"]
    for name, order in EXTRACTORS.items():
        code = _literal(build(name))
        statements.append(
            "INSERT INTO ee_extractors (name, code, enabled, execution_order, timeout_ms)\n"
            f"VALUES ({_literal(name)}, {code}, {str(enabled).lower()}, {order}, {TIMEOUT_MS})\n"
            "ON CONFLICT (name) DO UPDATE SET code = EXCLUDED.code, enabled = EXCLUDED.enabled,\n"
            "    execution_order = EXCLUDED.execution_order, timeout_ms = EXCLUDED.timeout_ms;"
        )
    statements.append("COMMIT;")
    return "\n".join(statements) + "\n"


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdout.write(sql(enabled="--disable" not in sys.argv[1:]))
