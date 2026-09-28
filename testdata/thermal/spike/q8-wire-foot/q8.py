"""brief-em3d-72 Q8 — the wire foot: a 50 um x 25 um contact patch embedded in the top face of a
100 um x 100 um pad (10 um thick, on a 400 um x 400 um x 100 um substrate). What minimum element size does
the foot force, and what does it cost in nodes? SPIKE MATERIAL. Micrometres.

    python q8.py      # writes q8.json, q8.csv; meshes go to mesh/ (git-ignored)
"""
import json
import os
import subprocess
import sys
import time

import meshio
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
from common import write_csv  # noqa: E402

GEO = """SetFactory("OpenCASCADE");
Box(1) = {-200, -200, -100, 400, 400, 100};   // substrate
Box(2) = {-50, -50, 0, 100, 100, 10};          // pad
Rectangle(1001) = {%(fx)s, %(fy)s, 10, 50, 25}; // the foot, 50 um along the wire x 25 um across
BooleanFragments{ Volume{1, 2}; Delete; }{ Surface{1001}; Delete; }
e = 1e-4;
foot[] = Surface In BoundingBox{%(fx)s - e, %(fy)s - e, 10 - e, %(fx)s + 50 + e, %(fy)s + 25 + e, 10 + e};
Physical Surface("foot") = {foot[]};
Physical Volume("solids") = {Volume{:}};
Mesh.MeshSizeMax = %(hmax)s;
Mesh.MeshSizeFromCurvature = 0;
%(field)s
"""
FIELD = """Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0;
Field[1] = Distance; Field[1].SurfacesList = {foot[]}; Field[1].Sampling = 60;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = %(hfoot)s; Field[2].SizeMax = %(hmax)s;
Field[2].DistMin = %(hfoot)s; Field[2].DistMax = 150;
Background Field = 2;"""

# (name, foot corner x, y, far size, field size at the foot or None)
VARIANTS = [
    ("centred-no-field", -25, -12.5, 100, None),
    ("near-edge-no-field", -25, 20, 100, None),        # the foot's far side 5 um inside the pad's edge
    ("centred-4-across", -25, -12.5, 100, 6.25),       # four elements across the foot's width
    ("near-edge-4-across", -25, 20, 100, 6.25),
    ("centred-8-across", -25, -12.5, 100, 3.125),
]


def edge_lengths(tets, p):
    c = tets[:, :4]
    pairs = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)]
    return np.concatenate([np.linalg.norm(p[c[:, a]] - p[c[:, b]], axis=1) for a, b in pairs])


def tet_quality(tets, p):
    """Mean-ratio quality, 12 (3V)^(2/3) / sum(l^2): 1 for a regular tetrahedron, -> 0 for a sliver."""
    c = tets[:, :4]
    a, b, cc, d = (p[c[:, i]] for i in range(4))
    V = np.abs(np.einsum("ij,ij->i", b - a, np.cross(cc - a, d - a))) / 6
    l2 = sum(np.sum((p[c[:, i]] - p[c[:, j]]) ** 2, axis=1) for i, j in ((0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)))
    return 12 * (3 * V) ** (2 / 3) / l2


def run(name, fx, fy, hmax, hfoot):
    os.makedirs(os.path.join(HERE, "mesh"), exist_ok=True)
    field = FIELD % {"hfoot": hfoot, "hmax": hmax} if hfoot else ""
    g = os.path.join(HERE, "mesh", f"{name}.geo")
    m = os.path.join(HERE, "mesh", f"{name}.msh")
    open(g, "w").write(GEO % {"fx": fx, "fy": fy, "hmax": hmax, "field": field})
    t = time.time()
    r = subprocess.run(["gmsh", g, "-3", "-order", "2", "-nt", "1", "-format", "msh22", "-o", m],
                       capture_output=True, text=True)
    out = {"variant": name, "foot_corner_um": [fx, fy], "h_far_um": hmax, "h_foot_um": hfoot,
           "exit": r.returncode, "seconds": time.time() - t}
    if r.returncode != 0:
        out["error"] = (r.stdout + r.stderr)[-600:]
        return out
    mm = meshio.read(m)
    tets = mm.cells_dict["tetra10"]
    tri = mm.cells_dict["triangle6"]
    el = edge_lengths(tets, mm.points)
    # edges of the triangles ON the foot
    tp = mm.points
    fe = np.concatenate([np.linalg.norm(tp[tri[:, a]] - tp[tri[:, b]], axis=1) for a, b in ((0, 1), (1, 2), (0, 2))])
    out.update({"tetrahedra": int(len(tets)), "corner_vertices": int(len(np.unique(tets[:, :4]))),
                "p2_nodes": int(len(np.unique(tets))), "foot_triangles": int(len(tri)),
                "min_tet_edge_um": float(el.min()), "median_tet_edge_um": float(np.median(el)),
                "foot_min_edge_um": float(fe.min()), "foot_max_edge_um": float(fe.max()),
                "min_tet_quality": float(tet_quality(tets, mm.points).min()),
                "tets_below_quality_0.2": int(np.sum(tet_quality(tets, mm.points) < 0.2))})
    return out


def main():
    res = [run(*v) for v in VARIANTS]
    for r in res:
        print(r)
    json.dump(res, open(os.path.join(HERE, "q8.json"), "w"), indent=1)
    write_csv(os.path.join(HERE, "q8.csv"),
              ["variant", "h foot [um]", "tetrahedra", "P2 nodes", "foot triangles", "min tet edge [um]",
               "foot edge min [um]", "foot edge max [um]", "min tet quality [1]", "tets with quality < 0.2"],
              [(r["variant"], r["h_foot_um"] or "", r.get("tetrahedra"), r.get("p2_nodes"), r.get("foot_triangles"),
                r.get("min_tet_edge_um"), r.get("foot_min_edge_um"), r.get("foot_max_edge_um"), r.get("min_tet_quality"),
                r.get("tets_below_quality_0.2")) for r in res])


if __name__ == "__main__":
    main()
