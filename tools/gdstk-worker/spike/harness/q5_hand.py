"""Q5, the independent half: read every hand-encoded OASIS fixture (oasis_hand.py) through the worker and
compare it with the content the fixture states. Prints one JSON line per fixture. Scratch harness code.

    python3 q5_hand.py <dir> [rid]
"""
import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
from gdstk_client import Refused, read_library, worker_path, Worker  # noqa: E402


def expected_lib(e):
    cells = {}
    for name, c in e["cells"].items():
        cells[name] = {"polygons": [dict(p, datatype=p.get("datatype", 0)) for p in c.get("polygons", [])],
                       "paths": [dict(p, end=end_name(p)) for p in c.get("paths", [])],
                       "labels": c.get("labels", []), "refs": c.get("refs", [])}
    return {"precision_m": 1e-6 / e["grid_per_um"], "cells": cells}


def end_name(p):
    a, b = p["ext"]
    hw = p["width"] // 2
    if a == 0 and b == 0:
        return "flush"
    if a == hw and b == hw:
        return "halfwidth"
    return "extended"


def main():
    d = sys.argv[1]
    rid = (sys.argv[2] or None) if len(sys.argv) > 2 else None
    for f in sorted(glob.glob(os.path.join(d, "hand-*.oas"))):
        name = os.path.basename(f)[:-4]
        e = json.load(open(f[:-4] + ".expected.json"))
        w = Worker(worker_path(rid))
        try:
            got = read_library(f, "oas", w)
        except Refused as r:
            print(json.dumps({"fixture": name, "refused": r.reply}))
            continue
        finally:
            w.close()
        # canonical paths carry the extension only for "extended"; the expectation states it always
        for c in got["cells"].values():
            for p in c["paths"]:
                if p["ext"] is None:
                    hw = p["width"] // 2
                    p["ext"] = [hw, hw] if p["end"] == "halfwidth" else [0, 0]
        exp = expected_lib(e)
        for c in exp["cells"].values():
            for p in c["paths"]:
                pass
        diffs = canon.diff(exp, got, "expected", "gdstk")
        rec = {"fixture": name, "equal": not diffs, "differences": diffs, "messages": got["messages"],
               "nonintegral": got["nonintegral"], "precision_m": got["precision_m"]}
        if "layer_names" in e:
            have = [[x["name"], x["kind"], x["layer_type"], x["layer_a"], x["layer_b"], x["type_type"], x["type_a"], x["type_b"]]
                    for x in got["layer_names"]]
            rec["layer_names_equal"] = have == e["layer_names"]
            if have != e["layer_names"]:
                rec["layer_names"] = have
        print(json.dumps(rec))


if __name__ == "__main__":
    main()
