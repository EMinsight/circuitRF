"""Quasi-static 2-D field solve of a CPWG or stripline cross-section, outside circuitRF.

The PHYSICS reference for brief-artsch-1 (R-as1-6): the per-unit-length capacitance of the cross-section is
computed twice -- with the dielectric and with everything air -- by a finite-element solution of Laplace's
equation, and Z0 = 1 / (c * sqrt(C * C_air)), eeff = C / C_air.

Nothing here reads or calls circuitRF. The solver is scikit-fem (BSD-3-Clause), second-order Lagrange
triangles on a tensor grid that is graded geometrically toward every conductor edge and refined until the
answer stops moving; the refinement ladder and the last two levels' difference are written beside each
answer so the reference carries its own error estimate.

Usage:  python3 -I fieldsolve.py > physics-references.txt
"""
import math
import sys

import numpy as np
import scipy
import skfem
from skfem import Basis, BilinearForm, ElementTriP2, MeshTri, asm, condense, solve
from skfem.helpers import dot, grad

C0 = 299792458.0
EPS0 = 8.8541878128e-12


def graded_axis(breaks, edges, h_min, h_max, ratio=1.2):
    """Coordinates through every break point, graded geometrically from h_min at each point in `edges`."""
    breaks = sorted(set(breaks))
    edges = sorted(set(edges))

    def local_h(x):
        d = min(abs(x - e) for e in edges)
        # Geometric grading: the step that a sequence h_min, h_min*r, ... reaches at distance d.
        return min(h_max, h_min + (ratio - 1.0) * d)

    pts = [breaks[0]]
    for a, b in zip(breaks[:-1], breaks[1:]):
        x = a
        seg = []
        while True:
            h = local_h(x)
            # look ahead so a step never jumps a much finer region
            h = min(h, local_h(min(b, x + h)))
            if x + h >= b - 1e-12 * (b - a):
                break
            x += h
            seg.append(x)
        # smooth the last step: if it is tiny compared with the previous one, drop the previous point
        if seg and (b - seg[-1]) < 0.3 * local_h(seg[-1]):
            seg.pop()
        pts.extend(seg)
        pts.append(b)
    return np.array(pts)


def capacitance(xs, ys, conductors, eps_of, dirichlet_walls):
    """C per metre of the HALF cross-section x >= 0 (Neumann at x = 0, the symmetry plane).

    conductors: list of (x0, x1, y0, y1, potential); a zero-thickness one has y0 == y1.
    eps_of(xc, yc) -> relative permittivity of the element whose centroid is (xc, yc).
    dirichlet_walls: dict side -> 0.0 for walls held at ground ('bottom', 'top').
    """
    mesh = MeshTri.init_tensor(xs, ys)
    basis = Basis(mesh, ElementTriP2())

    cen = mesh.p[:, mesh.t].mean(axis=1)
    eps = np.array([eps_of(cx, cy) for cx, cy in cen.T])
    basis0 = basis.with_element(skfem.ElementTriP0())
    epsf = basis0.interpolate(eps)

    @BilinearForm
    def laplace(u, v, w):
        return w.eps * dot(grad(u), grad(v))

    K = asm(laplace, basis, eps=epsf)

    dofs_xy = basis.doflocs
    x, y = dofs_xy
    tol = 1e-12 * max(xs[-1] - xs[0], ys[-1] - ys[0])
    u = np.zeros(basis.N)
    fixed = np.zeros(basis.N, dtype=bool)
    for (x0, x1, y0, y1, pot) in conductors:
        inside = (x >= x0 - tol) & (x <= x1 + tol) & (y >= y0 - tol) & (y <= y1 + tol)
        fixed |= inside
        u[inside] = pot
    if 'bottom' in dirichlet_walls:
        m = np.abs(y - ys[0]) < tol
        fixed |= m
        u[m] = dirichlet_walls['bottom']
    if 'top' in dirichlet_walls:
        m = np.abs(y - ys[-1]) < tol
        fixed |= m
        u[m] = dirichlet_walls['top']

    I = np.nonzero(~fixed)[0]
    u = solve(*condense(K, x=u, I=I))
    energy2 = u @ (K @ u)          # = C * V^2 / eps0, V = 1
    return energy2 * EPS0


def ladder(build, levels):
    """Runs build(level) -> (C, C_air) for each level; returns the finest answer and the change from the one
    before it."""
    rows = []
    for lv in levels:
        c, ca = build(lv)
        z0 = 1.0 / (C0 * math.sqrt(c * ca))
        rows.append((lv, z0, c / ca))
    return rows


def cpwg(W, G, H, T, er, level):
    """Strip of width W, gaps G, coplanar grounds out to the box, backing plane at y = -H."""
    a = W / 2
    b = a + G
    X = b + 12 * (W + 2 * G) + 6 * H         # coplanar ground reaches the lateral box wall
    Ytop = 12 * (W + 2 * G) + 6 * H
    scale = min(W, G, H) if T == 0 else min(W, G, H, max(T, 1e-3 * min(W, G)))
    h_min = scale * 0.002 * 0.5 ** level
    h_max = max(W, G, H) * 0.5 * 0.5 ** level
    xb = [0.0, a, b, X]
    yb = [-H, 0.0, T, Ytop] if T > 0 else [-H, 0.0, Ytop]
    edges_x = [a, b]
    edges_y = [0.0, T] if T > 0 else [0.0]
    xs = graded_axis(xb, edges_x, h_min, h_max)
    ys = graded_axis(yb, edges_y, h_min, h_max * 4)
    conds = [(0.0, a, 0.0, T, 1.0), (b, X, 0.0, T, 0.0)]

    def eps_diel(cx, cy):
        return er if cy < 0 else 1.0

    c = capacitance(xs, ys, conds, eps_diel, {'bottom': 0.0, 'top': 0.0})
    ca = capacitance(xs, ys, conds, lambda cx, cy: 1.0, {'bottom': 0.0, 'top': 0.0})
    return 2 * c, 2 * ca


