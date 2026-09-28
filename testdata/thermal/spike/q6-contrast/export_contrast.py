"""brief-em3d-72 Q6 — the conductivity-contrast case: a copper block on FR-4 under a mould-compound cap.
Writes its condensed P2 stiffness matrices at three refinements for the harness (../harness), which counts
CG iterations under Jacobi, IC(0) and smoothed-aggregation AMG; also runs pyamg's own SA solver on the same
matrices as an independent comparison of the AMG counts. SPIKE MATERIAL. Micrometres in the geometry.

    python export_contrast.py     # writes c6-<rung>.bin, c6u-<rung>.bin (uniform k) + index-c6.json to $THERMAL_SPIKE_MATRICES, pyamg.json here
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
sys.path.insert(0, os.path.join(HERE, "..", "q5-crossover"))
import fem  # noqa: E402
from export_matrices import write_bin  # noqa: E402

OUT = os.environ.get("THERMAL_SPIKE_MATRICES", os.path.expanduser("~/opt/thermal-spike/matrices"))
K = {"board": 0.3, "copper": 390.0, "mould": 0.65}   # W/(m K): FR-4 through-plane, copper, mould compound

GEO = """SetFactory("OpenCASCADE");
Box(1) = {-5000, -5000, -1600, 10000, 10000, 1600};   // FR-4 board
Box(2) = {-1500, -1500, 0, 3000, 3000, 1000};         // copper block on the board
Box(3) = {-2500, -2500, 0, 5000, 5000, 2000};         // mould cap, overlapping the block
Rectangle(1001) = {-500, -500, 1000, 1000, 1000};     // 1 W source on the block's top face
BooleanFragments{ Volume{1, 2, 3}; Delete; }{ Surface{1001}; Delete; }
e = 1e-3;
board[] = Volume In BoundingBox{-5000 - e, -5000 - e, -1600 - e, 5000 + e, 5000 + e, e};
cu[] = Volume In BoundingBox{-1500 - e, -1500 - e, -e, 1500 + e, 1500 + e, 1000 + e};
all[] = Volume{:};
mould[] = all[];
mould[] -= board[];
mould[] -= cu[];
src[] = Surface In BoundingBox{-500 - e, -500 - e, 1000 - e, 500 + e, 500 + e, 1000 + e};
bot[] = Surface In BoundingBox{-5000 - e, -5000 - e, -1600 - e, 5000 + e, 5000 + e, -1600 + e};
Physical Volume("board") = {board[]};
Physical Volume("copper") = {cu[]};
Physical Volume("mould") = {mould[]};
Physical Surface("source") = {src[]};
Physical Surface("bottom") = {bot[]};
Mesh.MeshSizeMax = %(hmax)s;
Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0; Mesh.MeshSizeFromCurvature = 0;
Field[1] = Box; Field[1].VIn = %(hin)s; Field[1].VOut = %(hmax)s;
Field[1].XMin = -2600; Field[1].XMax = 2600; Field[1].YMin = -2600; Field[1].YMax = 2600;
Field[1].ZMin = -500; Field[1].ZMax = 2100; Field[1].Thickness = 1500;
Background Field = 1;
"""
RUNGS = [(1200, 300), (800, 200), (500, 130)]


def main():
    import pyamg
    os.makedirs(os.path.join(OUT, "mesh"), exist_ok=True)
    index, py = [], []
    for i, (hmax, hin) in enumerate(RUNGS):
        g, m = os.path.join(OUT, "mesh", f"c6-{i}.geo"), os.path.join(OUT, "mesh", f"c6-{i}.msh")
        fem.run_gmsh(GEO % {"hmax": hmax, "hin": hin}, g, m, threads=1)
        mesh = fem.load(m)
        _, Kmat, F, D, xD = fem.assemble(mesh, 2, K, {"source": 1.0 / 1e-6}, {"bottom": 25.0})
        A, b, _, _ = fem.condensed(Kmat, F[:, 0], D, xD)
        path = os.path.join(OUT, f"c6-{i}.bin")
        write_bin(path, A, b)
        e = {"file": os.path.basename(path), "n": int(A.shape[0]), "nnz": int(A.nnz), "h_max_um": hmax,
             "h_in_um": hin, "tetrahedra": int(mesh.t.shape[1]), "k_W_mK": K}
        index.append(e)
        # the SAME mesh with one conductivity everywhere: isolates what the contrast itself costs
        _, Ku, Fu, Du, xDu = fem.assemble(mesh, 2, {n_: 1.0 for n_ in K}, {"source": 1.0 / 1e-6}, {"bottom": 25.0})
        Au, bu, _, _ = fem.condensed(Ku, Fu[:, 0], Du, xDu)
        write_bin(os.path.join(OUT, f"c6u-{i}.bin"), Au, bu)
        index.append({**e, "file": f"c6u-{i}.bin", "k_W_mK": {n_: 1.0 for n_ in K}, "uniform": True})
        # pyamg's smoothed aggregation on the same matrix, default and theta = 0.08, CG to 1e-8
        for theta in (0.0, 0.08):
            ml = pyamg.smoothed_aggregation_solver(A, symmetry="symmetric", strength=("symmetric", {"theta": theta}))
            res = []
            ml.solve(b, tol=1e-8, accel="cg", residuals=res, maxiter=2000)
            py.append({"file": e["file"], "n": e["n"], "theta": theta, "iterations": len(res) - 1,
                       "levels": len(ml.levels), "operator_complexity": ml.operator_complexity()})
        print(e, py[-2:])
    json.dump(index, open(os.path.join(OUT, "index-c6.json"), "w"), indent=1)
    json.dump({"pyamg": pyamg.__version__, "runs": py}, open(os.path.join(HERE, "pyamg.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
