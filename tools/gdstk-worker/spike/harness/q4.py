"""Q4 -- GDSII agreement (brief-oasis-gdstk.md §3 Q4 and §8). Every §8a fixture is written by BOTH
writers (circuitRF's GdsiiWriter through GdsiiDump, gdstk's write_gds through the worker), each file is read
by BOTH readers, and every reading is compared under §8b's semantic equality with the fixture and with the
other reader. Also writes the corpus G0 delivers: <name>.circuitrf.gds, <name>.gdstk.gds, <name>.gdstk.oas.

    python3 q4.py <dir> [rid]

Scratch harness code.
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
import q6  # noqa: E402  (its independent GDSII record writer)
from gdstk_client import Refused, Worker, read_library, worker_path, write_library  # noqa: E402

DUMP = os.path.expanduser("~/.circuitRF-build/gdstk/1.0.1/gdsiidump/GdsiiDump")


def rect(l, x1, y1, x2, y2, d=0):
    return {"layer": l, "datatype": d, "xy": [x1, y1, x2, y1, x2, y2, x1, y2]}


def ref(cell, x, y, rot=0, mag=1, mirror=False, rep=None):
    r = {"cell": cell, "x": x, "y": y, "rotation": rot, "magnification": mag, "mirror": mirror}
    if rep:
        r["rep"] = rep
    return r


def label(l, text, x, y, rot=0, mag=1, mirror=False, texttype=0):
    return {"layer": l, "texttype": texttype, "text": text, "x": x, "y": y, "anchor": 0, "rotation": rot,
            "magnification": mag, "mirror": mirror}


def path(l, w, end, xy, ext=None, d=0):
    return {"layer": l, "datatype": d, "width": w, "end": end, "ext": ext, "xy": xy}


def lib(cells, grid=1000):
    for c in cells.values():
        for k in ("polygons", "paths", "labels", "refs"):
            c.setdefault(k, [])
    return {"grid_per_um": grid, "precision_m": 1e-6 / grid, "cells": cells}


LEAF = {"polygons": [rect(1, 0, 0, 1000, 500)]}

# §7c's simple cases, expected to agree.
SIMPLE = {
    "rectangle": lib({"top": {"polygons": [rect(1, 0, 0, 2000, 1000)]}}),
    "polygon7": lib({"top": {"polygons": [{"layer": 2, "datatype": 0,
                                           "xy": [0, 0, 4000, 0, 4000, 1000, 3000, 1000, 3000, 3000, 1000, 2500, 0, 3000]}]}}),
    "path-ends": lib({"top": {"paths": [path(5, 100, "flush", [0, 0, 1000, 0, 1000, 1000]),
                                        path(5, 100, "round", [0, 3000, 1000, 3000]),
                                        path(5, 100, "halfwidth", [0, 6000, 1000, 6000, 1000, 7000])]}}),
    "labels": lib({"top": {"labels": [label(7, "east", 0, 0), label(7, "north", 1000, 0, rot=90),
                                      label(7, "mirrored", 2000, 0, mirror=True), label(7, "big", 3000, 0, mag=2)]}}),
    "sref-transform": lib({"leaf": dict(LEAF), "top": {"refs": [ref("leaf", 5000, 5000, rot=90, mag=2, mirror=True)]}}),
    "aref-3x2": lib({"leaf": dict(LEAF), "top": {"refs": [ref("leaf", 0, 0, rep={"kind": "rectangular", "columns": 3, "rows": 2,
                                                                                    "spacing": [2000, 1500]})]}}),
    "hierarchy": lib({"leaf": dict(LEAF), "mid": {"refs": [ref("leaf", 0, 0), ref("leaf", 3000, 0)]},
                      "top": {"refs": [ref("mid", 0, 0), ref("leaf", 0, 5000, rot=180)]}}),
    "layers-datatypes": lib({"top": {"polygons": [rect(1, 0, 0, 100, 100, 0), rect(1, 200, 0, 300, 100, 1),
                                                  rect(2, 400, 0, 500, 100, 0), rect(2, 600, 0, 700, 100, 1)]}}),
    "dbu-1nm": lib({"top": {"polygons": [rect(1, -123457, 7, 99991, 1003)]}}, grid=1000),
    "dbu-0p25nm": lib({"top": {"polygons": [rect(1, -123457, 7, 99991, 1003)]}}, grid=4000),
    "empty-cell": lib({"empty": {}, "top": {"refs": [ref("empty", 0, 0)]}}),
}

# Beyond the simple cases: where the two stacks are expected to part company, each one a question.
PROBES = {
    "aref-rotated": lib({"leaf": dict(LEAF), "top": {"refs": [ref("leaf", 0, 0, rot=90, rep={"kind": "rectangular", "columns": 3,
                                                                                              "rows": 2, "spacing": [2000, 1500]})]}}),
    "aref-mirrored": lib({"leaf": dict(LEAF), "top": {"refs": [ref("leaf", 0, 0, mirror=True, rep={"kind": "rectangular", "columns": 2,
                                                                                                    "rows": 2, "spacing": [2000, 1500]})]}}),
    "path-extensions": lib({"top": {"paths": [path(5, 100, "extended", [0, 0, 1000, 0], ext=[50, 50]),
                                              path(5, 100, "extended", [0, 3000, 1000, 3000], ext=[20, 70])]}}),
    "path-1dbu-segment": lib({"top": {"paths": [path(5, 100, "flush", [0, 0, 1, 0, 1001, 0])]}}),
    "path-odd-width": lib({"top": {"paths": [path(5, 3, "flush", [0, 0, 1000, 0])]}}),
    "label-texttype": lib({"top": {"labels": [label(7, "pin", 0, 0, texttype=1), label(7, "five", 100, 0, texttype=5)]}}),
    "label-45deg": lib({"top": {"labels": [label(7, "slant", 0, 0, rot=45)]}}),
    "sref-30deg": lib({"leaf": dict(LEAF), "top": {"refs": [ref("leaf", 0, 0, rot=30)]}}),
    "layer-40000": lib({"top": {"polygons": [rect(40000, 0, 0, 100, 100, 3)]}}),
    "datatype-40000": lib({"top": {"polygons": [rect(3, 0, 0, 100, 100, 40000)]}}),
    "big-polygon-8000": lib({"top": {"polygons": [{"layer": 1, "datatype": 0,
                                                   "xy": [v for i in range(8000) for v in (i, (i * 7919) % 1000 + (1000 if i % 2 else 0))]}]}}),
}


def gds_box_file(path):
    """A GDSII BOX element (record 0x2D) beside a BOUNDARY, written byte by byte: not something either
    writer emits, but a file from another tool may carry it."""
    rec = q6.rec
    stamp = q6.struct.pack(">12h", 2026, 1, 1, 0, 0, 0, 2026, 1, 1, 0, 0, 0)
    out = rec(0x00, 2, q6.struct.pack(">h", 600)) + rec(0x01, 2, stamp) + rec(0x02, 6, b"LIB\x00")
    out += rec(0x03, 5, q6.real8(1e-3) + q6.real8(1e-9)) + rec(0x05, 2, stamp) + rec(0x06, 6, b"top\x00")
    out += rec(0x08, 0) + rec(0x0D, 2, q6.struct.pack(">h", 1)) + rec(0x0E, 2, q6.struct.pack(">h", 0))
    out += rec(0x10, 3, q6.struct.pack(">10i", 0, 0, 10, 0, 10, 10, 0, 10, 0, 0)) + rec(0x11, 0)
    out += rec(0x2D, 0) + rec(0x0D, 2, q6.struct.pack(">h", 2)) + rec(0x2E, 2, q6.struct.pack(">h", 0))
    out += rec(0x10, 3, q6.struct.pack(">10i", 100, 0, 110, 0, 110, 10, 100, 10, 100, 0)) + rec(0x11, 0)
    out += rec(0x07, 0) + rec(0x04, 0)
    open(path, "wb").write(out)
    return lib({"top": {"polygons": [rect(1, 0, 0, 10, 10), rect(2, 100, 0, 110, 10)]}})


def ours_write(l, path):
    j = path + ".src.json"
    json.dump(l, open(j, "w"))
    p = subprocess.run([DUMP, "write", j, path], capture_output=True, text=True)
    os.remove(j)
    if p.returncode != 0:
        return {"error": (p.stderr or p.stdout)[-600:]}
    return json.loads(p.stdout)


def ours_read(path):
    p = subprocess.run([DUMP, "read", path], capture_output=True, text=True)
    if p.returncode != 0:
        return {"error": (p.stderr or p.stdout)[-600:]}
    out = json.loads(p.stdout)
    for c in out["cells"].values():
        for x in c["paths"]:
            if x["ext"] is None:
                x.pop("ext")
    return out


def gdstk_read(path, w):
    try:
        out = read_library(path, "gds", w, tolerance_dbu=0.5)
    except Refused as r:
        return {"error": r.reply}
    for c in out["cells"].values():
        for x in c["paths"]:
            if x["ext"] is None:
                x.pop("ext")
    return out


def source(l):
    """The fixture in the comparison's terms: paths carry ext only when extended."""
    out = json.loads(json.dumps(l))
    for c in out["cells"].values():
        for x in c["paths"]:
            if x.get("ext") is None:
                x.pop("ext", None)
    return out


