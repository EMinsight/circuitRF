"""brief-em3d-72 R-em3d72-4 — rho(T) and k(T) of the four bond-wire metals, from the cited tables.

Spike material: run by hand, never by the test suite. The numbers below are TRANSCRIBED from the
sources named in README.md (recommended values for the pure bulk metal); this script only merges the
two tables onto one temperature column, adds the three rows the brief asks for (20, 85, 125 degC) by
linear interpolation, flags every value as tabulated / interpolated / estimated-by-the-source, and
records the checks of R-em3d72-4.

    python make_metals.py        # writes gold.csv copper.csv aluminium.csv silver.csv constants.json checks.json
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import KELVIN, versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# Electrical resistivity, 1e-8 Ohm m, solid, (T [K], rho). Cu/Au/Ag: Matula 1979; Al: Desai, James & Ho 1984.
# The last row is the solid at the source's own melting temperature.
RHO = {
    "gold": [(250, 1.864), (273.15, 2.051), (293, 2.214), (300, 2.271), (350, 2.685), (400, 3.107),
             (500, 3.974), (600, 4.875), (700, 5.816), (800, 6.808), (900, 7.862), (1000, 8.986),
             (1100, 10.191), (1200, 11.486), (1300, 12.854), (1337.58, 13.388)],
    "copper": [(250, 1.387), (273.15, 1.543), (293, 1.678), (300, 1.725), (350, 2.063), (400, 2.402),
               (500, 3.090), (600, 3.792), (700, 4.514), (800, 5.262), (900, 6.041), (1000, 6.858),
               (1100, 7.717), (1200, 8.626), (1300, 9.592), (1357.6, 10.171)],
    "aluminium": [(250, 2.155), (273, 2.417), (293, 2.650), (300, 2.733), (400, 3.875), (500, 5.020),
                  (600, 6.122), (700, 7.322), (800, 8.614), (900, 10.005), (933.52, 10.565)],
    "silver": [(250, 1.329), (273.15, 1.467), (293, 1.587), (300, 1.629), (350, 1.932), (400, 2.241),
               (500, 2.875), (600, 3.531), (700, 4.209), (800, 4.912), (900, 5.638), (1000, 6.396),
               (1100, 7.215), (1200, 8.089), (1235.08, 8.415)],
}

# Thermal conductivity, W/(m K), solid: Ho, Powell & Liley 1974. A third element marks a value the
# source itself labels "extrapolated or estimated".
K = {
    "gold": [(250, 321), (273.2, 319), (298.2, 318), (300, 317), (350, 314), (400, 311), (500, 304),
             (600, 298), (700, 291), (800, 284), (900, 277), (1000, 270), (1100, 262), (1200, 255),
             (1300, 247, "est"), (1337.58, 244, "est")],
    "copper": [(250, 406), (273.2, 403), (298.2, 401), (300, 401), (350, 396), (400, 393), (500, 386),
               (600, 379), (700, 373), (800, 366), (900, 359), (1000, 352), (1100, 346), (1200, 339),
               (1300, 332), (1357.6, 328)],
    "aluminium": [(250, 235), (273.2, 236), (298.2, 237), (300, 237), (350, 240), (400, 240), (500, 236),
                  (600, 231), (700, 225), (800, 218), (900, 210), (933.52, 208)],
    "silver": [(250, 429), (273.2, 429), (298.2, 429), (300, 429), (350, 427), (400, 425), (500, 419),
               (600, 412), (700, 404), (800, 396), (900, 388), (1000, 379), (1100, 370, "est"),
               (1200, 361, "est"), (1235.08, 358, "est")],
}

# Melting point (ITS-90 fixed point, degC), density at 25 degC (kg/m^3), specific heat at 25 degC
# (J/(kg K)): CRC Handbook of Chemistry and Physics. The tables above end at their SOURCE's melting
# temperature, which predates ITS-90 and differs from these by at most 0.25 K.
CONST = {
    "gold": {"melting_point_degC": 1064.18, "density_kg_m3": 19300, "cp_J_kgK": 129},
    "copper": {"melting_point_degC": 1084.62, "density_kg_m3": 8960, "cp_J_kgK": 385},
    "aluminium": {"melting_point_degC": 660.323, "density_kg_m3": 2700, "cp_J_kgK": 897},
    "silver": {"melting_point_degC": 961.78, "density_kg_m3": 10490, "cp_J_kgK": 235},
}

# What generic-materials.cmat carries today (src/Design/resources/technologies), checked, not changed.
CMAT = {
    "gold": {"Sigma20": 41000000, "Alpha20": 0.0034},
    "copper": {"Sigma20": 58000000, "Alpha20": 0.0039},
    "aluminium": {"Sigma20": 37700000, "Alpha20": 0.0039},
    "silver": {"Sigma20": 63000000, "Alpha20": 0.0038},
}

# The owner's figures for gold's k(T) (R-em3d72-4): (degC, W/(m K)).
OWNER_GOLD_K = [(125.0, 312.0), (927.0, 262.0)]

EXTRA_ROWS_C = (20.0, 85.0, 125.0)


def build(metal):
    rt = np.array([r[0] for r in RHO[metal]]) - KELVIN
    rv = np.array([r[1] for r in RHO[metal]]) * 1e-8
    kt = np.array([r[0] for r in K[metal]]) - KELVIN
    kv = np.array([float(r[1]) for r in K[metal]])
    kest = {round(r[0] - KELVIN, 6) for r in K[metal] if len(r) > 2}
    tmax = min(rt[-1], kt[-1])
    grid = set(round(t, 6) for t in np.concatenate([rt, kt]) if -0.01 <= t <= tmax + 1e-9)
    grid |= set(EXTRA_ROWS_C)
    rows = []
    for t in sorted(grid):
        rho_tab = any(abs(t - x) < 1e-6 for x in rt)
        k_tab = any(abs(t - x) < 1e-6 for x in kt)
        rho = float(np.interp(t, rt, rv))
        k = float(np.interp(t, kt, kv))
        rows.append((t, rho, k, "tabulated" if rho_tab else "interpolated",
                     ("estimated-by-source" if round(t, 6) in kest else "tabulated") if k_tab else "interpolated"))
    return rt, rv, kt, kv, rows


def main():
    constants, checks = {}, {"versions": versions()}
    for metal in ("gold", "copper", "aluminium", "silver"):
        rt, rv, kt, kv, rows = build(metal)
        write_csv(os.path.join(HERE, f"{metal}.csv"),
                  ["T [degC]", "rho [Ohm m]", "k [W/(m K)]", "rho origin", "k origin"], rows,
                  comment=f"{metal}: pure bulk metal, solid, 0 degC to the melting point. Sources and method: README.md.\n"
                          "Interpolated rows are piecewise-linear in T between the two neighbouring tabulated rows.")
        # rho(20) by the table's own linear interpolation; alpha20 from a quadratic through the rows
        # 250..350 K, differentiated at 20 degC (the tangent, not a chord).
        rho20 = float(np.interp(20.0, rt, rv))
        near = rt <= 350 - KELVIN + 1e-6
        c = np.polyfit(rt[near], rv[near], 2)
        drho_dT = float(np.polyval(np.polyder(c), 20.0))
        alpha20 = drho_dT / rho20
        k20 = float(np.interp(20.0, kt, kv))
        use = rt >= 20.0 - 1e-9
        lin = rho20 * (1 + alpha20 * (rt[use] - 20.0))
        dev_tangent = float(np.max(np.abs(rv[use] - lin) / rv[use]))
        at = float(rt[use][np.argmax(np.abs(rv[use] - lin) / rv[use])])
        # the best straight line through all the rows from 20 degC up (least squares), for comparison
        cc = np.polyfit(rt[use], rv[use], 1)
        dev_ls = float(np.max(np.abs(rv[use] - np.polyval(cc, rt[use])) / rv[use]))
        cm = CMAT[metal]
        rho_cmat = 1.0 / cm["Sigma20"]
        constants[metal] = {**CONST[metal], "rho20_Ohm_m": rho20, "alpha20_per_K": alpha20, "k20_W_mK": k20,
                            "table_end_degC": round(float(min(rt[-1], kt[-1])), 6)}
        checks[metal] = {
            "rho20_table_Ohm_m": rho20, "rho20_cmat_Ohm_m": rho_cmat,
            "rho20_cmat_vs_table_percent": 100 * (rho_cmat - rho20) / rho20,
            "alpha20_table_per_K": alpha20, "alpha20_cmat_per_K": cm["Alpha20"],
            "alpha20_cmat_vs_table_percent": 100 * (cm["Alpha20"] - alpha20) / alpha20,
            "drho_dT_table_Ohm_m_per_K": drho_dT, "drho_dT_cmat_Ohm_m_per_K": rho_cmat * cm["Alpha20"],
            "drho_dT_cmat_vs_table_percent": 100 * (rho_cmat * cm["Alpha20"] - drho_dT) / drho_dT,
            "linearity_max_dev_from_20C_tangent_percent": 100 * dev_tangent,
            "linearity_max_dev_at_degC": round(at, 6),
            "linearity_max_dev_from_least_squares_line_percent": 100 * dev_ls,
        }
        if metal == "gold":
            checks[metal]["owner_k"] = [
                {"T_degC": t, "owner_W_mK": v, "table_W_mK": float(np.interp(t, kt, kv)),
                 "table_vs_owner_percent": 100 * (float(np.interp(t, kt, kv)) - v) / v} for t, v in OWNER_GOLD_K]
    write_json(os.path.join(HERE, "constants.json"), constants)
    write_json(os.path.join(HERE, "checks.json"), checks)
    print(json.dumps(checks, indent=1))


if __name__ == "__main__":
    main()
