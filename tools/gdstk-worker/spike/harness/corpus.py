"""Assembles testdata/interchange/gdstk/ -- G0's committed corpus (brief-oasis-gdstk.md §3 "G0 deliverables").
Fixed data, written ONCE by this script from the spike's runs; tests never regenerate it. Scratch harness code.

    python3 corpus.py <runs-dir> <out-dir>

    8a/          §8a: each fixture as <name>.circuitrf.gds (circuitRF's GdsiiWriter, through GdsiiDump),
                 <name>.gdstk.gds and <name>.gdstk.oas (gdstk 1.0.1 through the spike worker), and <name>.json:
                 the source, each file's writer and SHA-256, and what each reader made of each file (Q4)
    oasis-gdstk/ Q5: one OASIS feature per file, written by gdstk's write_oas through the spike worker, beside
                 <name>.json (the source, the options, and what gdstk reads back, differences listed)
    oasis-hand/  Q5's independent half: OASIS encoded byte by byte by oasis_hand.py (circuitRF's own encoder),
                 beside <name>.expected.json (the content an ideal reader produces)

Files of 50 KB or more are left out and listed. q5-ref-repetitions.oas is REWRITTEN here without its gdstk#247
row (a negative explicit offset, which gdstk writes as ~2^64 and which then reads back differently by libm);
every other file is copied from the runs as it is.
"""
import hashlib
import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
import q4  # noqa: E402
import q5_gdstk  # noqa: E402
from gdstk_client import Worker, read_library, write_library  # noqa: E402

LIMIT = 50 * 1024


def sha(p):
    return hashlib.sha256(open(p, "rb").read()).hexdigest()


