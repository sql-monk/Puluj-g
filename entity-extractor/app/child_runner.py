from __future__ import annotations

import json
import sys

from .runtime import _json_default, run_user_batch, run_user_code


def main() -> None:
    request = json.load(sys.stdin)
    if request.get("mode") == "batch":
        payload = run_user_batch(
            request["extractors"],
            request["messages"],
            int(request["maxOutputBytes"]),
            int(request["maxMemoryMb"]),
            int(request["timeoutMs"]),
            bool(request.get("sparse")),
        )
        json.dump(payload, sys.stdout, ensure_ascii=False, default=_json_default)
        return
    payload = run_user_code(
        request["code"],
        request["message"],
        int(request["maxOutputBytes"]),
        int(request["maxMemoryMb"]),
        int(request["timeoutMs"]),
    )
    json.dump(payload, sys.stdout, ensure_ascii=False, default=_json_default)


if __name__ == "__main__":
    main()
