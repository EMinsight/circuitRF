"""brief-em3d-72 Q7 — the mesh-size span of scenario 2: a 0.5 um source strip on a 100 um die on a 10 mm
flange. Can Gmsh grade from 0.5 um to millimetres with the EXISTING Distance/Threshold fields plus a Box
field (a mesh region), and what does it cost in elements and nodes? SPIKE MATERIAL: writes .geo files,
runs the gmsh executable, reads its statistics. Micrometres.

    python q7.py      # writes q7.json, q7.csv; meshes go to mesh/ (git-ignored)
"""
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
from common import write_csv  # noqa: E402

GEOM = """SetFactory("OpenCASCADE");
Box(1) = {-5000, -5000, -1000, 10000, 10000, 1000};   // flange, 10 mm x 10 mm x 1 mm
Box(2) = {-500, -250, 0, 1000, 500, 100};             // die, 1 mm x 0.5 mm x 100 um
Rectangle(1001) = {-0.25, -100, 100, 0.5, 200};       // the source strip: 0.5 um x 200 um on the die's top
BooleanFragments{ Volume{1, 2}; Delete; }{ Surface{1001}; Delete; }
e = 1e-4;
strip[] = Surface In BoundingBox{-0.25 - e, -100 - e, 100 - e, 0.25 + e, 100 + e, 100 + e};
Physical Surface("strip") = {strip[]};
Physical Volume("solids") = {Volume{:}};
Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0; Mesh.MeshSizeFromCurvature = 0;
Mesh.MeshSizeMax = %(hmax)s;
Field[1] = Distance; Field[1].SurfacesList = {strip[]}; Field[1].Sampling = 400;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = %(hmin)s; Field[2].SizeMax = %(hmax)s;
Field[2].DistMin = %(dmin)s; Field[2].DistMax = %(dmax)s;
%(box)s
Background Field = %(bg)s;
Mesh.Algorithm3D = %(alg3d)s;
"""

BOX = """Field[3] = Box; Field[3].VIn = %(hbox)s; Field[3].VOut = %(hmax)s;
Field[3].XMin = -600; Field[3].XMax = 600; Field[3].YMin = -350; Field[3].YMax = 350; Field[3].ZMin = -100; Field[3].ZMax = 100;
Field[3].Thickness = %(boxgrade)s;
Field[4] = Min; Field[4].FieldsList = {2, 3};"""

# (name, h at the strip, h far, Threshold DistMin, DistMax, Box field inner size or None, 3D algorithm)
VARIANTS = [
    ("threshold-only", 0.25, 1000, 0.25, 3300, None, 1),
    ("threshold+box", 0.25, 1000, 0.25, 3300, 20, 1),
    ("threshold+box-hxt", 0.25, 1000, 0.25, 3300, 20, 10),
    ("threshold-steep+box", 0.25, 1000, 0.25, 1000, 20, 1),
]


def run(name, hmin, hmax, dmin, dmax, hbox, alg):
    os.makedirs(os.path.join(HERE, "mesh"), exist_ok=True)
    box = BOX % {"hbox": hbox, "hmax": hmax, "boxgrade": 200} if hbox else ""
    geo = GEOM % {"hmin": hmin, "hmax": hmax, "dmin": dmin, "dmax": dmax, "box": box, "bg": 4 if hbox else 2,
                  "alg3d": alg}
    g = os.path.join(HERE, "mesh", f"{name}.geo")
    m = os.path.join(HERE, "mesh", f"{name}.msh")
    open(g, "w").write(geo)
    t = time.time()
    r = subprocess.run(["gmsh", g, "-3", "-order", "2", "-nt", "1", "-format", "msh22", "-o", m],
                       capture_output=True, text=True, timeout=3600)
    dt = time.time() - t
    log = r.stdout + r.stderr
    nodes = re.findall(r"(\d+) nodes (\d+) elements", log)
    out = {"variant": name, "h_strip_um": hmin, "h_far_um": hmax, "dist_min_um": dmin, "dist_max_um": dmax,
           "box_inner_um": hbox, "algorithm3d": alg, "exit": r.returncode, "seconds": dt,
           "nodes_order2": int(nodes[-1][0]) if nodes else None, "elements": int(nodes[-1][1]) if nodes else None,
           "errors": [l for l in log.splitlines() if "Error" in l][:5]}
    # tetrahedra and corner vertices from the mesh itself (the log's wording varies by version)
    if r.returncode == 0:
        import meshio
        import numpy as np
        mm = meshio.read(m)
        tets = mm.cells_dict["tetra10"]
        out["tetrahedra"] = int(len(tets))
        out["corner_vertices"] = int(len(np.unique(tets[:, :4])))
        out["p2_unknowns_approx"] = int(len(np.unique(tets)))
        sys.path.insert(0, os.path.join(HERE, "..", "q8-wire-foot"))
        from q8 import edge_lengths, tet_quality
        tri = mm.cells_dict["triangle6"][:, :3]
        p = mm.points
        fe = np.concatenate([np.linalg.norm(p[tri[:, a]] - p[tri[:, b]], axis=1) for a, b in ((0, 1), (1, 2), (0, 2))])
        q = tet_quality(tets, p)
        el = edge_lengths(tets, p)
        out.update({"strip_triangles": int(len(tri)), "strip_edge_min_um": float(fe.min()),
                    "strip_edge_median_um": float(np.median(fe)), "strip_edge_max_um": float(fe.max()),
                    "tet_edge_min_um": float(el.min()), "tet_edge_max_um": float(el.max()),
                    "min_tet_quality": float(q.min()), "tets_below_quality_0.2": int(np.sum(q < 0.2))})
    return out


def main():
    res = [run(*v) for v in VARIANTS]
    for r in res:
        print(r)
    json.dump(res, open(os.path.join(HERE, "q7.json"), "w"), indent=1)
    write_csv(os.path.join(HERE, "q7.csv"), ["variant", "h strip [um]", "h far [um]", "DistMax [um]", "box inner [um]",
                                             "3D algorithm", "nodes (order 2)", "tetrahedra", "corner vertices", "seconds", "strip triangles",
                                             "strip edge median [um]", "tet edge min [um]", "tet edge max [um]", "min tet quality [1]",
                                             "tets with quality < 0.2"],
              [(r["variant"], r["h_strip_um"], r["h_far_um"], r["dist_max_um"], r["box_inner_um"] or "",
                r["algorithm3d"], r["nodes_order2"], r.get("tetrahedra"), r.get("corner_vertices"), r["seconds"],
                r.get("strip_triangles"), r.get("strip_edge_median_um"), r.get("tet_edge_min_um"), r.get("tet_edge_max_um"),
                r.get("min_tet_quality"), r.get("tets_below_quality_0.2")) for r in res])


if __name__ == "__main__":
    main()
