"""brief-em3d-72 Z2 — a Foster network of known terms driven by a periodic rectangular pulse train, in
periodic steady state. The closed form of the peak (at the end of each pulse)

    dT_peak = P sum_i R_i (1 - exp(-t_on/tau_i)) / (1 - exp(-T/tau_i))

and of the whole waveform, against the Fourier-series sum dT(t) = P_0 Z(0) + 2 Re sum_{n>=1} P_n Z(j n w0) e^{j n w0 t}
truncated at N harmonics — the route brief 80 takes (Z_th(jw) per harmonic, no transient solver). The
truncation table is the evidence for how many harmonics that route needs. Spike material.

    python make_pulse.py   # writes pulse-<case>.csv, truncation-<case>.csv, pulse.json
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

CASES = {
    # three poles spanning the pulse: die (1 us), attach/flange (100 us), heatsink (10 ms); 10 % duty
    "Z2a": {"P_W": 10.0, "period_s": 1e-3, "t_on_s": 100e-6,
            "foster": [{"R_K_W": 0.5, "tau_s": 1e-6}, {"R_K_W": 1.0, "tau_s": 1e-4}, {"R_K_W": 2.0, "tau_s": 1e-2}]},
    # a radar-like 1 % duty, 10 us pulse every 1 ms, four poles
    "Z2b": {"P_W": 100.0, "period_s": 1e-3, "t_on_s": 10e-6,
            "foster": [{"R_K_W": 0.2, "tau_s": 3e-7}, {"R_K_W": 0.4, "tau_s": 5e-6},
                       {"R_K_W": 0.8, "tau_s": 2e-4}, {"R_K_W": 1.5, "tau_s": 5e-2}]},
}
TRUNCATIONS = [10, 30, 100, 300, 1000, 3000, 10000, 30000, 100000, 300000, 1000000]


def closed_form(t, c):
    """Periodic steady state, 0 <= t < T, pulse on for 0 <= t < t_on."""
    P, T, ton = c["P_W"], c["period_s"], c["t_on_s"]
    out = np.zeros_like(t)
    for term in c["foster"]:
        R, tau = term["R_K_W"], term["tau_s"]
        tmin = P * R * (1 - np.exp(-ton / tau)) * np.exp(-(T - ton) / tau) / (1 - np.exp(-T / tau))
        tmax = P * R * (1 - np.exp(-ton / tau)) / (1 - np.exp(-T / tau))
        on = t < ton
        out += np.where(on, P * R * (1 - np.exp(-t / tau)) + tmin * np.exp(-t / tau),
                        tmax * np.exp(-(t - ton) / tau))
    return out


def peak(c):
    P, T, ton = c["P_W"], c["period_s"], c["t_on_s"]
    return P * sum(f["R_K_W"] * (1 - np.exp(-ton / f["tau_s"])) / (1 - np.exp(-T / f["tau_s"])) for f in c["foster"])


def fourier(t, c, N):
    P, T, ton = c["P_W"], c["period_s"], c["t_on_s"]
    w0 = 2 * np.pi / T
    n = np.arange(1, N + 1)
    Pn = P * (1 - np.exp(-1j * n * w0 * ton)) / (1j * n * w0 * T)
    Z = sum(f["R_K_W"] / (1 + 1j * n * w0 * f["tau_s"]) for f in c["foster"])
    coef = Pn * Z
    dc = P * ton / T * sum(f["R_K_W"] for f in c["foster"])
    t = np.atleast_1d(t)
    out = np.empty(len(t))
    for i, tt in enumerate(t):
        out[i] = dc + 2 * np.sum((coef * np.exp(1j * n * w0 * tt)).real)
    return out


def fourier_tail_corrected(t, c, N):
    """The same sum with the high-frequency asymptote Z_inf(jw) = sum R_i/(jw tau_i) = 1/(jw C) taken
    out of every harmonic and added back EXACTLY in the time domain: 1/(jw C) acting on the zero-mean
    part of the pulse train is its running integral over C, a piecewise-linear sawtooth with zero mean.
    The remaining coefficients fall like 1/n^3 instead of 1/n^2."""
    P, T, ton = c["P_W"], c["period_s"], c["t_on_s"]
    w0 = 2 * np.pi / T
    n = np.arange(1, N + 1)
    Pn = P * (1 - np.exp(-1j * n * w0 * ton)) / (1j * n * w0 * T)
    inv_c = sum(f["R_K_W"] / f["tau_s"] for f in c["foster"])
    Z = sum(f["R_K_W"] / (1 + 1j * n * w0 * f["tau_s"]) for f in c["foster"]) - inv_c / (1j * n * w0)
    coef = Pn * Z
    dc = P * ton / T * sum(f["R_K_W"] for f in c["foster"])
    Pm = P * ton / T

    def saw(tt):
        return inv_c * ((P - Pm) * tt if tt < ton else (P - Pm) * ton - Pm * (tt - ton))

    # the sawtooth's mean over one period, exactly (two linear pieces)
    mean = inv_c * ((P - Pm) * ton * ton / 2 + (P - Pm) * ton * (T - ton) - Pm * (T - ton) ** 2 / 2) / T
    t = np.atleast_1d(t)
    return np.array([dc + 2 * np.sum((coef * np.exp(1j * n * w0 * tt)).real) + saw(tt) - mean for tt in t])


def slab_case():
    """Z2c: the pulse train on the SLAB of zth-slab/ (silicon, 100 um, heated area 1 mm^2). A slab with a
    fixed back face is an INFINITE Foster network: tanh(gL)/(k g) = sum_m R_m/(1 + jw tau_m) with
    lambda_m = (m + 1/2) pi / L, R_m = 2/(k L lambda_m^2), tau_m = rho c/(k lambda_m^2) (per unit area).
    The closed-form peak sums M terms and adds the tail, whose terms all have tau << t_on (ratio -> 1):
    sum_{m>=M} R_m = (2 L/(k pi^2)) psi'(M + 1/2)."""
    from scipy.special import polygamma
    k, rho, cp, L, area = 148.0, 2329.0, 705.0, 100e-6, 1e-6
    P, T, ton = 1.0, 1e-3, 100e-6
    M = 200000
    m = np.arange(M)
    lam = (m + 0.5) * np.pi / L
    R = 2 / (k * L * lam ** 2) / area
    tau = rho * cp / (k * lam ** 2)
    pk = P * np.sum(R * (1 - np.exp(-ton / tau)) / (1 - np.exp(-T / tau)))
    tail = P * (2 * L / (k * np.pi ** 2)) * float(polygamma(1, M + 0.5)) / area
    pk += tail
    w0 = 2 * np.pi / T
    trunc = []
    for N in TRUNCATIONS:
        n = np.arange(1, N + 1)
        Pn = P * (1 - np.exp(-1j * n * w0 * ton)) / (1j * n * w0 * T)
        g = np.sqrt(1j * n * w0 * rho * cp / k)
        Z = np.tanh(g * L) / (k * g) / area
        v = P * ton / T * (L / k) / area + 2 * np.sum((Pn * Z * np.exp(1j * n * w0 * ton)).real)
        trunc.append((N, v, v - pk, (v - pk) / pk))
    return {"slab": {"material": "silicon (zth-slab/)", "k_W_mK": k, "density_kg_m3": rho, "cp_J_kgK": cp,
                     "thickness_m": L, "heated_area_m2": area},
            "P_W": P, "period_s": T, "t_on_s": ton, "foster_terms_summed": M, "foster_tail_added_K": tail,
            "dT_peak_closed_form_K": pk}, trunc


