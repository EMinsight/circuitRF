"""The same answers on every RID (brief-oasis-gdstk.md §3 Q1, "Their output must equal the macOS build's under
§8b, and each OASIS file must be byte-identical if Q8 shows the writer is deterministic").

For every corpus file: read it (canonical form, hashed), then write the canonical form back as GDSII and as
OASIS and hash the bytes. One JSON object per RID; compare two with --compare. Scratch harness code.

    python3 platform_check.py run <out.json> <scratch-dir> <corpus-file>...   (worker from CRF_GDSTK_WORKER,
                                                                             or this machine's spike build)
    python3 platform_check.py compare <reference.json> <other.json>
"""
import hashlib
import json
import os
import shlex
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdstk_client import Refused, Worker, read_library, worker_path, write_library  # noqa: E402


def h(b):
    return hashlib.sha256(b).hexdigest()[:16]


def canonical_bytes(lib):
    keep = {"precision_m": lib["precision_m"], "cells": lib["cells"], "layer_names": lib["layer_names"], "tops": sorted(lib["tops"])}
    return json.dumps(keep, sort_keys=True).encode()


def run(out, scratch, files):
    argv = shlex.split(os.environ["CRF_GDSTK_WORKER"]) if os.environ.get("CRF_GDSTK_WORKER") else [worker_path()]
    os.makedirs(scratch, exist_ok=True)
    w = Worker(argv=argv)
    res = {"worker": argv, "hello": w.request({"op": "hello", "protocol": 1}), "selftest": None, "files": {}}
    res["hello"].pop("_blobs", None)
    try:
        st = w.request({"op": "selftest"})
        st.pop("_blobs", None)
        res["selftest"] = st
    except Refused as r:
        res["selftest"] = r.reply
    for f in files:
        fmt = "oas" if f.endswith(".oas") else "gds"
        rec = {}
        try:
            lib = read_library(f, fmt, w, tolerance_dbu=0.5)
            rec["read"] = h(canonical_bytes(lib))
            rec["nonintegral"] = lib["nonintegral"]
            base = os.path.join(scratch, os.path.basename(f))
            src = {"precision_m": lib["precision_m"], "cells": lib["cells"]}
            for c in src["cells"].values():
                for p in c["paths"]:
                    if p.get("ext") is None:
                        p.pop("ext", None)
            for ofmt in ("gds", "oas"):
                try:
                    write_library(src, base + ".rt." + ofmt, ofmt, w,
                                  {"compression_level": 6, "detect_rectangles": True, "detect_trapezoids": True, "validation": "crc32"})
                    rec["write_" + ofmt] = h(open(base + ".rt." + ofmt, "rb").read())
                except Refused as r:
                    rec["write_" + ofmt] = "refused: " + r.reply.get("code", "")
        except Refused as r:
            rec["read"] = "refused: " + r.reply.get("code", "") + " " + r.reply.get("detail", "")[:120]
        except EOFError:
            rec["read"] = "worker stopped"
            w = Worker(argv=argv)
        res["files"][os.path.basename(f)] = rec
    w.close()
    json.dump(res, open(out, "w"), indent=1)


def compare(a, b):
    A, B = json.load(open(a)), json.load(open(b))
    same = diff = 0
    for f, ra in A["files"].items():
        rb = B["files"].get(f)
        if rb == ra:
            same += 1
            continue
        diff += 1
        print(f, {k: (ra.get(k), (rb or {}).get(k)) for k in set(ra) | set(rb or {}) if ra.get(k) != (rb or {}).get(k)})
    print(json.dumps({"reference": A["hello"].get("rid"), "other": B["hello"].get("rid"), "files_equal": same, "files_different": diff,
                      "other_selftest": B["selftest"]}))


if __name__ == "__main__":
    if sys.argv[1] == "run":
        run(sys.argv[2], sys.argv[3], sys.argv[4:])
    else:
        compare(sys.argv[2], sys.argv[3])
