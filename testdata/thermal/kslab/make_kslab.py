"""brief-em3d-72 S4 — slab with temperature-dependent conductivity.

  S4a  k = k0 / (1 + beta (T - T0)): the Kirchhoff transform gives the closed form.
  S4b  k(T) a TABLE (silicon, piecewise-linear in T): a BVP solve, cross-checked against the
       Kirchhoff transform of the same piecewise-linear table (exact up to root-finding).

Spike material: run by hand, never by the test suite.

    python make_kslab.py     # writes kslab.json, kslab-S4a.csv, kslab-S4b.csv, silicon-k.csv
"""
import os
import sys

import numpy as np
from scipy.integrate import solve_bvp
from scipy.optimize import brentq

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import KELVIN, versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# Silicon, W/(m K): Ho, Powell & Liley, J. Phys. Chem. Ref. Data 3 Suppl. 1 (1974). (T [K], k)
SILICON = [(273.2, 168), (298.2, 149), (300, 148), (350, 119), (400, 98.9), (500, 76.2), (600, 61.9),
           (700, 50.8), (800, 42.2), (900, 35.9), (1000, 31.2)]

GEOM = {"lateral_size_m": [1e-3, 1e-3], "T_bottom_degC": 25.0}  # z = 0 fixed; lateral faces insulated
CASES = {
    "S4a": {**GEOM, "thickness_L_m": 500e-6, "flux_top_W_m2": 4e7,
            "k": {"form": "k0/(1+beta*(T-T0))", "k0_W_mK": 150.0, "T0_degC": 25.0, "beta_per_K": 0.004}},
    "S4b": {**GEOM, "thickness_L_m": 500e-6, "flux_top_W_m2": 4e7,
            "k": {"form": "table", "table": "silicon-k.csv", "interpolation": "piecewise-linear in T, clamped"}},
}

TK = np.array([t for t, _ in SILICON]) - KELVIN
KV = np.array([float(k) for _, k in SILICON])


def k_table(T):
    return np.interp(T, TK, KV)


def s4a(z, c):
    kk = c["k"]
    k0, T0, b = kk["k0_W_mK"], kk["T0_degC"], kk["beta_per_K"]
    # U(T) = int_T0^T k dT = (k0/b) ln(1 + b (T - T0));  U'' = 0, U(0) = U(Tb), U' = q2  (flux in at z = L)
    Ub = (k0 / b) * np.log(1 + b * (c["T_bottom_degC"] - T0))
    U = Ub + c["flux_top_W_m2"] * z
    return T0 + (np.exp(b * U / k0) - 1) / b


def kirchhoff_table(z, c):
    """U(T) = int_Tb^T k(T') dT' of the piecewise-linear table, exact (trapezoids), inverted by brentq."""
    fine = np.union1d(TK, [c["T_bottom_degC"]])
    fine = fine[fine >= c["T_bottom_degC"]]

    def U(T):
        xs = np.append(fine[fine < T], T)
        ks = k_table(xs)
        return float(np.sum(0.5 * (ks[1:] + ks[:-1]) * np.diff(xs)))

    return np.array([brentq(lambda T: U(T) - c["flux_top_W_m2"] * zz, c["T_bottom_degC"], TK[-1], xtol=1e-13)
                     if zz > 0 else c["T_bottom_degC"] for zz in z])


def bvp_table(z, c):
    L, q2 = c["thickness_L_m"], c["flux_top_W_m2"]
    # y = [T, flux k T'];  T' = flux/k(T), flux' = 0;  T(0) = Tb, flux(L) = q2
    f = lambda x, y: np.vstack([y[1] / k_table(y[0]), np.zeros_like(y[1])])
    bc = lambda ya, yb: np.array([ya[0] - c["T_bottom_degC"], yb[1] - q2])
    x = np.linspace(0, L, 201)
    y0 = np.vstack([c["T_bottom_degC"] + q2 * x / 100.0, np.full_like(x, q2)])
    sol = solve_bvp(f, bc, x, y0, tol=1e-8, bc_tol=1e-9, max_nodes=1000000)
    assert sol.success, sol.message
    return sol.sol(z)[0]


def main():
    write_csv(os.path.join(HERE, "silicon-k.csv"), ["T [degC]", "k [W/(m K)]"], zip(TK, KV),
              comment="Silicon, Ho, Powell & Liley, J. Phys. Chem. Ref. Data 3 Suppl. 1 (1974). Interpolate linearly in T.")
    out = {"versions": versions(), "cases": {}}
    c = CASES["S4a"]
    z = np.linspace(0, c["thickness_L_m"], 101)
    t = s4a(z, c)
    write_csv(os.path.join(HERE, "kslab-S4a.csv"), ["z [m]", "T [degC]"], zip(z, t))
    out["cases"]["S4a"] = {**c, "T_top_degC": float(t[-1]),
                           "T_top_if_k_constant_k0_degC": c["T_bottom_degC"] + c["flux_top_W_m2"] * c["thickness_L_m"] / c["k"]["k0_W_mK"]}
    c = CASES["S4b"]
    t_bvp = bvp_table(z, c)
    t_kir = kirchhoff_table(z, c)
    write_csv(os.path.join(HERE, "kslab-S4b.csv"), ["z [m]", "T [degC]"], zip(z, t_bvp))
    out["cases"]["S4b"] = {**c, "T_top_degC": float(t_bvp[-1]),
                           "bvp_vs_kirchhoff_max_abs_K": float(np.max(np.abs(t_bvp - t_kir)))}
    write_json(os.path.join(HERE, "kslab.json"), out)
    print({k: {kk: v for kk, v in cc.items() if kk.startswith(("T_top", "bvp"))} for k, cc in out["cases"].items()})


if __name__ == "__main__":
    main()