def main():
    out = {"versions": versions(), "cases": {}}
    for name, c in CASES.items():
        T, ton = c["period_s"], c["t_on_s"]
        t = np.concatenate([np.linspace(0, ton, 401, endpoint=False), np.linspace(ton, T, 1601, endpoint=False)])
        wave = closed_form(t, c)
        pk = peak(c)
        # the peak is at the end of the pulse (every term rises during it): the waveform's own limit there
        at_ton = float(closed_form(np.array([ton - 1e-15]), c)[0])
        f_wave = fourier(t[::20], c, 100000)
        write_csv(os.path.join(HERE, f"pulse-{name}.csv"), ["t [s]", "dT [K]"], zip(t, wave),
                  comment=f"Z2 {name}: periodic steady state over one period; pulse on for 0 <= t < {ton} s. Temperature RISE.")
        trunc = []
        for N in TRUNCATIONS:
            v = float(fourier(np.array([ton]), c, N)[0])
            vt = float(fourier_tail_corrected(np.array([ton]), c, N)[0])
            trunc.append((N, v, v - pk, (v - pk) / pk, vt, (vt - pk) / pk))
        write_csv(os.path.join(HERE, f"truncation-{name}.csv"),
                  ["harmonics N [1]", "dT(t_on) Fourier [K]", "error [K]", "relative error [1]",
                   "dT(t_on) tail-corrected [K]", "tail-corrected relative error [1]"], trunc)
        out["cases"][name] = {**c, "dT_peak_closed_form_K": pk, "dT_at_t_on_from_waveform_K": at_ton,
                              "dT_mean_K": c["P_W"] * ton / T * sum(f["R_K_W"] for f in c["foster"]),
                              "dT_min_K": float(wave[0]),
                              "fourier_N100000_vs_closed_form_max_abs_K": float(np.max(np.abs(f_wave - wave[::20]))),
                              "truncation": [{"N": n, "relative_error": r, "tail_corrected_relative_error": rt}
                                             for n, _, _, r, _, rt in trunc]}
        print(name, "peak %.6f K (waveform %.6f), mean %.4f, min %.4f" % (pk, at_ton, out["cases"][name]["dT_mean_K"], wave[0]))
        for n, v, e, r, vt, rt in trunc:
            print("   N=%7d  %.6f K  rel err %.2e   tail-corrected %.2e" % (n, v, r, rt))
    info, trunc = slab_case()
    write_csv(os.path.join(HERE, "truncation-Z2c.csv"),
              ["harmonics N [1]", "dT(t_on) Fourier [K]", "error [K]", "relative error [1]"], trunc)
    info["truncation"] = [{"N": n, "relative_error": r} for n, _, _, r in trunc]
    out["cases"]["Z2c"] = info
    print("Z2c slab peak %.6f K (tail %.3g K)" % (info["dT_peak_closed_form_K"], info["foster_tail_added_K"]))
    for n, v, e, r in trunc:
        print("   N=%7d  %.6f K  rel err %.2e" % (n, v, r))
    write_json(os.path.join(HERE, "pulse.json"), out)


if __name__ == "__main__":
    main()
