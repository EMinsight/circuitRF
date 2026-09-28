"""brief-em3d-72 W4 — a wire on the axis of a coaxial cylinder of mould compound whose OUTER surface is
held at T_amb: lateral loss g' (T - T_amb) per unit length, g' = 2 pi k_mould / ln(r_outer / r_wire).
Linear rho(T), constant k. Closed form (cosh/cos; sinh/sin for unequal ends), checked against scipy's
BVP solver. Spike material.

    python make_wire_mould.py   # writes <metal>.csv, wire-mould.json
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
K_MOULD = 0.8                    # W/(m K): a representative epoxy mould compound (typically 0.6-1)
R_OUTER = [100e-6, 500e-6]       # m
# (T_a, T_b, T_amb): equal ends with the mould's outer surface at the same temperature, and a die pad
# hotter than the lead with the mould at the lead's temperature.
ENDS = [(25.0, 25.0, 25.0), (100.0, 100.0, 100.0), (100.0, 25.0, 25.0)]
FRACTIONS = [0.25, 0.5, 0.9, 0.99]   # of the W4 runaway current I*_mould
NPTS = 101


def main():
    out = {"versions": versions(), "k_mould_W_mK": K_MOULD, "metals": {}}
    worst = 0.0
    for metal in METALS:
        m = read_metal(metal)
        rho0, alpha, k = m["rho20_Ohm_m"], m["alpha20_per_K"], m["k20_W_mK"]
        rows, ent = [], []
        for dm in DIAMETERS[metal]:
            d = dm * MIL
            A = W.area(d)
            for ro in R_OUTER:
                g = W.g_coax(K_MOULD, d / 2, ro)
                for L in LENGTHS:
                    Is = W.i_star_mould(d, L, rho0, alpha, k, g)
                    Is_free = W.i_star(d, L, rho0, alpha, k)
                    s = np.linspace(0, L, NPTS)
                    for Ta, Tb, Tamb in ENDS:
                        for frac in FRACTIONS:
                            I = frac * Is
                            T, m2, Tp = W.w4(s, I, d, L, rho0, alpha, k, g, Tamb, Ta, Tb)
                            q = lambda t, I=I: I * I * rho0 * (1 + alpha * (t - W.T0)) / A - g * (t - Tamb)
                            bvp = W.solve_bvp_wire(L, A, lambda t: np.full_like(t, k), q, Ta, Tb)
                            e = float(np.max(np.abs(bvp(s) - T)))
                            worst = max(worst, e)
                            rows += [(d, ro, L, Ta, Tb, Tamb, frac, I, ss, tt) for ss, tt in zip(s, T)]
                            ent.append({"d_m": d, "r_outer_m": ro, "g_W_mK": g, "L_m": L, "T_a_degC": Ta, "T_b_degC": Tb,
                                        "T_amb_degC": Tamb, "I_over_Istar_mould": frac, "I_A": I,
                                        "I_star_mould_A": Is, "I_star_no_mould_A": Is_free,
                                        "m2_per_m2": m2, "form": "cosh/sinh" if m2 > 0 else "cos/sin",
                                        "T_particular_degC": Tp, "T_max_degC": float(np.max(T)),
                                        "bvp_vs_closed_form_max_abs_K": e})
        write_csv(os.path.join(HERE, f"{metal}.csv"),
                  ["d [m]", "r_outer [m]", "L [m]", "T_a [degC]", "T_b [degC]", "T_amb [degC]", "I/I*_mould [1]",
                   "I [A]", "s [m]", "T [degC]"], rows,
                  comment=f"W4, {metal}: linear rho(T), constant k, mould k = {K_MOULD} W/(m K); g' in wire-mould.json.")
        out["metals"][metal] = {"parameters": {"rho0_Ohm_m": rho0, "alpha_per_K": alpha, "k_W_mK": k}, "cases": ent}
        e = [x for x in ent if x["L_m"] == 1e-3 and x["T_a_degC"] == 25.0 and x["I_over_Istar_mould"] == 0.25]
        for x in e:
            print("%-9s d=%.1fum ro=%gum g'=%.3f W/mK  I*_mould %.3f A vs %.3f A free (x%.2f)"
                  % (metal, x["d_m"] * 1e6, x["r_outer_m"] * 1e6, x["g_W_mK"], x["I_star_mould_A"], x["I_star_no_mould_A"],
                     x["I_star_mould_A"] / x["I_star_no_mould_A"]))
    out["worst_bvp_vs_closed_form_K"] = worst
    write_json(os.path.join(HERE, "wire-mould.json"), out)
    print("worst BVP vs closed form %.3g K" % worst)
    forms = {x["form"] for mm in out["metals"].values() for x in mm["cases"]}
    print("forms exercised:", forms)


if __name__ == "__main__":
    main()
