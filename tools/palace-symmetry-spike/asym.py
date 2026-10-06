"""§4d: the §3 combination on an asymmetric pair (line 2 10 % wider).
Exact reference: homogeneous-air MTL from the 2D capacitance matrix, Zc = C^-1 / c, beta = k0 for every
mode. Ports 1,2 = line 1 near/far, 3,4 = line 2 near/far."""
import sys, numpy as np
from ana import read, compare, renorm2, combine, tl2, C0
import fv2d

Z0 = 50.0; ELL = 15e-3; F = 5.0

def mtl4(Zc, f, ell, z0):
    th = 2 * np.pi * f * 1e9 / C0 * ell; Yc = np.linalg.inv(Zc)
    Yn = -1j / np.tan(th) * Yc; Yf = 1j / np.sin(th) * Yc
    Y = np.block([[Yn, Yf], [Yf, Yn]])            # order: near1, near2, far1, far2
    S = (np.eye(4) - z0 * Y) @ np.linalg.inv(np.eye(4) + z0 * Y)
    P = [0, 2, 1, 3]                              # -> ports 1 (near1), 2 (far1), 3 (near2), 4 (far2)
    return S[np.ix_(P, P)]

def project(S4):
    """§3 by projection: even/odd 2-ports from a 4-port, u = (1, ±1)/√2 on each end."""
    out = []
    for s in (1, -1):
        M = np.zeros((2, 2), complex)
        ends = ((0, 2), (1, 3))
        for a in range(2):
            for b in range(2):
                i1, i3 = ends[a]; j1, j3 = ends[b]
                M[a, b] = (S4[i1, j1] + s * S4[i1, j3] + s * S4[i3, j1] + S4[i3, j3]) / 2
        out.append(M)
    return out

def renorm4(S, zp, z0):
    I = np.eye(4); Zr = np.diag(zp); sq = np.sqrt(Zr)
    Z = sq @ (I + S) @ np.linalg.inv(I - S) @ sq
    return (Z - z0 * I) @ np.linalg.inv(Z + z0 * I)

def exact(wb):
    C, _ = fv2d.solve(0.2 + max(1.2, wb) + 6.0, 2.0, 0.02, [(-1.4, -0.2), (0.2, 0.2 + wb)], h0=0.15e-3, hmax=0.01, ratio=1.03)
    return C

if __name__ == "__main__":
    wb = float(sys.argv[1]) if len(sys.argv) > 1 else 1.32
    C = exact(wb); Zc = np.linalg.inv(C) / C0
    print(f"2D, line 2 W = {wb}: Zc = {np.round(Zc, 3).tolist()}")
    R = mtl4(Zc, F, ELL, Z0)
    Se, So = project(R)
    txt, w, sv = compare(combine(Se, So), R, "formula alone: §3 applied to the EXACT asymmetric 4-port")
    print(txt)
    # the S entries of line 2 too (S33, S43), which a symmetric combination forces equal to line 1's
    for n, (i, j) in (("S33", (2, 2)), ("S43", (3, 2))):
        print(f"  exact {n} {20*np.log10(abs(R[i, j])):.3f} dB vs combined {20*np.log10(abs(combine(Se, So)[i, j])):.3f} dB")
    if len(sys.argv) > 3:
        ev, od = sys.argv[2], sys.argv[3]
        mods = []
        for run, s in ((ev, 0), (od, 1)):
            Sd, z = read(run); S = Sd[F]
            M = np.zeros((4, 4), complex)
            for j in (1, 3):                   # excited ports; 2, 4 by the end-to-end mirror
                for i in (1, 2, 3, 4): M[i - 1, j - 1] = S[(i, j)]
            mir = {0: 1, 1: 0, 2: 3, 3: 2}
            for j in (2, 4):
                for i in range(4): M[i, j - 1] = M[mir[i], mir[j - 1]]
            M = renorm4(M, [z[F][k] for k in (1, 2, 3, 4)], Z0)
            mods.append(project(M)[s])
            print(f"{run}: Z_PV {[round(z[F][k], 3) for k in (1, 2, 3, 4)]}")
        txt, w, sv = compare(combine(*mods), R, "Palace route A, combined, vs exact asymmetric")
        print(txt)
        for n, (i, j) in (("S33", (2, 2)), ("S43", (3, 2))):
            print(f"  exact {n} {20*np.log10(abs(R[i, j])):.3f} dB vs combined {20*np.log10(abs(combine(*mods)[i, j])):.3f} dB, |ΔS| {abs(R[i,j]-combine(*mods)[i,j]):.4f}")
