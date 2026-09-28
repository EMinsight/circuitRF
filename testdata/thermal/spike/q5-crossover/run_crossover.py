"""brief-em3d-72 Q5 — drive the harness (../harness, Release) over the exported S5 matrices: CSparse
Cholesky against CG + smoothed-aggregation AMG, each run in its own process under /usr/bin/time -l so its
peak resident set is its own. SPIKE MATERIAL: a measurement for brief 74's default, never a test.

    dotnet build -c Release ../harness
    python run_crossover.py            # writes crossover.csv, crossover.json
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
from common import write_csv, write_json  # noqa: E402

HARNESS = os.path.join(HERE, "..", "harness", "bin", "Release", "net10.0", "ThermalSpikeHarness")
MAT = os.environ.get("THERMAL_SPIKE_MATRICES", os.path.expanduser("~/opt/thermal-spike/matrices"))
CHOLESKY_STOP_S = 600        # stop factoring once one factorization takes longer than this
CHOLESKY_STOP_MB = 9000      # ... or its peak resident set passes this (16 GB machine)
# ... or the NEXT one is projected past either: time grew like n^2.2 and the factor like n^1.5 over the
# last rungs measured (373k -> 643k: 152 s -> 509 s, 3.0 GB -> 6.8 GB), which put 997k at ~22 min, ~13 GB.
GROWTH_T, GROWTH_M = 2.2, 1.5


def run(method, path, extra=()):
    r = subprocess.run(["/usr/bin/time", "-l", HARNESS, method, path, *extra], capture_output=True, text=True)
    if r.returncode != 0:
        return {"method": method, "file": os.path.basename(path), "error": r.stderr[-800:]}
    out = json.loads(r.stdout.strip().splitlines()[-1])
    m = re.search(r"(\d+)\s+maximum resident set size", r.stderr)
    if m:
        out["peak_rss_MB"] = int(m.group(1)) / 1048576.0
    return out


def main():
    index = json.load(open(os.path.join(MAT, "index-s5.json")))
    rows, results = [], []
    # resume: a matrix already measured (crossover.json) is not run again
    done = {}
    if os.path.exists(os.path.join(HERE, "crossover.json")):
        for r in json.load(open(os.path.join(HERE, "crossover.json"))):
            done[(r["method"], r["file"])] = r
    chol_on, last = True, None
    for e in sorted(index, key=lambda e: e["n"]):
        path = os.path.join(MAT, e["file"])
        if chol_on and last is not None:
            k = (e["n"] / last["n"]) ** GROWTH_T
            km = (e["n"] / last["n"]) ** GROWTH_M
            # projected on the FACTOR's size, not the peak resident set (which below ~200k is dominated by
            # everything else and does not grow like the factor)
            if last["total_s"] * k > CHOLESKY_STOP_S or last["L_MB"] * km > CHOLESKY_STOP_MB:
                chol_on = False
                print("Cholesky not run from n = %d: projected %.0f s, factor %.0f MB" % (e["n"], last["total_s"] * k,
                                                                                         last["L_MB"] * km))
        a = done.get(("amg", e["file"])) or run("amg", path)
        c = (done.get(("cholesky", e["file"])) or run("cholesky", path)) if chol_on else None
        if c and "total_s" in c:
            last = c
        results += [x for x in (a, c) if x]
        print(e["file"], e["n"], "AMG %.3fs (%s it, %.0f MB)" % (a["total_s"], a["iterations"], a.get("peak_rss_MB", -1)),
              "| Cholesky " + ("%.3fs (%.0f MB, L %.0f MB)" % (c["total_s"], c.get("peak_rss_MB", -1), c["L_MB"]) if c and "total_s" in c else str(c)))
        rows.append((e["n"], e["nnz"], a["setup_s"], a["solve_s"], a["total_s"], a["iterations"], a.get("peak_rss_MB", ""),
                     c["factor_s"] if c else "", c["solve_s"] if c else "", c["total_s"] if c else "",
                     c["L_MB"] if c else "", c.get("peak_rss_MB", "") if c else ""))
        if c and (c.get("total_s", 1e9) > CHOLESKY_STOP_S or c.get("peak_rss_MB", 0) > CHOLESKY_STOP_MB):
            chol_on = False
        write_csv(os.path.join(HERE, "crossover.csv"),
                  ["unknowns [1]", "nnz [1]", "AMG setup [s]", "AMG solve [s]", "AMG total [s]", "CG iterations [1]",
                   "AMG peak RSS [MB]", "Cholesky factor [s]", "Cholesky solve [s]", "Cholesky total [s]",
                   "L factor [MB]", "Cholesky peak RSS [MB]"], rows)
        write_json(os.path.join(HERE, "crossover.json"), results)


if __name__ == "__main__":
    main()
