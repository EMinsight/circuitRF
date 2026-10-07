"""Q3 -- file paths (brief-oasis-gdstk.md §3). gdstk takes const char* filenames; this reads and writes a GDSII
and an OASIS file through the worker at paths with a space, e-acute, Omega, CJK, and (on Windows) a path longer
than 260 characters. ON WINDOWS THIS RUNS IN THE OWNER'S REAL-WINDOWS SESSION (windows-session/), never under
Wine. Scratch harness code.

    python3 q3_paths.py <dir>
"""
import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdstk_client import Refused, Worker, read_library, write_library  # noqa: E402

LIB = {"grid_per_um": 1000, "precision_m": 1e-9,
       "cells": {"top": {"polygons": [{"layer": 1, "datatype": 0, "xy": [0, 0, 1000, 0, 1000, 500, 0, 500]}],
                         "paths": [], "labels": [], "refs": []}}}

NAMES = {
    "space": "with space",
    "e-acute": "café",
    "omega": "Ω-ohm",
    "cjk": "回路",
}


def main():
    root = sys.argv[1]
    os.makedirs(root, exist_ok=True)
    cases = dict(NAMES)
    long_dir = os.path.join(root, *(["d" * 50] * 5))  # > 260 characters in all
    cases["long-260"] = None
    out = []
    w = Worker()
    for key, name in cases.items():
        d = long_dir if name is None else os.path.join(root, name)
        os.makedirs(d, exist_ok=True)
        for fmt in ("gds", "oas"):
            p = os.path.join(d, f"layout {key}.{fmt}")
            rec = {"case": key, "format": fmt, "path_chars": len(os.path.abspath(p))}
            try:
                write_library(LIB, p, fmt, w)
                rec["written"] = os.path.exists(p)
            except Refused as r:
                rec["written"] = False
                rec["write_refused"] = r.reply.get("code") + ": " + r.reply.get("detail", "")
            # Read a copy Python itself placed there, so a failed write does not hide the read's answer.
            if not rec["written"]:
                src = os.path.join(root, f"plain.{fmt}")
                write_library(LIB, src, fmt, w)
                shutil.copyfile(src, p)
            try:
                lib = read_library(p, fmt, w)
                rec["read"] = list(lib["cells"]) == ["top"]
            except Refused as r:
                rec["read"] = False
                rec["read_refused"] = r.reply.get("code") + ": " + r.reply.get("detail", "")
            out.append(rec)
            print(json.dumps(rec, ensure_ascii=True))
    w.close()


if __name__ == "__main__":
    main()
