"""brief-em3d-72 W1b — as W1 with UNEQUAL end temperatures (die pad hotter than the lead), and the
constant-rho limit alpha -> 0 (the parabola). Closed forms, checked against scipy's BVP solver.
Spike material.

    python make_wire_ends.py   # writes <metal>.csv, <metal>-constant-rho.csv, wire-ends.json
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
T_A, T_B = 100.0, 25.0   # s = 0 is the die pad (hot), s = L the lead
FRACTIONS = [0.25, 0.5, 0.9, 0.99]
NPTS = 101
HEADER = ["d [m]", "L [m]", "T_a [degC]", "T_b [degC]", "I/I* [1]", "I [A]", "s [m]", "T [degC]"]


def main():
    out = {"versions": versions(), "T0_degC": W.T0, "T_a_degC": T_A, "T_b_degC": T_B, "metals": {}}
    worst_bvp, worst_limit = 0.0, 0.0
    for metal in METALS:
        m = read_metal(metal)
        rho0, alpha, k = m["rho20_Ohm_m"], m["alpha20_per_K"], m["k20_W_mK"]
        rows, rows_c, prof, prof_c = [], [], [], []
        for dm in DIAMETERS[metal]:
            d = dm * MIL
            A = W.area(d)
            for L in LENGTHS:
                Is = W.i_star(d, L, rho0, alpha, k)
                s = np.linspace(0, L, NPTS)
                for frac in FRACTIONS:
                    I = frac * Is
                    T = W.w1b(s, I, d, L, rho0, alpha, k, T_A, T_B)
                    s_hot, T_hot = W.w1b_hot_spot(I, d, L, rho0, alpha, k, T_A, T_B)
                    rows += [(d, L, T_A, T_B, frac, I, ss, tt) for ss, tt in zip(s, T)]
                    bvp = W.solve_bvp_wire(L, A, lambda t: np.full_like(t, k),
                                           lambda t: I * I * rho0 * (1 + alpha * (t - W.T0)) / A, T_A, T_B)
                    e = float(np.max(np.abs(bvp(s) - T)))
                    worst_bvp = max(worst_bvp, e)
                    prof.append({"d_m": d, "L_m": L, "I_over_Istar": frac, "I_A": I, "I_star_A": Is,
                                 "hot_spot_s_m": s_hot, "hot_spot_s_over_L": s_hot / L, "T_max_degC": T_hot,
                                 "bvp_vs_closed_form_max_abs_K": e})
                    # the constant-rho limit at the SAME current in amperes, rho = rho0
                    Tc = W.parabola(s, I, d, L, rho0, k, T_A, T_B)
                    rows_c += [(d, L, T_A, T_B, frac, I, ss, tt) for ss, tt in zip(s, Tc)]
                    # the sin form tends to it: evaluate at alpha = 1e-7 /K
                    lim = float(np.max(np.abs(W.w1b(s, I, d, L, rho0, 1e-7, k, T_A, T_B) - Tc)))
                    worst_limit = max(worst_limit, lim)
                    q = I * I * rho0 / A  # W/m
                    # hot spot of the parabola: dT/ds = (T_b - T_a)/L + (q/(2kA))(L - 2s) = 0
                    s_c = L / 2 + (T_B - T_A) * k * A / (q * L)
                    s_c = min(max(s_c, 0.0), L)
                    prof_c.append({"d_m": d, "L_m": L, "I_A": I, "hot_spot_s_m": s_c,
                                   "T_max_degC": float(W.parabola(s_c, I, d, L, rho0, k, T_A, T_B)),
                                   "sin_form_at_alpha_1e-7_vs_parabola_max_abs_K": lim})
        write_csv(os.path.join(HERE, f"{metal}.csv"), HEADER, rows,
                  comment=f"W1b, {metal}: linear rho(T), constant k (wire-ends.json); s = 0 is the hotter end.")
        write_csv(os.path.join(HERE, f"{metal}-constant-rho.csv"), HEADER, rows_c,
                  comment=f"W1b constant-rho limit, {metal}: rho = rho0 everywhere, the same currents in amperes.\n"
                          "The I/I* column names the row of <metal>.csv each current came from.")
        out["metals"][metal] = {"parameters": {"rho0_Ohm_m": rho0, "alpha_per_K": alpha, "k_W_mK": k},
                                "profiles": prof, "constant_rho": prof_c}
    out["worst_bvp_vs_closed_form_K"] = worst_bvp
    out["worst_sin_form_limit_vs_parabola_K"] = worst_limit
    write_json(os.path.join(HERE, "wire-ends.json"), out)
    print("worst BVP vs closed form %.3g K; sin-form limit vs parabola %.3g K" % (worst_bvp, worst_limit))
    g = out["metals"]["gold"]["profiles"]
    for e in g[:8]:
        print("gold d=%.1f um L=%g mm I/I*=%.2f hot spot s/L=%.4f Tmax=%.2f" % (e["d_m"] * 1e6, e["L_m"] * 1e3, e["I_over_Istar"], e["hot_spot_s_over_L"], e["T_max_degC"]))


if __name__ == "__main__":
    main()
