"""2D quasi-static (Laplace, FV on a graded tensor grid, cell-wise permittivity) for a pair of strips
in a rectangular box. Box sides PEC or PMC; ground z = zlo PEC; lid z = zhi PEC or PMC."""
import numpy as np, scipy.sparse as sp, scipy.sparse.linalg as sla
from fv2d import graded, EPS0, C0

def cmat(y, z, epsc, fixed):
    ny, nz = len(y), len(z); dy, dz = np.diff(y), np.diff(z)
    idx = lambda i, j: i * nz + j
    E = lambda i, j: epsc[i, j] if 0 <= i < ny - 1 and 0 <= j < nz - 1 else 0.0
    rows, cols, vals = [], [], []
    for i in range(ny):
        for j in range(nz):
            p = idx(i, j)
            if i < ny - 1:   # edge to (i+1, j)
                g = (E(i, j - 1) * (dz[j - 1] / 2 if j > 0 else 0) + E(i, j) * (dz[j] / 2 if j < nz - 1 else 0)) / dy[i]
                q = idx(i + 1, j); rows += [p, p, q, q]; cols += [p, q, q, p]; vals += [g, -g, g, -g]
            if j < nz - 1:   # edge to (i, j+1)
                g = (E(i - 1, j) * (dy[i - 1] / 2 if i > 0 else 0) + E(i, j) * (dy[i] / 2 if i < ny - 1 else 0)) / dz[j]
                q = idx(i, j + 1); rows += [p, p, q, q]; cols += [p, q, q, p]; vals += [g, -g, g, -g]
    A = sp.csr_matrix((vals, (rows, cols)), shape=(ny * nz, ny * nz)) * EPS0
    free = np.where(fixed < 0)[0]; lu = sla.splu(A[free][:, free].tocsc())
    n = fixed.max(); C = np.zeros((n, n))
    for k in range(1, n + 1):
        phi = np.zeros(ny * nz); phi[fixed == k] = 1.0
        phi[free] = lu.solve(-(A[free] @ phi)); q = A @ phi
        for m in range(1, n + 1): C[m - 1, k - 1] = q[fixed == m].sum()
    return C

def solve(Y, zhi, h, er, t, strips, sides="PEC", lid="PEC", h0=0.5e-3, hmax=0.1, ratio=1.1):
    """strips: [(ya, yb)] at z in [h, h+t] (mm). Box y in [-Y, Y], z in [0, zhi]. Returns C, Cair (F/m)."""
    y = graded(-Y, Y, [v for s in strips for v in s] + [0.0], h0, hmax, ratio) * 1e-3
    z = graded(0.0, zhi, [h, h + t], h0, hmax, ratio) * 1e-3
    ny, nz = len(y), len(z)
    fixed = -np.ones((ny, nz), int)
    fixed[:, 0] = 0
    if lid == "PEC": fixed[:, -1] = 0
    if sides == "PEC": fixed[0, :] = 0; fixed[-1, :] = 0
    for k, (ya, yb) in enumerate(strips, 1):
        I = (y >= ya * 1e-3 - 1e-12) & (y <= yb * 1e-3 + 1e-12)
        J = (z >= h * 1e-3 - 1e-12) & (z <= (h + t) * 1e-3 + 1e-12)
        fixed[np.ix_(I, J)] = k
    zc = (z[:-1] + z[1:]) / 2
    epsc = np.where(zc[None, :] < h * 1e-3, er, 1.0) * np.ones((ny - 1, 1))
    f = fixed.ravel()
    return cmat(y, z, epsc, f), cmat(y, z, np.ones_like(epsc), f), (ny, nz)

def modal(C, Ca):
    out = {}
    for nm, s in (("even", 1), ("odd", -1)):
        c, ca = C[0, 0] + s * C[0, 1], Ca[0, 0] + s * Ca[0, 1]
        out[nm] = (1 / (C0 * np.sqrt(c * ca)), c / ca)
    return out

if __name__ == "__main__":
    import sys
    st = [(-1.25, -0.15), (0.15, 1.25)]
    for args in ((20 + 1.25, 20.508, "PEC", "PEC"),):
        C, Ca, shp = solve(args[0], args[1], 0.508, 3.5, 0.017, st, args[2], args[3])
        print(args, shp, {k: (round(v[0], 3), round(v[1], 4)) for k, v in modal(C, Ca).items()})
