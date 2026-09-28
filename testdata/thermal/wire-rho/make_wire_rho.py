"""brief-em3d-72 W1 — a wire between fixed, EQUAL end temperatures, DC current, rho(T) linear, k constant.
Closed form (derived in README.md), checked against scipy's BVP solver. Spike material.

    python make_wire_rho.py    # writes <metal>.csv for the four metals, worked-check.csv, wire-rho.json
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
import wire as W  # noqa: E402
from common import METALS, read_metal, versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
MIL = 25.4e-6
DIAMETERS = {"gold": [1.0, 2.0], "silver": [1.0, 2.0], "copper": [1.0, 1.5, 2.0], "aluminium": [1.0, 1.5, 2.0]}
LENGTHS = [1e-3, 3e-3]
ENDS = [25.0, 100.0]
FRACTIONS = [0.25, 0.5, 0.9, 0.99]
NPTS = 101


def run_config(metal, p, d, L, T_end, rows, summary, numerical=True):
    rho0, alpha, k, Tm = p["rho0"], p["alpha"], p["k"], p["T_melt"]
    Is = W.i_star(d, L, rho0, alpha, k)
    If = W.i_fuse(d, L, rho0, alpha, k, T_end, Tm)
    s = np.linspace(0, L, NPTS)
    for frac in FRACTIONS:
        I = frac * Is
        T = W.w1(s, I, d, L, rho0, alpha, k, T_end)
        rows += [(d, L, T_end, frac, I, ss, tt) for ss, tt in zip(s, T)]
        ent = {"d_m": d, "L_m": L, "T_end_degC": T_end, "I_over_Istar": frac, "I_A": I,
               "T_centre_degC": float(T[NPTS // 2]), "centre_above_melting": bool(T[NPTS // 2] > Tm)}
        if numerical:
            A = W.area(d)
            sol = W.solve_bvp_wire(L, A, lambda t: np.full_like(t, k),
                                   lambda t: I * I * rho0 * (1 + alpha * (t - W.T0)) / A, T_end, T_end)
            ent["bvp_vs_closed_form_max_abs_K"] = float(np.max(np.abs(sol(s) - T)))
        summary.append(ent)
    return Is, If


def main():
    out = {"versions": versions(), "T0_degC": W.T0, "metals": {}}
    for metal in METALS:
        m = read_metal(metal)
        p = {"rho0": m["rho20_Ohm_m"], "alpha": m["alpha20_per_K"], "k": m["k20_W_mK"], "T_melt": m["melting_point_degC"]}
        rows, summary, currents = [], [], []
        for dm in DIAMETERS[metal]:
            for L in LENGTHS:
                for T_end in ENDS:
                    Is, If = run_config(metal, p, dm * MIL, L, T_end, rows, summary)
                    currents.append({"d_m": dm * MIL, "L_m": L, "T_end_degC": T_end, "I_star_A": Is, "I_fuse_A": If})
        write_csv(os.path.join(HERE, f"{metal}.csv"),
                  ["d [m]", "L [m]", "T_end [degC]", "I/I* [1]", "I [A]", "s [m]", "T [degC]"], rows,
                  comment=f"W1, {metal}: rho = rho0 [1 + alpha (T - 20 degC)], k constant; parameters in wire-rho.json.\n"
                          "s is the arc length from one end; the centre is s = L/2.")
        out["metals"][metal] = {"parameters": {"rho0_Ohm_m": p["rho0"], "alpha_per_K": p["alpha"], "k_W_mK": p["k"],
                                               "T_melt_degC": p["T_melt"], "source": "metals/constants.json (rho20, alpha20, k20)"},
                                "currents": currents, "profiles": summary}
    # the brief's worked check: 1 mil gold, L = 1 mm, rho0 2.44e-8, alpha 0.0034, k 312, ends 25 degC
    p = {"rho0": 2.44e-8, "alpha": 0.0034, "k": 312.0, "T_melt": 1064.18}
    rows, summary = [], []
    Is, If = run_config("gold", p, MIL, 1e-3, 25.0, rows, summary)
    write_csv(os.path.join(HERE, "worked-check.csv"),
              ["d [m]", "L [m]", "T_end [degC]", "I/I* [1]", "I [A]", "s [m]", "T [degC]"], rows)
    out["worked_check"] = {"parameters": {"d_m": MIL, "L_m": 1e-3, "rho0_Ohm_m": 2.44e-8, "alpha_per_K": 0.0034,
                                          "k_W_mK": 312.0, "T_end_degC": 25.0, "T_melt_degC": 1064.18},
                           "I_star_A": Is, "I_fuse_A": If, "profiles": summary}
    write_json(os.path.join(HERE, "wire-rho.json"), out)
    print("worked check: I* = %.4f A, I_fuse = %.4f A" % (Is, If))
    worst = max(e["bvp_vs_closed_form_max_abs_K"] for mm in out["metals"].values() for e in mm["profiles"])
    print("worst BVP vs closed form: %.3g K" % worst)


if __name__ == "__main__":
    main()
