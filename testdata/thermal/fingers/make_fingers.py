"""brief-em3d-72 S6 — eight parallel source strips (a multi-finger device) on a two-layer substrate, bottom
fixed, every other face insulated: mutual heating, as the 8 x 8 thermal-resistance matrix
R_ij = (mean temperature rise over finger i) / (power in finger j).

The reference is an independent 3D FEM (scikit-fem P2 tetrahedra on Gmsh meshes, CG + pyamg) refined
until R moves by less than 0.1 %, on a half model (the plane through the fingers' mid-length is a mirror;
exciting one finger keeps that symmetry). The layered-channel Fourier series (tools/spreading.py) is
computed alongside as an independent check. Spike material: run by hand; needs `gmsh` on PATH.

    python make_fingers.py      # writes fingers.json, rth-matrix.csv, all-on-line.csv, ladder.csv, mesh/ (scratch)
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
import fem  # noqa: E402
import spreading as S  # noqa: E402
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
SCRATCH = os.path.join(HERE, "mesh")
UM = 1e-6
NF = 8
CASE = {
    "lateral_a_m": 1e-3, "lateral_b_m": 1e-3,
    "layers_top_down": [{"thickness_m": 100e-6, "k_W_mK": 370.0}, {"thickness_m": 500e-6, "k_W_mK": 200.0}],
    "fingers": NF, "finger_width_m": 10e-6, "finger_length_m": 200e-6, "pitch_m": 40e-6,
    "array_centre_m": [0.5e-3, 0.5e-3], "T_bottom_degC": 25.0,
    "all_on_power_per_finger_W": 0.25,
}
SERIES_M = [(4000, 1000), (8000, 2000), (16000, 4000)]
LADDER = [(8, 120, 250), (4, 100, 220), (2, 80, 180), (1.2, 60, 150), (0.7, 50, 130)]


def finger_rects():
    cx, cy = CASE["array_centre_m"]
    w, L, p = CASE["finger_width_m"], CASE["finger_length_m"], CASE["pitch_m"]
    return [(cx + (i - (NF - 1) / 2) * p - w / 2, cx + (i - (NF - 1) / 2) * p + w / 2, cy - L / 2, cy + L / 2)
            for i in range(NF)]


def geo(hs, hmax, dist):
    a, b = CASE["lateral_a_m"] / UM, CASE["lateral_b_m"] / UM
    t1, t2 = (L["thickness_m"] / UM for L in CASE["layers_top_down"])
    cy = CASE["array_centre_m"][1] / UM
    e = 1e-3
    lines = ['// S6 half model (y >= the fingers\' mid-length), micrometres. Written by make_fingers.py.',
             'SetFactory("OpenCASCADE");',
             f"Box(1) = {{0, {cy}, {-t1}, {a}, {b - cy}, {t1}}};",
             f"Box(2) = {{0, {cy}, {-t1 - t2}, {a}, {b - cy}, {t2}}};"]
    rects = finger_rects()
    for i, (x0, x1, y0, y1) in enumerate(rects):
        lines.append(f"Rectangle({100 + i}) = {{{x0 / UM}, {cy}, 0, {(x1 - x0) / UM}, {(y1 - y0) / UM / 2}}};")
    lines.append(f"BooleanFragments{{ Volume{{1, 2}}; Delete; }}{{ Surface{{{', '.join(str(100 + i) for i in range(NF))}}}; Delete; }}")
    for i, (x0, x1, y0, y1) in enumerate(rects):
        lines.append(f"f{i}[] = Surface In BoundingBox{{{x0 / UM - e}, {cy - e}, {-e}, {x1 / UM + e}, {y1 / UM + e}, {e}}};")
        lines.append(f'Physical Surface("f{i}") = {{f{i}[]}};')
    lines += [f"bot[] = Surface In BoundingBox{{{-e}, {cy - e}, {-t1 - t2 - e}, {a + e}, {b + e}, {-t1 - t2 + e}}};",
              f"v1[] = Volume In BoundingBox{{{-e}, {cy - e}, {-t1 - e}, {a + e}, {b + e}, {e}}};",
              f"v2[] = Volume In BoundingBox{{{-e}, {cy - e}, {-t1 - t2 - e}, {a + e}, {b + e}, {-t1 + e}}};",
              'Physical Volume("layer1") = {v1[]};', 'Physical Volume("layer2") = {v2[]};',
              'Physical Surface("bottom") = {bot[]};',
              f"Mesh.MeshSizeMax = {hmax};", "Mesh.MeshSizeFromPoints = 0;", "Mesh.MeshSizeExtendFromBoundary = 0;",
              "Mesh.MeshSizeFromCurvature = 0;",
              f"Field[1] = Distance; Field[1].SurfacesList = {{{', '.join(f'f{i}[]' for i in range(NF))}}}; Field[1].Sampling = 80;",
              f"Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = {hs}; Field[2].SizeMax = {hmax};",
              f"Field[2].DistMin = {hs}; Field[2].DistMax = {dist};", "Background Field = 2;"]
    return "\n".join(lines) + "\n"


def series(M, N):
    a, b = CASE["lateral_a_m"], CASE["lateral_b_m"]
    layers = [(L["thickness_m"], L["k_W_mK"]) for L in CASE["layers_top_down"]]
    ch = S.Channel(a, b, layers, M, N)
    rects = finger_rects()
    R = np.zeros((NF, NF))
    total = None   # every finger at 1 W, summed (kept for the all-on line; one array, not eight)
    for j, rj in enumerate(rects):
        c = ch.modes(rj, 1.0)
        total = c if total is None else total + c
        for i, ri in enumerate(rects):
            R[i, j] = ch.mean(c, ri)
        del c
    return ch, R, total


def fem_run(hs, hmax, dist, tag):
    os.makedirs(SCRATCH, exist_ok=True)
    g, m = os.path.join(SCRATCH, f"s6-{tag}.geo"), os.path.join(SCRATCH, f"s6-{tag}.msh")
    fem.run_gmsh(geo(hs, hmax, dist), g, m, threads=1)
    mesh = fem.load(m)
    w, L = CASE["finger_width_m"], CASE["finger_length_m"]
    q = 1.0 / (w * L)   # 1 W in the WHOLE finger; the half model carries half of it at the same flux
    ks = {f"layer{i + 1}": Ly["k_W_mK"] for i, Ly in enumerate(CASE["layers_top_down"])}
    basis, K, F, D, xD = fem.assemble(mesh, 2, ks, {f"f{i}": q for i in range(NF)}, {"bottom": 0.0})
    U, info = fem.solve_amg_multi(K, F, D, xD)
    R = np.array([[fem.facet_mean(mesh, 2, U[:, j], f"f{i}") for j in range(NF)] for i in range(NF)])
    return R, {"h_source_um": hs, "h_max_um": hmax, "grading_distance_um": dist, "vertices": mesh.p.shape[1],
               "tetrahedra": mesh.t.shape[1], "dofs": int(basis.N), "cg_amg_iterations": info["iterations"]}


def main():
    ser = []
    for M, N in SERIES_M:
        ch, Rs, total = series(M, N)
        ser.append({"M": M, "N": N, "R_K_W": Rs})
        print("series M=%d N=%d  R11 %.6f R12 %.6f R18 %.6f R44 %.6f" % (M, N, Rs[0, 0], Rs[0, 1], Rs[0, 7], Rs[3, 3]))
    R_series = 2 * ser[-1]["R_K_W"] - ser[-2]["R_K_W"]   # the means converge like 1/M: one Richardson step
    # all fingers on: the temperature across the array along the mid-length line (series, finest)
    cx, cy = CASE["array_centre_m"]
    P = CASE["all_on_power_per_finger_W"]
    xs = np.linspace(cx - 250e-6, cx + 250e-6, 1001)
    line = P * ch.line_x(total, xs, cy)
    write_csv(os.path.join(HERE, "all-on-line.csv"), ["x [m]", "T [degC]"], zip(xs, CASE["T_bottom_degC"] + line),
              comment=f"All {NF} fingers at {P} W each; top face along the fingers' mid-length line (series, M = {SERIES_M[-1][0]}, N = {SERIES_M[-1][1]}).")
    ladder, prev = [], None
    for i, (hs, hmax, dist) in enumerate(LADDER):
        R, info = fem_run(hs, hmax, dist, str(i))
        info["R_K_W"] = R
        info["max_change_from_previous_percent"] = None if prev is None else float(np.max(np.abs(R - prev) / np.abs(R)) * 100)
        info["max_vs_series_percent"] = float(np.max(np.abs(R - R_series) / np.abs(R_series)) * 100)
        ladder.append(info)
        prev = R
        print("FEM h=%g/%g %d dofs: R11 %.6f R12 %.6f R18 %.6f  change %s %%  vs series %.4f %%"
              % (hs, hmax, info["dofs"], R[0, 0], R[0, 1], R[0, 7], info["max_change_from_previous_percent"],
                 info["max_vs_series_percent"]))
    R_fem = ladder[-1]["R_K_W"]
    write_csv(os.path.join(HERE, "rth-matrix.csv"), ["i \\ j"] + [f"R_i{j + 1} [K/W]" for j in range(NF)],
              [[f"finger {i + 1}"] + list(R_fem[i]) for i in range(NF)],
              comment="Mean temperature rise over finger i per watt in finger j; the finest FEM rung (fingers.json has the ladder and the series).")
    write_csv(os.path.join(HERE, "ladder.csv"), ["h_source [um]", "h_max [um]", "P2 dofs [1]", "R11 [K/W]", "R12 [K/W]",
                                                 "R18 [K/W]", "R44 [K/W]", "max change from previous [%]", "max vs series [%]"],
              [(r["h_source_um"], r["h_max_um"], r["dofs"], r["R_K_W"][0, 0], r["R_K_W"][0, 1], r["R_K_W"][0, 7],
                r["R_K_W"][3, 3], "" if r["max_change_from_previous_percent"] is None else r["max_change_from_previous_percent"],
                r["max_vs_series_percent"]) for r in ladder])
    write_json(os.path.join(HERE, "fingers.json"), {
        "versions": {**versions(), "gmsh": fem.gmsh_version()}, "case": CASE,
        "finger_rects_m": finger_rects(),
        "R_fem_K_W": R_fem, "R_series_K_W": R_series, "series": ser, "fem_ladder": ladder,
        "all_on_mean_rise_K": list(R_fem @ np.full(NF, P))})


if __name__ == "__main__":
    main()
