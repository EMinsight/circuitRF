"""brief-em3d-72 S1 — 1D slab: fixed T on one face, uniform flux into the other (and a uniform
volumetric source). Closed form. Spike material: run by hand, never by the test suite.

    python make_slab.py      # writes slab.json, slab-S1a.csv, slab-S1b.csv
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# A box, lateral faces insulated, so the 3D field is exactly this 1D field (z up).
CASE = {
    "lateral_size_m": [1e-3, 1e-3],
    "thickness_L_m": 1e-3,
    "k_W_mK": 150.0,
    "T_bottom_degC": 25.0,            # z = 0, fixed temperature
    "flux_top_W_m2": 1e6,             # z = L, uniform flux INTO the slab
}
CASES = {
    "S1a": {**CASE, "q_volume_W_m3": 0.0},
    "S1b": {**CASE, "q_volume_W_m3": 1e9},
}


def T(z, c):
    L, k, q2, q3 = c["thickness_L_m"], c["k_W_mK"], c["flux_top_W_m2"], c["q_volume_W_m3"]
    # -k T'' = q3, T(0) = Tb, k T'(L) = q2  ->  T = Tb + (q2 + q3 L) z / k - q3 z^2 / (2k)
    return c["T_bottom_degC"] + (q2 + q3 * L) * z / k - q3 * z * z / (2 * k)


def main():
    out = {"versions": versions(), "cases": {}}
    for name, c in CASES.items():
        z = np.linspace(0, c["thickness_L_m"], 101)
        t = T(z, c)
        write_csv(os.path.join(HERE, f"slab-{name}.csv"), ["z [m]", "T [degC]"], zip(z, t))
        out["cases"][name] = {**c, "T_top_degC": float(t[-1]),
                              "total_power_W": (c["flux_top_W_m2"] + c["q_volume_W_m3"] * c["thickness_L_m"])
                                               * c["lateral_size_m"][0] * c["lateral_size_m"][1]}
    write_json(os.path.join(HERE, "slab.json"), out)
    print(out)


if __name__ == "__main__":
    main()
