"""brief-em3d-72 S3 — slab with a convection (Robin) face. Closed form.
Spike material: run by hand, never by the test suite.

    python make_robin.py     # writes robin.json, robin-S3a.csv, robin-S3b.csv
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

LAT = [1e-3, 1e-3]  # lateral faces insulated
CASES = {
    # S3a: flux q2 into the top (z = L); the bottom (z = 0) convects to T_amb with h.
    #      T(0) = T_amb + q2/h exactly, T(L) = T(0) + q2 L / k.
    "S3a": {"lateral_size_m": LAT, "thickness_L_m": 2e-3, "k_W_mK": 20.0, "flux_top_W_m2": 1e4,
            "bottom": {"h_W_m2K": 1000.0, "T_amb_degC": 25.0}},
    # S3b: uniform volumetric source, BOTH faces convective with different h and ambient.
    "S3b": {"lateral_size_m": LAT, "thickness_L_m": 2e-3, "k_W_mK": 20.0, "q_volume_W_m3": 5e7,
            "bottom": {"h_W_m2K": 2000.0, "T_amb_degC": 25.0}, "top": {"h_W_m2K": 200.0, "T_amb_degC": 40.0}},
}


def solve(c):
    L, k = c["thickness_L_m"], c["k_W_mK"]
    hb, Tb = c["bottom"]["h_W_m2K"], c["bottom"]["T_amb_degC"]
    if "flux_top_W_m2" in c:
        q2 = c["flux_top_W_m2"]
        t0 = Tb + q2 / hb
        return lambda z: t0 + q2 * z / k
    q3 = c["q_volume_W_m3"]
    ht, Tt = c["top"]["h_W_m2K"], c["top"]["T_amb_degC"]
    # T = -q3 z^2/(2k) + a z + b;  k T'(0) = hb (T(0) - Tb);  -k T'(L) = ht (T(L) - Tt)
    # -> k a = hb (b - Tb);  q3 L - k a = ht (-q3 L^2/(2k) + a L + b - Tt)
    A = np.array([[k, -hb], [k + ht * L, ht]])
    rhs = np.array([-hb * Tb, q3 * L + ht * q3 * L * L / (2 * k) + ht * Tt])
    a, b = np.linalg.solve(A, rhs)
    return lambda z: -q3 * z * z / (2 * k) + a * z + b


def main():
    out = {"versions": versions(), "cases": {}}
    for name, c in CASES.items():
        f = solve(c)
        z = np.linspace(0, c["thickness_L_m"], 101)
        t = f(z)
        write_csv(os.path.join(HERE, f"robin-{name}.csv"), ["z [m]", "T [degC]"], zip(z, t))
        L, k = c["thickness_L_m"], c["k_W_mK"]
        res = {"T_bottom_degC": float(t[0]), "T_top_degC": float(t[-1]),
               "T_max_degC": float(np.max(f(np.linspace(0, L, 100001))))}
        # energy balance: what leaves by convection equals what went in
        out_b = c["bottom"]["h_W_m2K"] * (t[0] - c["bottom"]["T_amb_degC"])
        out_t = c["top"]["h_W_m2K"] * (t[-1] - c["top"]["T_amb_degC"]) if "top" in c else 0.0
        into = c.get("flux_top_W_m2", 0.0) + c.get("q_volume_W_m3", 0.0) * L
        res["balance_residual_W_m2"] = float(out_b + out_t - into)
        out["cases"][name] = {**c, **res}
    write_json(os.path.join(HERE, "robin.json"), out)
    print({k: {kk: v for kk, v in c.items() if kk.startswith(("T_", "bal"))} for k, c in out["cases"].items()})


if __name__ == "__main__":
    main()
