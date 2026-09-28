"""brief-em3d-72 Q6 — CG iteration counts on the contrast case under Jacobi, IC(0) and smoothed-aggregation
AMG (the harness, Release), at the three refinements export_contrast.py wrote. SPIKE MATERIAL.

    python run_contrast.py     # writes contrast.csv, contrast.json
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
sys.path.insert(0, os.path.join(HERE, "..", "q5-crossover"))
from common import write_csv  # noqa: E402
from run_crossover import MAT, run  # noqa: E402


def main():
    index = json.load(open(os.path.join(MAT, "index-c6.json")))
    res, rows = [], []
    for e in index:
        path = os.path.join(MAT, e["file"])
        for method, extra in (("jacobi", ()), ("ic0", ()), ("amg", ("0.08",)), ("amg", ("0.0",))):
            r = run(method, path, extra)
            r["label"] = method + (f" theta={extra[0]}" if extra else "")
            res.append(r)
            r["uniform_k"] = bool(e.get("uniform"))
            rows.append((e["n"], "uniform k" if e.get("uniform") else "contrast", r["label"], r.get("iterations"), r.get("converged"), r.get("setup_s"), r.get("solve_s"),
                         r.get("total_s"), r.get("diagonal_shift", ""), r.get("operator_complexity", "")))
            print(e["n"], "uniform" if e.get("uniform") else "contrast", r["label"], r.get("iterations"), r.get("converged"), "%.3f s" % r.get("total_s", -1))
    json.dump(res, open(os.path.join(HERE, "contrast.json"), "w"), indent=1)
    write_csv(os.path.join(HERE, "contrast.csv"),
              ["unknowns [1]", "conductivities", "preconditioner", "CG iterations [1]", "converged", "setup [s]", "solve [s]", "total [s]",
               "IC(0) diagonal shift [1]", "AMG operator complexity [1]"], rows)


if __name__ == "__main__":
    main()
