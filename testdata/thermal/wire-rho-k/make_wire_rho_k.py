"""brief-em3d-72 W2 — as W1 (equal ends, DC) with the metal's rho(T) AND k(T) TABLES (metals/), both
interpolated piecewise-linearly in T. No closed form: shooting from the centre (the reference) checked
against scipy's collocation BVP solver. Spike material.

    python make_wire_rho_k.py   # writes <metal>.csv, wire-rho-k.json
"""
import os
import sys

import numpy as np
from scipy.optimize import brentq, minimize_scalar

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
import wire as W  # noqa: E402
from common import METALS, interp, read_metal, versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
MIL = 25.4e-6
DIAMETERS = {"gold": [1.0, 2.0], "silver": [1.0, 2.0], "copper": [1.0, 1.5, 2.0], "aluminium": [1.0, 1.5, 2.0]}
LENGTHS = [1e-3, 3e-3]
ENDS = [25.0, 100.0]
FRACTIONS = [0.25, 0.5, 0.9, 0.99]   # of THIS case's steady-state limit I_limit (below)
NPTS = 101


def end_temp(L, A, kf, qf, Tc):
    return W.shoot_centre(L, A, kf, qf, Tc)


def current_for_centre(L, A, kf, rhof, T_end, Tc, I_hi):
    """The current whose symmetric solution has centre Tc and ends T_end (the end temperature falls
    monotonically as I rises at fixed Tc)."""
    g = lambda I: end_temp(L, A, kf, lambda t: I * I * rhof(t) / A, Tc) - T_end
    return brentq(g, 1e-9, I_hi, xtol=1e-14, rtol=1e-13)


def main():
    out = {"versions": versions(), "interpolation": "piecewise-linear in T, clamped at the table ends", "metals": {}}
    worst = 0.0
    for metal in METALS:
        m = read_metal(metal)
        Tm = m["melting_point_degC"]
        rhof = lambda t, m=m: interp(m["T"], m["rho"], t)
        kf = lambda t, m=m: interp(m["T"], m["k"], t)
        k20 = m["k20_W_mK"]
        rows, entries = [], []
        for dm in DIAMETERS[metal]:
            d = dm * MIL
            A = W.area(d)
            for L in LENGTHS:
                for T_end in ENDS:
                    I_lin_star = W.i_star(d, L, m["rho20_Ohm_m"], m["alpha20_per_K"], k20)
                    I_lin_fuse = W.i_fuse(d, L, m["rho20_Ohm_m"], m["alpha20_per_K"], k20, T_end, Tm)
                    I_hi = 2 * I_lin_star
                    I_fuse = current_for_centre(L, A, kf, rhof, T_end, Tm, I_hi)
                    I_fuse_k_const = current_for_centre(L, A, lambda t: np.full_like(np.asarray(t, float), k20),
                                                        rhof, T_end, Tm, I_hi)
                    # I(Tc): the current whose steady state has centre Tc. For linear rho it rises to I* as
                    # Tc -> infinity; with the tables it can PEAK below the melting point (a fold): above
                    # the peak there is no steady state at all, and the centre-melt current then lies on
                    # the unstable upper branch. I_limit is the largest current with a steady state whose
                    # centre is at or below the melting point: the peak if there is one, else I(Tm).
                    tcs = np.linspace(T_end + 1.0, Tm, 25)
                    ic = np.array([current_for_centre(L, A, kf, rhof, T_end, tc, I_hi) for tc in tcs])
                    j = int(np.argmax(ic))
                    if j == len(tcs) - 1:
                        I_limit, Tc_limit, kind = I_fuse, Tm, "melt"
                    else:
                        r = minimize_scalar(lambda tc: -current_for_centre(L, A, kf, rhof, T_end, tc, I_hi),
                                            bounds=(tcs[max(j - 1, 0)], tcs[j + 1]), method="bounded",
                                            options={"xatol": 1e-3})
                        I_limit, Tc_limit, kind = -r.fun, r.x, "fold"
                    s = np.linspace(0, L, NPTS)
                    profs = []
                    for frac in FRACTIONS:
                        I = frac * I_limit
                        qf = lambda t, I=I: I * I * rhof(t) / A
                        Tc = W.solve_shoot_symmetric(L, A, kf, qf, T_end, Tm + 1.0)
                        T = W.profile_symmetric(L, A, kf, qf, Tc, s)
                        bvp = W.solve_bvp_wire(L, A, kf, qf, T_end, T_end)
                        e = float(np.max(np.abs(bvp(s) - T)))
                        worst = max(worst, e)
                        rows += [(d, L, T_end, frac, I, ss, tt) for ss, tt in zip(s, T)]
                        profs.append({"I_over_Ilimit": frac, "I_A": I, "T_centre_degC": Tc, "bvp_vs_shooting_max_abs_K": e})
                    entries.append({"d_m": d, "L_m": L, "T_end_degC": T_end,
                                    "I_fuse_tables_A": I_fuse, "I_fuse_rho_table_k_constant_A": I_fuse_k_const,
                                    "I_fuse_W1_linear_model_A": I_lin_fuse, "I_star_W1_linear_model_A": I_lin_star,
                                    "W1_fuse_over_tables_fuse": I_lin_fuse / I_fuse,
                                    "I_limit_A": I_limit, "limit_kind": kind, "T_centre_at_limit_degC": Tc_limit,
                                    "I_of_Tcentre": {"T_centre_degC": tcs, "I_A": ic}, "profiles": profs})
        write_csv(os.path.join(HERE, f"{metal}.csv"),
                  ["d [m]", "L [m]", "T_end [degC]", "I/I_limit [1]", "I [A]", "s [m]", "T [degC]"], rows,
                  comment=f"W2, {metal}: rho(T) and k(T) from metals/{metal}.csv, piecewise-linear; equal ends.\n"
                          "I_limit is this row's own steady-state limit (wire-rho-k.json): a fold below melting, or the centre-melt current.")
        out["metals"][metal] = {"T_melt_degC": Tm, "table": f"metals/{metal}.csv", "cases": entries}
        e0 = entries[0]
        for e0 in entries:
            print("%-9s d=%.1fum L=%gmm Tend=%g: I_limit %.4f A (%s, Tc %.1f), centre-melt %.4f A, k const %.4f A, W1 linear %.4f A"
                  % (metal, e0["d_m"] * 1e6, e0["L_m"] * 1e3, e0["T_end_degC"], e0["I_limit_A"], e0["limit_kind"],
                     e0["T_centre_at_limit_degC"], e0["I_fuse_tables_A"], e0["I_fuse_rho_table_k_constant_A"],
                     e0["I_fuse_W1_linear_model_A"]))
    out["worst_bvp_vs_shooting_K"] = worst
    write_json(os.path.join(HERE, "wire-rho-k.json"), out)
    print("worst BVP vs shooting %.3g K" % worst)


if __name__ == "__main__":
    main()