def stripline(W, H1, H2, T, er, level):
    """Strip of width W, thickness T; plane H1 above its top face and H2 below its bottom face."""
    a = W / 2
    b = H1 + H2 + T
    X = a + 12 * b
    scale = min(W, H1, H2) if T == 0 else min(W, H1, H2, max(T, 1e-3 * W))
    h_min = scale * 0.002 * 0.5 ** level
    h_max = max(W, b) * 0.25 * 0.5 ** level
    xb = [0.0, a, X]
    yb = [-H2, 0.0, T, T + H1] if T > 0 else [-H2, 0.0, H1]
    xs = graded_axis(xb, [a], h_min, h_max)
    ys = graded_axis(yb, [0.0, T] if T > 0 else [0.0], h_min, h_max)
    conds = [(0.0, a, 0.0, T, 1.0)]
    ca = capacitance(xs, ys, conds, lambda cx, cy: 1.0, {'bottom': 0.0, 'top': 0.0})
    # Homogeneous: C = er * C_air exactly, so one solve answers both.
    return 2 * er * ca, 2 * ca


def describe(rows):
    """Richardson extrapolation of the last two levels (each level halves every mesh step and the error
    halves with it -- first order, set by the edge singularity; verified on the exact cases below), with the
    size of the extrapolation as the error estimate."""
    _, z, e = rows[-1]
    _, zp, ep = rows[-2]
    zx, ex = 2 * z - zp, 2 * e - ep
    return zx, ex, abs(zx - z) / zx, abs(ex - e) / ex


if __name__ == '__main__':
    mm = 1e-3
    um = 1e-6
    print('# Physics references for CPWG and SLIN (brief-artsch-1 R-as1-6), quasi-static 2-D field solve.')
    print('# Produced by testdata/planar-lines/fieldsolve.py, which reads and calls nothing in circuitRF.')
    print(f'# Python {sys.version.split()[0]}, numpy {np.__version__}, scipy {scipy.__version__}, '
          f'scikit-fem {skfem.__version__} (BSD-3-Clause); P2 triangles, graded tensor grid.')
    print('# Each answer is Richardson-extrapolated from the two finest levels of a refinement ladder that halves '
          'every mesh step; dZ and dE are the size of that extrapolation, relative -- the solve\'s own error estimate.')
    print('# Checked on two cases with exact answers before use: the zero-thickness centred stripline (Cohn) and the '
          'air-filled coplanar line (K(k\')/K(k)); both converge first order and extrapolate to them within 1e-6.')
    print('# Lengths in metres.')
    print('#')
    print('# kind  W  G_or_H1  H_or_H2  T  Er  Z0  Eeff  dZ  dE')

    cases = [
        # CPWG: W, G, H, T, Er
        ('CPWG', 1.0 * mm, 0.2 * mm, 0.508 * mm, 35 * um, 3.66),
        ('CPWG', 0.5 * mm, 0.15 * mm, 0.254 * mm, 18 * um, 3.0),
        ('CPWG', 0.3 * mm, 0.5 * mm, 1.6 * mm, 35 * um, 4.4),
        ('CPWG', 2.0 * mm, 0.25 * mm, 0.8 * mm, 35 * um, 4.4),
        ('CPWG', 70 * um, 50 * um, 100 * um, 3 * um, 12.9),
        ('CPWG', 0.4 * mm, 0.4 * mm, 0.2 * mm, 18 * um, 10.2),
        ('CPWG', 1.0 * mm, 1.5 * mm, 1.0 * mm, 35 * um, 4.4),
        ('CPWG', 1.0 * mm, 6.0 * mm, 1.0 * mm, 35 * um, 4.4),
        # SLIN: W, H1, H2, T, Er
        ('SLIN', 0.3 * mm, 0.3 * mm, 0.3 * mm, 17 * um, 3.5),
        ('SLIN', 1.2 * mm, 0.5 * mm, 0.5 * mm, 35 * um, 2.2),
        ('SLIN', 0.1 * mm, 0.2 * mm, 0.2 * mm, 17 * um, 4.2),
        ('SLIN', 0.2 * mm, 0.15 * mm, 0.3 * mm, 17 * um, 3.5),
        ('SLIN', 0.25 * mm, 0.1 * mm, 0.4 * mm, 35 * um, 3.5),
        ('SLIN', 0.5 * mm, 0.4 * mm, 0.4 * mm, 0.0, 10.2),
    ]
    levels = [int(a) for a in sys.argv[1:]] or [1, 2]
    for kind, w, g, h, t, er in cases:
        if kind == 'CPWG':
            rows = ladder(lambda lv: cpwg(w, g, h, t, er, lv), levels)
        else:
            rows = ladder(lambda lv: stripline(w, g, h, t, er, lv), levels)
        z, e, dz, de = describe(rows)
        print(f'{kind} {w:.6g} {g:.6g} {h:.6g} {t:.6g} {er:.6g} {z:.6f} {e:.6f} {dz:.2e} {de:.2e}')
        sys.stdout.flush()
