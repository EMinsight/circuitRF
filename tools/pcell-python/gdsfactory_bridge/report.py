"""``python -m gdsfactory_bridge.report <module>`` — what a kit offers, as one JSON object on stdout.

The import report's source. It runs the SAME :func:`register_pdk` the kit's entry script runs, into a
private registry, and prints its structured findings; circuitRF runs it once, after consent, so the
report says exactly what the palette will be given. stdout carries the JSON and nothing else.
"""

from __future__ import annotations

import contextlib
import io
import json
import sys

import circuitrf_pcell as crf

from .bridge import register_pdk


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("usage: python -m gdsfactory_bridge.report <module>", file=sys.stderr)
        return 2

    # A kit that prints while it imports must not corrupt the one line this writes.
    noise = io.StringIO()
    with contextlib.redirect_stdout(noise):
        result = register_pdk(argv[0], registry=crf.Registry())
    if noise.getvalue():
        sys.stderr.write(noise.getvalue())

    body = dict(result.report)
    body["problems"] = result.problems
    print(json.dumps(body))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
