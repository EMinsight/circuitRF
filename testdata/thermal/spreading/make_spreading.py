"""brief-em3d-72 S5 — a rectangular heat source on a two-layer substrate, bottom held at a fixed
temperature, every other face insulated. Two independent references:

  1. the Fourier series of a layered rectangular flux channel (tools/spreading.py; README derives it
     and cites the published series it is the h -> infinity case of), and
  2. an independent 3D FEM (scikit-fem P2 tetrahedra on Gmsh meshes, CG + pyamg), refined until the
     reported temperatures move by less than 0.1 %, on a quarter model (two mirror planes, insulated).

Spike material: run by hand, never by the test suite. Needs `gmsh` on PATH (called as a program; not
committed, GPL) and the Python packages in ../README.md.

    python make_spreading.py            # writes spreading.json, surface-line.csv, ladder.csv, mesh/ (git-ignored scratch)
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
CASE = {
    "lateral_a_m": 2e-3, "lateral_b_m": 2e-3,
    "layers_top_down": [{"thickness_m": 100e-6, "k_W_mK": 150.0}, {"thickness_m": 1e-3, "k_W_mK": 390.0}],
    "source_centre_m": [1e-3, 1e-3], "source_size_m": [200e-6, 100e-6],
    "power_W": 1.0, "T_bottom_degC": 25.0,
}
SERIES_M = [2000, 4000, 8000]
# (source-near size, far size, grading distance) in micrometres, finest last
LADDER = [(40, 250, 600), (20, 200, 500), (10, 160, 400), (5, 130, 330), (2.5, 110, 280), (1.25, 80, 220), (0.8, 60, 180)]


def geo(hs, hmax, dist, order_hint=""):
    a, b = CASE["lateral_a_m"] / UM, CASE["lateral_b_m"] / UM
    t1, t2 = (L["thickness_m"] / UM for L in CASE["layers_top_down"])
    cx, cy = (c / UM for c in CASE["source_centre_m"])
    sx, sy = (s / UM / 2 for s in CASE["source_size_m"])
    x0, y0 = cx, cy                       # the quarter: x >= cx, y >= cy (both mirror planes insulated)
    wx, wy = a - cx, b - cy
    e = 1e-3
    return f"""// S5 quarter model, micrometres. Written by make_spreading.py. {order_hint}
