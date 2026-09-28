"""brief-em3d-72 W3 — as W1 (equal ends, rho(T) linear, k constant) carrying HARMONIC current: the heat
per unit length is sum_n 1/2 |I_n|^2 R'_ac(f_n, T), with R'_ac the real part of the EXACT internal
impedance of a round wire (Bessel functions, scipy.special — independent of circuitRF's own
implementation), evaluated at the local temperature through rho(T). Spike material.

    python make_wire_rf.py   # writes rac-<metal>.csv, <metal>.csv, wire-rf.json
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
import wire as W  # noqa: E402
from common import METALS, read_metal, versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
MIL = 25.4e-6
DIAMETERS = [1.0, 2.0]
L = 1e-3
T_END = 25.0
RAC_TEMPS = [20.0, 85.0, 125.0]
RAC_FREQS = np.logspace(6, 10.5, 46)
# Excitations: harmonic lists of (frequency [Hz], PEAK current as a fraction of the W1 I* of the same
# wire). "dc-0.25" is the check that f = 0 reproduces W1 exactly (the peak IS the RMS at DC).
EXCITATIONS = {
    "dc-0.25": [(0.0, 0.25)],
    "1GHz-rms0.10": [(1e9, 0.10 * np.sqrt(2))],
    "1GHz-rms0.25": [(1e9, 0.25 * np.sqrt(2))],
    "10GHz-rms0.10": [(1e10, 0.10 * np.sqrt(2))],
    "10GHz-rms0.25": [(1e10, 0.25 * np.sqrt(2))],
    # three harmonics of 2 GHz, peaks 0.30, 0.15 and whatever makes the total RMS 0.25 I*; their powers
    # add, phase is irrelevant
    "2GHz-h123": [(2e9, 0.30), (4e9, 0.15), (6e9, float(np.sqrt(2 * 0.25**2 - 0.30**2 - 0.15**2)))],
}
NPTS = 101


def main():
    out = {"versions": versions(), "L_m": L, "T_end_degC": T_END, "metals": {}}
    worst = 0.0
    for metal in METALS:
        m = read_metal(metal)
        rho0, alpha, k = m["rho20_Ohm_m"], m["alpha20_per_K"], m["k20_W_mK"]
        rho = lambda t, rho0=rho0, alpha=alpha: rho0 * (1 + alpha * (t - W.T0))
        rac_rows, rows, ent = [], [], []
        corner = {}
        for dm in DIAMETERS:
            d = dm * MIL
            A = W.area(d)
            corner[repr(d)] = W.skin_corner_hz(d, rho0)
            for T in RAC_TEMPS:
                rdc = rho(T) / A
                for f in RAC_FREQS:
                    rac = W.r_ac_per_m(f, d, rho(T))
                    rac_rows.append((d, T, f, rac, rac / rdc))
            Is = W.i_star(d, L, rho0, alpha, k)
            s = np.linspace(0, L, NPTS)
            for name, harmonics in EXCITATIONS.items():
                Ipk = [(f, frac * Is) for f, frac in harmonics]
                I_rms_total = float(np.sqrt(sum((i if f == 0 else i / np.sqrt(2)) ** 2 for f, i in Ipk)))

                def q(t, Ipk=Ipk, d=d):
                    scalar = np.ndim(t) == 0
                    t = np.atleast_1d(np.asarray(t, float))
                    acc = np.zeros_like(t)
                    for f, i in Ipk:
                        w = 1.0 if f == 0 else 0.5
                        # a trial trajectory of the shooting can dive below T0 - 1/alpha, where the linear
                        # rho goes negative; floor it there (no converged solution comes near it)
                        acc += np.array([w * i * i * W.r_ac_per_m(f, d, max(rho(tt), 1e-3 * rho0)) for tt in t])
                    return acc[0] if scalar else acc

                kf = lambda t: np.full_like(np.asarray(t, float), k)
                Tc = W.solve_shoot_symmetric(L, A, kf, q, T_END, 3e4)
                T = W.profile_symmetric(L, A, kf, q, Tc, s)
                bvp = W.solve_bvp_wire(L, A, kf, q, T_END, T_END)
                e = float(np.max(np.abs(bvp(s) - T)))
                worst = max(worst, e)
                extra = {}
                if name == "dc-0.25":
                    extra["dc_vs_W1_closed_form_max_abs_K"] = float(np.max(np.abs(T - W.w1(s, 0.25 * Is, d, L, rho0, alpha, k, T_END))))
                rows += [(d, name, ss, tt) for ss, tt in zip(s, T)]
                ent.append({"d_m": d, "excitation": name,
                            "harmonics": [{"f_Hz": f, "I_peak_A": i} for f, i in Ipk],
                            "I_rms_total_A": I_rms_total, "I_star_W1_A": Is,
                            "heat_per_m_at_T_end_W_m": float(q(T_END)),
                            "heat_per_m_if_treated_as_DC_at_T_end_W_m": float(I_rms_total ** 2 * rho(T_END) / A),
                            "T_centre_degC": Tc, "centre_above_melting": bool(Tc > m["melting_point_degC"]),
                            "bvp_vs_shooting_max_abs_K": e, **extra})
        write_csv(os.path.join(HERE, f"rac-{metal}.csv"),
                  ["d [m]", "T [degC]", "f [Hz]", "Rac' [Ohm/m]", "Rac'/Rdc' [1]"], rac_rows,
                  comment=f"{metal}: Re of the exact internal impedance of a round wire, rho = rho0 [1 + alpha (T - 20)].")
        write_csv(os.path.join(HERE, f"{metal}.csv"), ["d [m]", "excitation", "s [m]", "T [degC]"], rows,
                  comment=f"W3, {metal}: L = 1 mm, ends 25 degC. Each excitation's harmonics (peak phasors) are in wire-rf.json.")
        out["metals"][metal] = {"parameters": {"rho0_Ohm_m": rho0, "alpha_per_K": alpha, "k_W_mK": k},
                                "skin_corner_Hz_by_diameter_m": corner, "profiles": ent}
        for e in ent:
            print("%-9s d=%.1fum %-14s Irms %.3f A  q'(Tend) %.4g W/m (as DC %.4g)  Tc %.2f  bvp %.2g %s"
                  % (metal, e["d_m"] * 1e6, e["excitation"], e["I_rms_total_A"], e["heat_per_m_at_T_end_W_m"],
                     e["heat_per_m_if_treated_as_DC_at_T_end_W_m"], e["T_centre_degC"], e["bvp_vs_shooting_max_abs_K"],
                     e.get("dc_vs_W1_closed_form_max_abs_K", "")))
    out["worst_bvp_vs_shooting_K"] = worst
    write_json(os.path.join(HERE, "wire-rf.json"), out)


if __name__ == "__main__":
    main()
