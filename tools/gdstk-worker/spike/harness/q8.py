"""Q8 -- the write side (brief-oasis-gdstk.md §3). Each §8a fixture as circuitRF's OWN GdsiiReader sees it (the
InterchangeStructures an export would start from) is written as OASIS through gdstk -- detect_rectangles,
detect_trapezoids, compression 6, CRC32, standard properties off -- TWICE, then read back through the worker
(whose open runs oas_validate first). Reports sizes against circuitRF's GDSII, whether the two writes are
the same bytes, and whether what comes back equals what went in. Scratch harness code.

    python3 q8.py <q4-dir> <out-dir>
"""
import glob
import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
import q4  # noqa: E402
from gdstk_client import Refused, Worker, read_library, write_library  # noqa: E402

OPTS = {"compression_level": 6, "detect_rectangles": True, "detect_trapezoids": True, "validation": "crc32",
        "standard_properties": False}


def main():
    src_dir, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    w = Worker()
    tot_gds = tot_oas = 0
    for g in sorted(glob.glob(os.path.join(src_dir, "*.circuitrf.gds"))):
        name = os.path.basename(g)[:-len(".circuitrf.gds")]
        ours = q4.ours_read(g)
        lib = {"precision_m": ours["precision_m"], "cells": ours["cells"]}
        a, b = os.path.join(out, name + ".1.oas"), os.path.join(out, name + ".2.oas")
        rec = {"fixture": name}
        try:
            write_library(lib, a, "oas", w, OPTS)
            write_library(lib, b, "oas", w, OPTS)
            back = read_library(a, "oas", w, tolerance_dbu=0.5)
            for c in back["cells"].values():
                for p in c["paths"]:
                    if p["ext"] is None:
                        p.pop("ext")
            ba, bb = open(a, "rb").read(), open(b, "rb").read()
            rec.update({"gds_bytes": os.path.getsize(g), "oas_bytes": len(ba), "deterministic": ba == bb,
                        "validated_and_read": True, "differences": canon.diff(lib, back, "circuitRF's reading", "OASIS read back")})
            tot_gds += rec["gds_bytes"]
            tot_oas += rec["oas_bytes"]
        except Refused as r:
            rec["refused"] = r.reply
        print(json.dumps(rec))
    w.close()
    print(json.dumps({"total_gds_bytes": tot_gds, "total_oas_bytes": tot_oas}))


if __name__ == "__main__":
    main()
