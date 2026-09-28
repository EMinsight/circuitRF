"""brief-em3d-72 Q1, the consequence — a 1D wire embedded in a 3D mould mesh is a LINE SOURCE, and in 3D
a line source's own temperature is logarithmically singular: the discrete answer depends on the mesh size
at the wire. This measures by how much, against W4's coaxial closed form, on the simplest geometry that
has one (../../wire-mould): a gold wire on the axis of a mould cylinder whose outer surface is fixed.

Discretisation: P1 tetrahedra for the mould (scikit-fem), P1 line elements for the wire ON the embedded
curve's nodes (shared with the tetrahedra — Q1's finding), the wire's Joule heat lumped to its nodes,
wire ends fixed. rho constant (alpha = 0), so the problem is linear and W4's closed form is exact for the
coax model: T = T_amb + (q'/g') (1 - cosh(m x)/cosh(m L/2)), g' = 2 pi k_m / ln(r_o / r_w), m^2 = g'/(k A).

For each mesh it also reports the EFFECTIVE RADIUS r_eff at which the coax model reproduces the discrete
centre temperature: if r_eff / h is roughly constant, a Peaceman-style correction (couple the wire to the
mesh through an extra ln(r_eff / r_w) / (2 pi k_m) per unit length) removes the mesh dependence.
SPIKE MATERIAL.

    python line_source.py      # writes line-source.json, line-source.csv; meshes in mesh/
"""
import json
import os
import sys

import numpy as np
import scipy.sparse as sp
from scipy.optimize import brentq
from scipy.sparse.linalg import spsolve

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
import fem  # noqa: E402
from common import write_csv, write_json  # noqa: E402

UM = 1e-6
L, RO, D = 1000.0, 500.0, 25.4          # um: wire length, mould radius, wire diameter
K_M, K_W, RHO, I, T_AMB = 0.8, 318.0, 2.215e-8, 1.25, 25.0
A = np.pi * (D * UM) ** 2 / 4
QP = I * I * RHO / A                    # W/m

GEO = """SetFactory("OpenCASCADE");
Cylinder(1) = {0, 0, 0, 0, 0, %(L)s, %(RO)s};
Point(1001) = {0, 0, 0}; Point(1002) = {0, 0, %(L)s};
Line(1001) = {1001, 1002};
BooleanFragments{ Volume{1}; Delete; }{ Curve{1001}; Delete; }
e = 1e-3;
w[] = Curve In BoundingBox{-e, -e, -e, e, e, %(L)s + e};
outer[] = Surface In BoundingBox{-%(RO)s - e, -%(RO)s - e, e, %(RO)s + e, %(RO)s + e, %(L)s - e};
Physical Volume("mould") = {Volume{:}};
Physical Curve("wire") = {w[]};
Physical Surface("outer") = {outer[]};
Mesh.MeshSizeMax = 150; Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0;
Mesh.MeshSizeFromCurvature = 12;
Field[1] = Distance; Field[1].CurvesList = {w[]}; Field[1].Sampling = 400;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = %(h)s; Field[2].SizeMax = 150;
Field[2].DistMin = %(h)s; Field[2].DistMax = 600;
Background Field = 2;
"""


def coax_centre(r_eff):
    g = 2 * np.pi * K_M / np.log(RO / r_eff)
    m = np.sqrt(g / (K_W * A))
    return T_AMB + (QP / g) * (1 - 1 / np.cosh(m * L * UM / 2))


