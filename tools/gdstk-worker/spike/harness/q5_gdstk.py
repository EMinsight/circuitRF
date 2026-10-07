"""Q5, gdstk's own half: each OASIS feature written by gdstk's write_oas (through the worker's write path,
the one G4 will use) and read back through the worker. Prints one JSON line per fixture: what went in,
what came back, the differences. Scratch harness code.

    python3 q5_gdstk.py <dir> [rid]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
from gdstk_client import Refused, Worker, read_library, worker_path, write_library  # noqa: E402

G = 1000  # 1 nm


def rect(l, x1, y1, x2, y2, d=0, rep=None):
    p = {"layer": l, "datatype": d, "xy": [x1, y1, x2, y1, x2, y2, x1, y2]}
    if rep:
        p["rep"] = rep
    return p


def lib(cells):
    return {"grid_per_um": G, "precision_m": 1e-9, "cells": cells}


FIXTURES = {
    "rectangles": lib({"r": {"polygons": [rect(1, 0, 0, 2000, 2000), rect(1, 5000, 0, 8000, 1000)]}}),
    "polygon7": lib({"p": {"polygons": [{"layer": 2, "datatype": 0,
                                         "xy": [0, 0, 4000, 0, 4000, 1000, 3000, 1000, 3000, 3000, 1000, 2500, 0, 3000]}]}}),
    # Trapezoids: horizontal parallel sides (OASIS 23/24/25 or 26) and vertical ones, plus a 45-degree triangle.
    "trapezoids": lib({"t": {"polygons": [
        {"layer": 3, "datatype": 0, "xy": [0, 0, 1000, 0, 800, 500, 200, 500]},
        {"layer": 3, "datatype": 0, "xy": [2000, 0, 2500, 200, 2500, 800, 2000, 1000]},
        {"layer": 3, "datatype": 0, "xy": [4000, 0, 5000, 0, 4000, 1000]},
        {"layer": 3, "datatype": 0, "xy": [6000, 0, 7000, 0, 7000, 1000, 6500, 1000]},
    ]}}),
    "paths": lib({"w": {"paths": [
        {"layer": 5, "datatype": 0, "width": 100, "end": "flush", "xy": [0, 0, 1000, 0, 1000, 1000]},
        {"layer": 5, "datatype": 0, "width": 100, "end": "halfwidth", "xy": [0, 3000, 1000, 3000]},
        {"layer": 5, "datatype": 0, "width": 100, "end": "extended", "ext": [20, 30], "xy": [0, 6000, 1000, 6000]},
        {"layer": 5, "datatype": 0, "width": 100, "end": "round", "xy": [0, 9000, 1000, 9000]},
        {"layer": 5, "datatype": 0, "width": 3, "end": "flush", "xy": [0, 12000, 1000, 12000]},
        {"layer": 5, "datatype": 0, "width": 100, "end": "flush", "xy": [0, 15000, 1, 15000, 1001, 15000]},
    ]}}),
    # OASIS TEXT has no rotation, magnification, mirror or anchor; gdstk's header says so.
    "labels": lib({"l": {"labels": [
        {"layer": 7, "texttype": 3, "text": "plain", "x": 0, "y": 0, "anchor": 5, "rotation": 0, "magnification": 1, "mirror": False},
        {"layer": 7, "texttype": 0, "text": "rot90", "x": 100, "y": 0, "anchor": 5, "rotation": 90, "magnification": 1, "mirror": False},
        {"layer": 7, "texttype": 0, "text": "mag2mirror", "x": 200, "y": 0, "anchor": 5, "rotation": 0, "magnification": 2, "mirror": True},
    ]}}),
    "placements": lib({
        "c": {"polygons": [rect(1, 0, 0, 100, 50)]},
        "t": {"refs": [
            {"cell": "c", "x": 0, "y": 0, "rotation": 0, "magnification": 1, "mirror": False},
            {"cell": "c", "x": 1000, "y": 0, "rotation": 90, "magnification": 1, "mirror": False},
            {"cell": "c", "x": 2000, "y": 0, "rotation": 180, "magnification": 1, "mirror": False},
            {"cell": "c", "x": 3000, "y": 0, "rotation": 270, "magnification": 1, "mirror": True},
            {"cell": "c", "x": 4000, "y": 0, "rotation": 90, "magnification": 2, "mirror": True},
            {"cell": "c", "x": 5000, "y": 0, "rotation": 30, "magnification": 1, "mirror": False},
        ]}}),
    "ref-repetitions": lib({
        "c": {"polygons": [rect(1, 0, 0, 100, 50)]},
        "t": {"refs": [
            {"cell": "c", "x": 0, "y": 0, "rep": {"kind": "rectangular", "columns": 3, "rows": 2, "spacing": [300, 200]}},
            {"cell": "c", "x": 10000, "y": 0, "rep": {"kind": "rectangular", "columns": 4, "rows": 1, "spacing": [150, 0]}},
            {"cell": "c", "x": 20000, "y": 0, "rep": {"kind": "rectangular", "columns": 1, "rows": 3, "spacing": [0, 250]}},
            {"cell": "c", "x": 30000, "y": 0, "rep": {"kind": "regular", "columns": 2, "rows": 3, "v1": [300, 100], "v2": [-50, 400]}},
            {"cell": "c", "x": 40000, "y": 0, "rep": {"kind": "explicit", "offsets": [100, 0, 100, 100, -150, 130]}},
            {"cell": "c", "x": 50000, "y": 0, "rep": {"kind": "explicit_x", "coords": [100, 350, 400]}},
            {"cell": "c", "x": 60000, "y": 0, "rep": {"kind": "explicit_y", "coords": [100, 130]}},
            # gdstk#247: a negative explicit offset; the issue says the OASIS written is invalid.
            {"cell": "c", "x": 70000, "y": 0, "rep": {"kind": "explicit_x", "coords": [100, -300]}},
            {"cell": "c", "x": 80000, "y": 0, "rep": {"kind": "rectangular", "columns": 3, "rows": 2, "spacing": [-300, 200]}},
        ]}}),
    "shape-repetition": lib({"s": {"polygons": [
        rect(1, 0, 0, 10, 10, rep={"kind": "rectangular", "columns": 3, "rows": 2, "spacing": [100, 100]})]}}),
    "hierarchy": lib({
        "leaf": {"polygons": [rect(1, 0, 0, 10, 10)]},
        "mid": {"refs": [{"cell": "leaf", "x": 0, "y": 0}, {"cell": "leaf", "x": 100, "y": 0}]},
        "top": {"refs": [{"cell": "mid", "x": 0, "y": 0}, {"cell": "leaf", "x": 500, "y": 500}]},
        "empty": {}}),
}

OPTIONS = {
    "default": {"compression_level": 6, "detect_rectangles": True, "detect_trapezoids": True, "validation": "crc32",
                "standard_properties": False},
}


def fill(cell):
    for k in ("polygons", "paths", "labels", "refs"):
        cell.setdefault(k, [])
    for r in cell["refs"]:
        r.setdefault("rotation", 0)
        r.setdefault("magnification", 1)
        r.setdefault("mirror", False)
    for p in cell["paths"]:
        p.setdefault("ext", None)
    return cell


def main():
    d = sys.argv[1]
    rid = (sys.argv[2] or None) if len(sys.argv) > 2 else None
    os.makedirs(d, exist_ok=True)
    runs = [(n, f, "default") for n, f in FIXTURES.items()]
    # CBLOCK at level 0 and 6, CRC and checksum, standard properties on: one fixture, several encodings.
    runs += [("hierarchy", FIXTURES["hierarchy"], name) for name in ("deflate0", "deflate9", "checksum32", "stdprops")]
    OPTIONS["deflate0"] = dict(OPTIONS["default"], compression_level=0)
    OPTIONS["deflate9"] = dict(OPTIONS["default"], compression_level=9)
    OPTIONS["checksum32"] = dict(OPTIONS["default"], validation="checksum32")
    OPTIONS["stdprops"] = dict(OPTIONS["default"], standard_properties=True)
    for name, l, opt in runs:
        for c in l["cells"].values():
            fill(c)
        path = os.path.join(d, f"q5-{name}{'' if opt == 'default' else '-' + opt}.oas")
        w = Worker(worker_path(rid))
        try:
            wr = write_library(l, path, "oas", w, OPTIONS[opt])
            got = read_library(path, "oas", w)
        except Refused as r:
            print(json.dumps({"fixture": name, "options": opt, "refused": r.reply}))
            continue
        finally:
            w.close()
        for c in got["cells"].values():
            for p in c["paths"]:
                if p["end"] != "extended":
                    p["ext"] = None
        diffs = canon.diff(l, got, "written", "read back")
        print(json.dumps({"fixture": name, "options": opt, "bytes": wr["bytes"], "write_messages": wr["messages"],
                          "read_messages": got["messages"], "equal": not diffs, "differences": diffs,
                          "nonintegral": got["nonintegral"], "tops": got["tops"]}))


if __name__ == "__main__":
    main()
