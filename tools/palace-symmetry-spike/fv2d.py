"""2D Laplace (finite volume on a graded tensor grid) for a homogeneous-air stripline cross-section.
Returns the 2x2 capacitance matrix per unit length; in air the TEM line's characteristic matrix is
Zc = C^-1 / c, exact for any (also asymmetric) cross-section."""
import numpy as np, scipy.sparse as sp, scipy.sparse.linalg as sla

EPS0 = 8.8541878128e-12; C0 = 299792458.0

def graded(lo, hi, marks, h0, hmax, ratio=1.15):
    marks = sorted(set([lo, hi] + [m for m in marks if lo <= m <= hi]))
    pts = set(marks)
    for i in range(len(marks) - 1):
        a, b = marks[i], marks[i + 1]
        # grow from both ends toward the middle
        left, right, h = [a], [b], h0
        while True:
            if left[-1] + h >= right[-1] - h: break
            left.append(left[-1] + h); right.append(right[-1] - h)
            h = min(h * ratio, hmax)
        pts.update(left); pts.update(right)
    return np.array(sorted(pts))

def solve(Y, b, t, strips, h0=0.3e-3, hmax=0.05, ratio=1.15, walls=("PEC", "PEC"), ylo=None):
    """strips: list of (ya, yb) in mm, each z in [-t/2, t/2]. Box y in [ylo or -Y, Y], z in [-b/2, b/2].
    walls: condition at (ylo, Y): 'PEC' or 'PMC'. Returns C (F/m)."""
    ylo = -Y if ylo is None else ylo
    ymarks = [v for s in strips for v in s] + [0.0]
    y = graded(ylo, Y, ymarks, h0, hmax, ratio) * 1e-3
    z = graded(-b / 2, b / 2, [-t / 2, t / 2], h0, hmax, ratio) * 1e-3
    ny, nz = len(y), len(z)
    idx = lambda i, j: i * nz + j
    fixed = np.full(ny * nz, -1)       # -1 free, 0 ground, k>0 conductor k
    for i in range(ny):
        for j in range(nz):
            if (j == 0 or j == nz - 1): fixed[idx(i, j)] = 0
            if i == 0 and walls[0] == "PEC": fixed[idx(i, j)] = 0
            if i == ny - 1 and walls[1] == "PEC": fixed[idx(i, j)] = 0
    for k, (ya, yb) in enumerate(strips, 1):
        I = np.where((y >= ya * 1e-3 - 1e-12) & (y <= yb * 1e-3 + 1e-12))[0]
        J = np.where((z >= -t / 2 * 1e-3 - 1e-12) & (z <= t / 2 * 1e-3 + 1e-12))[0]
        for i in I:
            for j in J: fixed[idx(i, j)] = k
    # dual cell widths
    dy = np.diff(y); dz = np.diff(z)
    wy = np.zeros(ny); wy[:-1] += dy / 2; wy[1:] += dy / 2
    wz = np.zeros(nz); wz[:-1] += dz / 2; wz[1:] += dz / 2
    rows, cols, vals = [], [], []
    for i in range(ny):
        for j in range(nz):
            p = idx(i, j)
            for (ii, jj, g) in ((i - 1, j, wz[j] / dy[i - 1] if i > 0 else 0), (i + 1, j, wz[j] / dy[i] if i < ny - 1 else 0),
                                (i, j - 1, wy[i] / dz[j - 1] if j > 0 else 0), (i, j + 1, wy[i] / dz[j] if j < nz - 1 else 0)):
                if g == 0: continue
                q = idx(ii, jj)
                rows += [p, p]; cols += [p, q]; vals += [g, -g]
    A = sp.csr_matrix((vals, (rows, cols)), shape=(ny * nz, ny * nz)) * EPS0
    free = np.where(fixed < 0)[0]
    Aff = A[free][:, free].tocsc(); lu = sla.splu(Aff)
    n = len(strips); C = np.zeros((n, n))
    for k in range(1, n + 1):
        phi = np.zeros(ny * nz); phi[fixed == k] = 1.0
        rhs = -A[free] @ phi
        phi[free] = lu.solve(rhs)
        q = A @ phi
        for m in range(1, n + 1):
            C[m - 1, k - 1] = q[fixed == m].sum()
    return C, (ny, nz)

def zc(C): return np.linalg.inv(C) / C0

if __name__ == "__main__":
    for h0 in (1e-3, 0.3e-3, 0.15e-3):
        C, shp = solve(7.4, 2.0, 0.02, [(-1.4, -0.2), (0.2, 1.4)], h0=h0)
        Z = zc(C); ze = Z[0, 0] + Z[0, 1]; zo = Z[0, 0] - Z[0, 1]
        Ch, _ = solve(7.4, 2.0, 0.02, [(-1.4, -0.2)], h0=h0, walls=("PEC", "PMC"), ylo=-7.4) if False else (None, None)
        print(f"h0 {h0*1e3:.2f} um grid {shp}: Z0e {ze:.3f} Z0o {zo:.3f}")