def run(h, tag, order=1):
    import meshio
    os.makedirs(os.path.join(HERE, "mesh"), exist_ok=True)
    g, mpath = os.path.join(HERE, "mesh", f"ls-{tag}.geo"), os.path.join(HERE, "mesh", f"ls-{tag}.msh")
    fem.run_gmsh(GEO % {"L": L, "RO": RO, "h": h}, g, mpath, threads=1)
    # scikit-fem's importer does not parse a mesh whose physical groups include LINE elements, so the
    # tetrahedral mesh is built here, and the outer surface is found by radius
    mm = meshio.read(mpath)
    from skfem import Basis, ElementTetP1, ElementTetP2, MeshTet
    mesh = MeshTet(np.ascontiguousarray(mm.points.T * UM), np.ascontiguousarray(mm.cells_dict["tetra"].T))
    basis = Basis(mesh, ElementTetP2() if order == 2 else ElementTetP1())
    Km = fem._laplace.assemble(basis, k=K_M)
    lines = mm.cells_dict["line"][mm.cell_sets_dict["wire"]["line"]]
    p = mesh.p
    nv = p.shape[1]
    n = basis.N
    # P2: an edge's dof is nv + its edge index (scikit-fem numbers vertex dofs first, then edges)
    edge_of = {tuple(e): i for i, e in enumerate(np.sort(mesh.edges, axis=0).T.tolist())}
    rows, cols, vals = [], [], []
    f = np.zeros(n)
    for a, b in lines:
        le = np.linalg.norm(p[:, a] - p[:, b])
        c = K_W * A / le
        if order == 1:
            dofs, Ke, fe = [a, b], c * np.array([[1, -1], [-1, 1]]), QP * le * np.array([0.5, 0.5])
        else:
            mdof = nv + edge_of[tuple(sorted((int(a), int(b))))]
            dofs = [a, b, mdof]
            Ke = (c / 3) * np.array([[7, 1, -8], [1, 7, -8], [-8, -8, 16]])
            fe = QP * le * np.array([1 / 6, 1 / 6, 2 / 3])
        for i_, di in enumerate(dofs):
            f[di] += fe[i_]
            for j_, dj in enumerate(dofs):
                rows.append(di); cols.append(dj); vals.append(Ke[i_, j_])
    Kw = sp.csr_matrix((vals, (rows, cols)), shape=(n, n))
    Kt = (Km + Kw).tocsr()
    r = np.hypot(p[0], p[1])
    on = r > RO * UM * (1 - 1e-6)
    outer = list(np.nonzero(on)[0])
    if order == 2:
        # the edges of boundary facets lying on the cylinder
        bf = mesh.facets[:, mesh.boundary_facets()]
        for tri in bf.T:
            if on[tri].all():
                for a_, b_ in ((0, 1), (1, 2), (0, 2)):
                    outer.append(nv + edge_of[tuple(sorted((int(tri[a_]), int(tri[b_]))))])
    outer = np.unique(outer)
    ends = [i for i in np.unique(lines) if p[2, i] < 1e-9 or p[2, i] > L * UM - 1e-9]
    Dd = np.unique(np.concatenate([outer, ends]))
    x = np.zeros(n)
    x[Dd] = T_AMB
    free = np.setdiff1d(np.arange(n), Dd)
    rhs = f[free] - Kt[free][:, Dd] @ x[Dd]
    x[free] = spsolve(Kt[free][:, free].tocsc(), rhs)
    wn = np.unique(lines)
    zc = p[2, wn]
    i = np.argmin(np.abs(zc - L * UM / 2))
    Tc = x[wn[i]]
    r_eff = brentq(lambda r: coax_centre(r) - Tc, 1e-3, RO * 0.99) if Tc < coax_centre(1e-3) else float("nan")
    return {"order": order, "h_at_wire_um": h, "dofs": int(n), "wire_segments": int(len(lines)), "T_centre_discrete_degC": float(Tc),
            "T_centre_coax_model_degC": float(coax_centre(D / 2)), "r_eff_um": r_eff, "r_eff_over_h": r_eff / h}


def main():
    res = [run(h, str(i), order) for order in (1, 2) for i, h in enumerate([80, 40, 20, 10, 5])]
    for r in res:
        print(r)
    write_json(os.path.join(HERE, "line-source.json"), {"parameters": {"L_um": L, "r_outer_um": RO, "d_um": D, "k_mould": K_M, "k_wire": K_W, "rho": RHO,
                              "I_A": I, "T_amb_degC": T_AMB, "q_per_m_W": QP}, "runs": res})
    write_csv(os.path.join(HERE, "line-source.csv"),
              ["element order [1]", "h at wire [um]", "dofs", "T centre discrete [degC]", "T centre coax model [degC]", "r_eff [um]", "r_eff/h [1]"],
              [(r["order"], r["h_at_wire_um"], r["dofs"], r["T_centre_discrete_degC"], r["T_centre_coax_model_degC"], r["r_eff_um"],
                r["r_eff_over_h"]) for r in res])


if __name__ == "__main__":
    main()