def d(a, b, na, nb):
    if "error" in a:
        return [f"{na} failed: {a['error']}"]
    if "error" in b:
        return [f"{nb} failed: {b['error']}"]
    return canon.diff(a, b, na, nb)


def run(name, l, out_dir, w, box=False):
    src = source(l)
    rec = {"fixture": name}
    if box:
        theirs = ours = os.path.join(out_dir, f"{name}.gds")
        files = {"file": ours}
    else:
        ours = os.path.join(out_dir, f"{name}.circuitrf.gds")
        theirs = os.path.join(out_dir, f"{name}.gdstk.gds")
        rec["ours_lost"] = ours_write(l, ours).get("lost")
        try:
            write_library(l, theirs, "gds", w)
            write_library(l, os.path.join(out_dir, f"{name}.gdstk.oas"), "oas", w,
                          {"compression_level": 6, "detect_rectangles": True, "detect_trapezoids": True, "validation": "crc32"})
        except Refused as r:
            rec["gdstk_write_refused"] = r.reply
        files = {"circuitrf": ours, "gdstk": theirs}
    for writer, f in files.items():
        if not os.path.exists(f):
            continue
        a, b = ours_read(f), gdstk_read(f, w)
        rec[f"{writer}->circuitrf"] = d(src, a, "fixture", "circuitrf read")
        rec[f"{writer}->gdstk"] = d(src, b, "fixture", "gdstk read")
        rec[f"{writer}: readers"] = d(a, b, "circuitrf read", "gdstk read")
        rec[f"{writer}: gdstk messages"] = b.get("messages") if "error" not in b else None
        rec[f"{writer}: circuitrf diagnostics"] = a.get("diagnostics") if "error" not in a else None
        if "error" not in a:
            rec[f"{writer}: circuitrf units"] = [a["unit_m"], a["precision_m"]]
        if "error" not in b:
            rec[f"{writer}: gdstk units"] = [b["unit_m"], b["precision_m"]]
    return rec


def main():
    out_dir = sys.argv[1]
    rid = (sys.argv[2] or None) if len(sys.argv) > 2 else None
    os.makedirs(out_dir, exist_ok=True)
    w = Worker(worker_path(rid))
    try:
        for group, fixtures in (("simple", SIMPLE), ("probe", PROBES)):
            for name, l in fixtures.items():
                r = run(name, l, out_dir, w)
                r["group"] = group
                print(json.dumps(r))
        l = gds_box_file(os.path.join(out_dir, "box-record.gds"))
        r = run("box-record", l, out_dir, w, box=True)
        r["group"] = "probe"
        print(json.dumps(r))
    finally:
        w.close()


if __name__ == "__main__":
    main()
