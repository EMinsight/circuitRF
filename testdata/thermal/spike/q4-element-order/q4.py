"""brief-em3d-72 Q4 — P1 against P2 tetrahedra at EQUAL UNKNOWN COUNTS on S5 (../../spreading): the
error in the peak (source-centre) temperature and in the source-mean temperature, against the series.
SPIKE MATERIAL: run by hand; nothing in src/ or tests/ may use it. Needs gmsh on PATH.

    python q4.py     # writes q4.csv, q4.json
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
sys.path.insert(0, os.path.join(HERE, "..", "..", "spreading"))
import make_spreading as S5  # noqa: E402
from common import versions, write_csv, write_json  # noqa: E402
import json  # noqa: E402

S5.SCRATCH = os.path.join(HERE, "mesh")
ref = json.load(open(os.path.join(HERE, "..", "..", "spreading", "spreading.json")))["reference"]
TC, TM = ref["Rth_centre_K_W"], ref["Rth_mean_K_W"]

# The same graded-mesh family for both orders; P1 simply runs further down it, so that its unknown counts
# span P2's. (h_source, h_max, grading distance) in micrometres.
P2_LADDER = [(20, 200, 500), (10, 160, 400), (5, 130, 330), (2.5, 110, 280), (1.25, 80, 220), (0.8, 60, 180)]
P1_LADDER = [(10, 100, 400), (5, 80, 330), (2.5, 65, 280), (1.25, 50, 220), (0.8, 40, 180), (0.5, 32, 150), (0.35, 26, 120)]


def main():
    rows, res = [], []
    for order, ladder in ((2, P2_LADDER), (1, P1_LADDER)):
        for i, (hs, hm, d) in enumerate(ladder):
            r, _ = S5.fem_run(order, hs, hm, d, f"q4-p{order}-{i}")
            ec = 100 * (r["centre_rise_K"] - TC) / TC
            em = 100 * (r["mean_rise_K"] - TM) / TM
            res.append({**r, "centre_error_percent": ec, "mean_error_percent": em})
            rows.append((order, hs, hm, int(r["unknowns"]), r["centre_rise_K"], ec, r["mean_rise_K"], em))
            print("P%d h=%g/%g unknowns %8d  centre err %+.4f %%  mean err %+.4f %%" % (order, hs, hm, r["unknowns"], ec, em))
    write_csv(os.path.join(HERE, "q4.csv"),
              ["order [1]", "h_source [um]", "h_max [um]", "unknowns [1]", "centre rise [K]", "centre error [%]",
               "mean rise [K]", "mean error [%]"], rows)
    # the error each order reaches at a given unknown count, by log-log interpolation along its ladder
    cmp = []
    for n in (1e4, 3e4, 1e5, 2e5):
        row = {"unknowns": n}
        for order in (1, 2):
            pts = sorted((r["unknowns"], abs(r["centre_error_percent"]), abs(r["mean_error_percent"]))
                         for r in res if r["order"] == order)
            u = np.log([p[0] for p in pts])
            if np.log(n) < u[0] or np.log(n) > u[-1]:
                continue
            row[f"P{order}_centre_error_percent"] = float(np.exp(np.interp(np.log(n), u, np.log([p[1] for p in pts]))))
            row[f"P{order}_mean_error_percent"] = float(np.exp(np.interp(np.log(n), u, np.log([p[2] for p in pts]))))
        cmp.append(row)
        print(row)
    write_json(os.path.join(HERE, "q4.json"), {"versions": versions(), "reference": ref, "runs": res, "at_equal_unknowns": cmp})


if __name__ == "__main__":
    main()