SetFactory("OpenCASCADE");
Box(1) = {{{x0}, {y0}, {-t1}, {wx}, {wy}, {t1}}};
Box(2) = {{{x0}, {y0}, {-t1 - t2}, {wx}, {wy}, {t2}}};
Rectangle(100) = {{{x0}, {y0}, 0, {sx}, {sy}}};
BooleanFragments{{ Volume{{1, 2}}; Delete; }}{{ Surface{{100}}; Delete; }}
src[] = Surface In BoundingBox{{{x0 - e}, {y0 - e}, {-e}, {x0 + sx + e}, {y0 + sy + e}, {e}}};
bot[] = Surface In BoundingBox{{{x0 - e}, {y0 - e}, {-t1 - t2 - e}, {x0 + wx + e}, {y0 + wy + e}, {-t1 - t2 + e}}};
v1[] = Volume In BoundingBox{{{x0 - e}, {y0 - e}, {-t1 - e}, {x0 + wx + e}, {y0 + wy + e}, {e}}};
v2[] = Volume In BoundingBox{{{x0 - e}, {y0 - e}, {-t1 - t2 - e}, {x0 + wx + e}, {y0 + wy + e}, {-t1 + e}}};
Physical Volume("layer1") = {{v1[]}};
Physical Volume("layer2") = {{v2[]}};
Physical Surface("source") = {{src[]}};
Physical Surface("bottom") = {{bot[]}};
Mesh.MeshSizeMax = {hmax};
Mesh.MeshSizeFromPoints = 0;
Mesh.MeshSizeExtendFromBoundary = 0;
Mesh.MeshSizeFromCurvature = 0;
Field[1] = Distance; Field[1].SurfacesList = {{src[]}}; Field[1].Sampling = 60;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = {hs}; Field[2].SizeMax = {hmax};
Field[2].DistMin = {hs}; Field[2].DistMax = {dist};
Background Field = 2;
"""


def series():
    a, b = CASE["lateral_a_m"], CASE["lateral_b_m"]
    layers = [(L["thickness_m"], L["k_W_mK"]) for L in CASE["layers_top_down"]]
    cx, cy = CASE["source_centre_m"]
    sx, sy = CASE["source_size_m"]
    rect = (cx - sx / 2, cx + sx / 2, cy - sy / 2, cy + sy / 2)
    out = []
    for M in SERIES_M:
        ch = S.Channel(a, b, layers, M, M)
        c = ch.modes(rect, CASE["power_W"])
        out.append({"M": M, "centre_rise_K": ch.point(c, cx, cy), "mean_rise_K": ch.mean(c, rect)})
        if M == SERIES_M[-1]:
            xs = np.linspace(cx, a, 201)
            line = ch.line_x(c, xs, cy)
    # the mean converges like 1/M: one Richardson step on the last two
    m1, m2 = out[-2]["mean_rise_K"], out[-1]["mean_rise_K"]
    return out, 2 * m2 - m1, xs, line


def fem_run(order, hs, hmax, dist, tag):
    os.makedirs(SCRATCH, exist_ok=True)
    g, m = os.path.join(SCRATCH, f"s5-{tag}.geo"), os.path.join(SCRATCH, f"s5-{tag}.msh")
    info = fem.run_gmsh(geo(hs, hmax, dist), g, m, threads=1)
    mesh = fem.load(m)
    sx, sy = CASE["source_size_m"]
    q = CASE["power_W"] / (sx * sy)
    ks = {f"layer{i + 1}": L["k_W_mK"] for i, L in enumerate(CASE["layers_top_down"])}
    basis, K, F, D, xD = fem.assemble(mesh, order, ks, {"source": q}, {"bottom": 0.0})
    u, sinfo = fem.solve_amg(K, F[:, 0], D, xD)
    cx, cy = CASE["source_centre_m"]
    centre = float((basis.probes(np.array([[cx], [cy], [0.0]])) @ u)[0])
    mean = fem.facet_mean(mesh, order, u, "source")
    return {"order": order, "h_source_um": hs, "h_max_um": hmax, "grading_distance_um": dist,
            "vertices": mesh.p.shape[1], "tetrahedra": mesh.t.shape[1], "dofs": basis.N,
            "unknowns": sinfo["unknowns"], "cg_amg_iterations": sinfo["iterations"],
            "centre_rise_K": centre, "mean_rise_K": mean}, (mesh, basis, u)


def main():
    ser, mean_rich, xs, line = series()
    print("series:", ser, "mean (Richardson) %.8f" % mean_rich)
    ladder = []
    for i, (hs, hmax, dist) in enumerate(LADDER):
        r, _ = fem_run(2, hs, hmax, dist, f"p2-{i}")
        ladder.append(r)
        print("P2 h=%g/%g: %d dofs, centre %.6f mean %.6f (%d it)" % (hs, hmax, r["dofs"], r["centre_rise_K"],
                                                                      r["mean_rise_K"], r["cg_amg_iterations"]))
    sc, sm = ser[-1]["centre_rise_K"], mean_rich
    for i, r in enumerate(ladder):
        r["centre_vs_series_percent"] = 100 * (r["centre_rise_K"] - sc) / sc
        r["mean_vs_series_percent"] = 100 * (r["mean_rise_K"] - sm) / sm
        if i:
            p = ladder[i - 1]
            r["centre_change_from_previous_percent"] = 100 * (r["centre_rise_K"] - p["centre_rise_K"]) / r["centre_rise_K"]
            r["mean_change_from_previous_percent"] = 100 * (r["mean_rise_K"] - p["mean_rise_K"]) / r["mean_rise_K"]
    write_csv(os.path.join(HERE, "ladder.csv"),
              ["h_source [um]", "h_max [um]", "P2 dofs [1]", "centre rise [K]", "mean rise [K]",
               "centre vs series [%]", "mean vs series [%]"],
              [(r["h_source_um"], r["h_max_um"], r["dofs"], r["centre_rise_K"], r["mean_rise_K"],
                r["centre_vs_series_percent"], r["mean_vs_series_percent"]) for r in ladder])
    write_csv(os.path.join(HERE, "surface-line.csv"), ["x [m]", "T [degC]"],
              zip(xs, CASE["T_bottom_degC"] + line),
              comment="Top face, along y = the source's centre line, from the source centre to the edge (series, M = N = 8000).")
    one_d = sum(L["thickness_m"] / L["k_W_mK"] for L in CASE["layers_top_down"]) / (CASE["lateral_a_m"] * CASE["lateral_b_m"])
    write_json(os.path.join(HERE, "spreading.json"), {
        "versions": {**versions(), "gmsh": fem.gmsh_version()}, "case": CASE,
        "reference": {"T_centre_degC": CASE["T_bottom_degC"] + sc, "T_mean_source_degC": CASE["T_bottom_degC"] + sm,
                      "Rth_centre_K_W": sc / CASE["power_W"], "Rth_mean_K_W": sm / CASE["power_W"],
                      "R_one_dimensional_K_W": one_d, "R_spreading_mean_K_W": sm / CASE["power_W"] - one_d},
        "series": ser, "series_mean_richardson_K": mean_rich, "fem_ladder": ladder})


if __name__ == "__main__":
    main()
