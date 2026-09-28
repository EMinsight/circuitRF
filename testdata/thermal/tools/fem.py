"""An independent steady-conduction FEM for the 3D references (brief-em3d-72 S5, S6, and the spike's
Q4-Q6 matrices). Spike material.

The finite elements, assembly and quadrature are scikit-fem's (BSD-3-Clause); the linear solves are
scipy's or pyamg's (MIT). Meshes come from the `gmsh` EXECUTABLE (GPL), called as a program on a .geo
file this module writes — no Gmsh library is imported, and nothing of Gmsh is committed. The .msh is read
by meshio (MIT) through scikit-fem's importer, which turns physical groups into named subdomains and
boundaries. Geometry is written in MICROMETRES (the circuitRF lowering's convention, for OCCT's absolute
tolerances) and scaled to metres on import.
"""
from __future__ import annotations

import os
import re
import subprocess
import time

import numpy as np
import scipy.sparse as sp
from skfem import (Basis, BilinearForm, ElementTetP1, ElementTetP2, FacetBasis, LinearForm, MeshTet, condense,
                   solve)
from skfem.helpers import dot, grad

GMSH = os.environ.get("GMSH", "gmsh")


def gmsh_version() -> str:
    return subprocess.run([GMSH, "--version"], capture_output=True, text=True).stderr.strip() or \
        subprocess.run([GMSH, "--version"], capture_output=True, text=True).stdout.strip()


def run_gmsh(geo_text: str, geo_path: str, msh_path: str, threads: int = 1) -> dict:
    with open(geo_path, "w", newline="\n") as f:
        f.write(geo_text)
    t = time.time()
    r = subprocess.run([GMSH, geo_path, "-3", "-format", "msh41", "-nt", str(threads), "-o", msh_path],
                       capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(r.stdout[-3000:] + r.stderr[-3000:])
    info = {"mesh_seconds": time.time() - t}
    m = re.findall(r"(\d+) nodes (\d+) elements", r.stdout)
    if m:
        info["gmsh_nodes"], info["gmsh_elements"] = int(m[-1][0]), int(m[-1][1])
    info["log_tail"] = r.stdout[-1500:]
    return info


def load(msh_path: str, scale: float = 1e-6) -> MeshTet:
    mesh = MeshTet.load(msh_path)
    return mesh.scaled(scale) if hasattr(mesh, "scaled") else MeshTet(mesh.p * scale, mesh.t,
                                                                         _boundaries=mesh.boundaries,
                                                                         _subdomains=mesh.subdomains)


def element(order: int):
    return ElementTetP2() if order == 2 else ElementTetP1()


@BilinearForm
def _laplace(u, v, w):
    return w.k * dot(grad(u), grad(v))


@LinearForm
def _unit(v, w):
    return 1.0 * v


def assemble(mesh: MeshTet, order: int, k_by_subdomain: dict, fluxes: dict, fixed: dict):
    """K and the load vector(s). fluxes: {boundary name: flux W/m^2} (one load column each, returned in
    the order given). fixed: {boundary name: temperature}. Returns basis, K, F (n x len(fluxes)), D, x_D."""
    e = element(order)
    basis = Basis(mesh, e)
    K = None
    for name, k in k_by_subdomain.items():
        b = Basis(mesh, e, elements=mesh.subdomains[name])
        Ki = _laplace.assemble(b, k=k)
        K = Ki if K is None else K + Ki
    F = np.zeros((basis.N, len(fluxes)))
    for j, (name, q) in enumerate(fluxes.items()):
        fb = FacetBasis(mesh, e, facets=mesh.boundaries[name])
        F[:, j] = q * _unit.assemble(fb)
    D = np.unique(np.concatenate([basis.get_dofs(mesh.boundaries[n]).all() for n in fixed]))
    xD = np.zeros(basis.N)
    for n, T in fixed.items():
        xD[basis.get_dofs(mesh.boundaries[n]).all()] = T
    return basis, K.tocsr(), F, D, xD


def facet_mean(mesh, order, u, name):
    """The area-weighted mean of u over a named boundary: w_i = int phi_i ds, sum_i w_i = the area."""
    w = _unit.assemble(FacetBasis(mesh, element(order), facets=mesh.boundaries[name]))
    return float((w @ u) / w.sum())


def solve_amg(K, f, D, xD, tol=1e-12):
    """Condense the Dirichlet dofs and solve with CG + pyamg smoothed aggregation. Returns (u, info)."""
    import pyamg
    A, b, x, I = condense(K, f, x=xD, D=D)
    t = time.time()
    ml = pyamg.smoothed_aggregation_solver(A.tocsr(), symmetry="symmetric")
    res = []
    xi = ml.solve(b, tol=tol, accel="cg", residuals=res, maxiter=500)
    x = x.copy()
    x[I] = xi
    return x, {"iterations": len(res) - 1, "solve_seconds": time.time() - t, "unknowns": A.shape[0],
               "nnz": A.nnz}


def solve_amg_multi(K, F, D, xD, tol=1e-12):
    """Several right-hand sides (columns of F) with ONE smoothed-aggregation hierarchy."""
    import pyamg
    A, _, _, I = condense(K, F[:, 0], x=xD, D=D)
    ml = pyamg.smoothed_aggregation_solver(A.tocsr(), symmetry="symmetric")
    U, its = [], []
    for j in range(F.shape[1]):
        _, b, x, _ = condense(K, F[:, j], x=xD, D=D)
        res = []
        x = x.copy()
        x[I] = ml.solve(b, tol=tol, accel="cg", residuals=res, maxiter=500)
        U.append(x)
        its.append(len(res) - 1)
    return np.array(U).T, {"iterations": its, "unknowns": A.shape[0], "nnz": A.nnz}


def condensed(K, f, D, xD):
    A, b, x, I = condense(K, f, x=xD, D=D)
    return A.tocsr(), b, x, I
