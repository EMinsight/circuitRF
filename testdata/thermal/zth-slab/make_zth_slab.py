"""brief-em3d-72 Z1 — the thermal impedance of a slab under a PERIODIC surface flux, the other face held
at a fixed temperature:  Z''_th(jw) = T~(0) / q~'' = tanh(gamma L) / (k gamma),  gamma = sqrt(jw rho c / k).
Closed form, checked against an independent numerical solve (second-order finite differences of the
complex 1D equation on a graded grid). Spike material.

    python make_zth_slab.py   # writes zth-<material>.csv, zth-slab.json
"""
import os
import sys

import numpy as np
from scipy.sparse import diags
from scipy.sparse.linalg import spsolve

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# k: Ho, Powell & Liley (1974) at 300 K; density and specific heat at 25 degC: CRC Handbook.
MATERIALS = {
    "silicon": {"k_W_mK": 148.0, "density_kg_m3": 2329.0, "cp_J_kgK": 705.0, "thickness_m": 100e-6},
    "copper": {"k_W_mK": 401.0, "density_kg_m3": 8960.0, "cp_J_kgK": 385.0, "thickness_m": 1e-3},
}
FREQS = np.logspace(-1, 7, 81)   # Hz


def zth(f, k, rho, c, L):
    if f == 0:
        return complex(L / k)
    g = np.sqrt(1j * 2 * np.pi * f * rho * c / k)
    return complex(np.tanh(g * L) / (k * g))


def zth_fd(f, k, rho, c, L, n=4000):
    """-k T'' + jw rho c T = 0 on [0, L]; -k T'(0) = 1 (unit flux in); T(L) = 0. Grid graded toward x = 0
    so the thermal penetration depth is resolved at the highest frequency. Returns T(0)."""
    x = L * (np.linspace(0, 1, n + 1) ** 3)          # cubic grading, finest at the heated face
    h = np.diff(x)
    jw = 1j * 2 * np.pi * f * rho * c
    N = n  # unknowns T0..T_{n-1}; T_n = 0
    main = np.zeros(N, complex)
    lo = np.zeros(N - 1, complex)
    up = np.zeros(N - 1, complex)
    rhs = np.zeros(N, complex)
    # node 0: half-cell flux balance  k (T0 - T1)/h0 + jw rho c (h0/2) T0 = 1
    main[0] = k / h[0] + jw * h[0] / 2
    up[0] = -k / h[0]
    rhs[0] = 1.0
    for i in range(1, N):
        hl, hr = h[i - 1], h[i]
        main[i] = k / hl + k / hr + jw * (hl + hr) / 2
        lo[i - 1] = -k / hl
        if i < N - 1:
            up[i] = -k / hr
    A = diags([lo, main, up], [-1, 0, 1], format="csc")
    return complex(spsolve(A, rhs)[0])


def main():
    out = {"versions": versions(), "materials": {}}
    for name, mm in MATERIALS.items():
        k, rho, c, L = mm["k_W_mK"], mm["density_kg_m3"], mm["cp_J_kgK"], mm["thickness_m"]
        rows, worst = [], 0.0
        for f in FREQS:
            z = zth(f, k, rho, c, L)
            zf = zth_fd(f, k, rho, c, L)
            worst = max(worst, abs(zf - z) / abs(z))
            rows.append((f, z.real, z.imag, abs(z), np.degrees(np.angle(z))))
        write_csv(os.path.join(HERE, f"zth-{name}.csv"),
                  ["f [Hz]", "Re Z''th [K m^2/W]", "Im Z''th [K m^2/W]", "|Z''th| [K m^2/W]", "arg Z''th [deg]"], rows,
                  comment=f"Z1, {name}: slab {L} m, k {k}, rho {rho}, c {c}; per unit AREA (divide by the heated area for K/W).")
        tau = rho * c * L * L / k
        out["materials"][name] = {**mm, "Zth_dc_K_m2_W": L / k, "diffusion_time_rho_c_L2_over_k_s": tau,
                                  "corner_Hz_1_over_2pi_tau": 1 / (2 * np.pi * tau),
                                  "finite_difference_vs_closed_form_max_relative": worst}
        print(name, "L/k = %.4g K m^2/W, tau = %.4g s, FD vs closed form max rel %.2g" % (L / k, tau, worst))
    write_json(os.path.join(HERE, "zth-slab.json"), out)


if __name__ == "__main__":
    main()