def main():
    runs, out = sys.argv[1], sys.argv[2]
    if os.path.exists(out):
        shutil.rmtree(out)
    for d in ("8a", "oasis-gdstk", "oasis-hand"):
        os.makedirs(os.path.join(out, d))
    left_out = []

    # §8a, with Q4's verdicts
    q4rows = {json.loads(l)["fixture"]: json.loads(l) for l in open(os.path.join(runs, "q4", "q4-osx-arm64.jsonl"))}
    fixtures = [(n, "simple", l) for n, l in q4.SIMPLE.items()] + [(n, "probe", l) for n, l in q4.PROBES.items()]
    for name, group, l in fixtures + [("box-record", "probe", None)]:
        row = q4rows[name]
        files = {}
        writers = {"circuitrf.gds": "circuitRF GdsiiWriter (GdsiiDump write)", "gdstk.gds": "gdstk 1.0.1 write_gds (spike worker)",
                   "gdstk.oas": "gdstk 1.0.1 write_oas (spike worker): compression 6, detect rectangles and trapezoids, CRC32"}
        if name == "box-record":
            writers = {"gds": "q4.py gds_box_file, byte by byte (circuitRF's spike harness)"}
        for suffix, writer in writers.items():
            src = os.path.join(runs, "q4", f"{name}.{suffix}")
            if os.path.getsize(src) >= LIMIT:
                left_out.append(f"8a/{name}.{suffix} ({os.path.getsize(src)} bytes)")
                continue
            shutil.copy(src, os.path.join(out, "8a", f"{name}.{suffix}"))
            files[f"{name}.{suffix}"] = {"writer": writer, "bytes": os.path.getsize(src), "sha256": sha(src)}
        q4v = {k: v for k, v in row.items() if "->" in k or k.endswith(": readers")}
        source = q4.source(l) if l is not None else q4.source(q4.gds_box_file(os.devnull))
        if name == "big-polygon-8000":
            source = {"grid_per_um": 1000, "note": "one polygon, layer 1, datatype 0, 8000 vertices: vertex i = "
                      "(i, (i * 7919) % 1000 + (1000 if i is odd else 0)) -- q4.py PROBES (16,000 integers, not repeated here)"}
        doc = {"fixture": name, "group": group, "source": source, "files": files,
               "q4": {"comment": "empty list = equal under §8b; '<writer>-><reader>' compares the reading with the source",
                      **q4v}}
        json.dump(doc, open(os.path.join(out, "8a", f"{name}.json"), "w"), indent=1)

    # Q5, gdstk's writer
    w = Worker()
    q5files = sorted(f for f in os.listdir(os.path.join(runs, "q5")) if f.startswith("q5-") and f.endswith(".oas"))
    for f in q5files:
        stem = f[3:-4]
        name, opt = (stem.split("-")[0], stem.split("-", 1)[1]) if stem.startswith("hierarchy-") else (stem, "default")
        options = dict(q5_gdstk.OPTIONS["default"])
        options.update({"deflate0": {"compression_level": 0}, "deflate9": {"compression_level": 9},
                        "checksum32": {"validation": "checksum32"}, "stdprops": {"standard_properties": True}}.get(opt, {}))
        src_lib = json.loads(json.dumps(q5_gdstk.FIXTURES[name]))
        for c in src_lib["cells"].values():
            q5_gdstk.fill(c)
        dst = os.path.join(out, "oasis-gdstk", f)
        note = None
        if name == "ref-repetitions":
            src_lib["cells"]["t"]["refs"] = [r for r in src_lib["cells"]["t"]["refs"] if r["x"] != 70000]
            write_library(src_lib, dst, "oas", w, options)
            note = ("rewritten by corpus.py without the gdstk#247 row (x = 70000, explicit_x [100, -300]); "
                    "runs/q5 keeps the original")
        else:
            shutil.copy(os.path.join(runs, "q5", f), dst)
        got = read_library(dst, "oas", w, tolerance_dbu=0.5)
        for c in got["cells"].values():
            for p in c["paths"]:
                if p["end"] != "extended":
                    p["ext"] = None
        doc = {"file": f, "writer": "gdstk 1.0.1 write_oas (spike worker)", "options": options, "bytes": os.path.getsize(dst),
               "sha256": sha(dst), "source": src_lib, "reads_back_as": {"precision_m": got["precision_m"], "cells": got["cells"],
                                                                        "tops": got["tops"]},
               "differences": canon.diff(src_lib, got, "source", "read back")}
        if note:
            doc["note"] = note
        json.dump(doc, open(os.path.join(out, "oasis-gdstk", f[:-4] + ".json"), "w"), indent=1)

    # Q5, the independent encoder
    for f in sorted(os.listdir(os.path.join(runs, "q5"))):
        if f.startswith("hand-") and f.endswith(".oas"):
            shutil.copy(os.path.join(runs, "q5", f), os.path.join(out, "oasis-hand", f))
            e = os.path.join(runs, "q5", f[:-4] + ".expected.json")
            if os.path.exists(e):
                shutil.copy(e, os.path.join(out, "oasis-hand", f[:-4] + ".expected.json"))
    # hand-circle.oas carries no .expected.json in the runs: two CIRCLE records, stated here.
    got = read_library(os.path.join(out, "oasis-hand", "hand-circle.oas"), "oas", w, tolerance_dbu=0.5)
    json.dump({"file": "hand-circle.oas", "grid_per_um": 1000,
               "records": [{"record": "CIRCLE", "layer": 1, "datatype": 0, "radius": 50000, "x": 0, "y": 0},
                           {"record": "CIRCLE", "layer": 1, "datatype": 0, "radius": 500, "x": 200000, "y": 0}],
               "gdstk_reads_as": {"tolerance_dbu": 0.5, "polygons": [len(p["xy"]) // 2 for c in got["cells"].values()
                                                                    for p in c["polygons"]],
                                  "nonintegral_coordinates": got["nonintegral"]},
               "comment": "gdstk turns a CIRCLE into a polygon at read time; the vertex count follows the read "
                          "tolerance, and the vertices are off the grid (the worker must round them, and count them)"},
              open(os.path.join(out, "oasis-hand", "hand-circle.expected.json"), "w"), indent=1)
    w.close()

    sizes = {os.path.join(d, f): os.path.getsize(os.path.join(out, d, f)) for d in ("8a", "oasis-gdstk", "oasis-hand")
             for f in os.listdir(os.path.join(out, d))}
    assert max(sizes.values()) < LIMIT, max(sizes.items(), key=lambda kv: kv[1])
    print(json.dumps({"files": len(sizes), "bytes": sum(sizes.values()), "largest": max(sizes.items(), key=lambda kv: kv[1]),
                      "left_out": left_out}))


if __name__ == "__main__":
    main()
