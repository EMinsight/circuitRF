"""brief-emw-1: terminal S from an EMerge run's modal S, two ways, and the references each case is held to.

  csharp(...)  a line-for-line port of src/Engine/Em3d/ModalTerminalTransform.Solve: per-face least-squares M from V and
               P = 1 + S_m, a one-entry face's M = sqrt(Z_PV), the real Gram fit on a degenerate face, K from real parts,
               I = M^-H (2 - K C), or with robin='source' I = M^-H K (2 - C). It takes V and Z_PV in ITS normalisation (|T_V,ii|^2 = Z_PV); EMerge's modes carry 1 W,
               so |T_V|^2 = 2 Z_PV there, and EMerge's V enters divided by sqrt(2) (`scale`).
  exact(...)   what an EMerge run can do that a Palace run could not: every quantity from the mode fields themselves.
               C = W^-1 P (W the E-overlap EMerge's projection uses), T_V from each mode's own path integral, T_I from the
               power Gram (1/2 T_V^T conj(T_I) = Gp), and the modal current d = a - b in one of three forms (`robin`):
                 'palace'  d = 2 - K C          (the C# form: K on the reflected part only)
                 'emerge'  d = K (2 - C)        (EMerge's source is -2 j beta_1 e_n for every mode, its Robin j beta_1)
                 'none'    d = 2 - C            (K = 1)
               with K = beta_1 / beta_n per face (beta_1 = the mode EMerge's port BC uses).
"""
import numpy as np

C0 = 299792458.0
ETA0 = 376.730313668


# -- the C# transform, ported ------------------------------------------------------------------------------------------

def _fit_face(V, P, face):
    vf, pf = V[face, :], P[face, :]
    ph = pf.conj().T
    return vf @ ph @ np.linalg.inv(pf @ ph)


def _fit_real_g(m11, m12, m21, m22, z1, z2):
    def co(a, b, z):
        return (abs(a) ** 2 - z) / z, 2 * (a * np.conj(b)).real / z, abs(b) ** 2 / z
    p0, p1, p2 = co(m11, m12, z1)
    q0, q1, q2 = co(m22, m21, z2)
    R1 = lambda g: p0 + g * (p1 + g * p2)
    R2 = lambda g: q0 + g * (q1 + g * q2)
    F = lambda g: R1(g) ** 2 + R2(g) ** 2
    c3 = 2 * (p2 * p2 + q2 * q2); c2 = 3 * (p1 * p2 + q1 * q2)
    c1 = p1 * p1 + 2 * p0 * p2 + q1 * q1 + 2 * q0 * q2; c0 = p0 * p1 + q0 * q1
    edge = 1 - 1e-9
    cand = [-edge, 0.0, edge] + [r.real for r in np.roots([c3, c2, c1, c0]) if abs(r.imag) < 1e-9 and -edge < r.real < edge]
    g = min(cand, key=F)
    return g, max(abs(R1(g)), abs(R2(g)))


def csharp(S, V, zpv, kn, faces, z0, gram="fit", robin="real"):
    n = S.shape[0]
    P = np.eye(n) + S
    M = np.zeros((n, n), complex)
    for face in faces:
        if len(face) == 1:
            M[face[0], face[0]] = np.sqrt(zpv[face[0]])
            continue
        M[np.ix_(face, face)] = _fit_face(V, P, face)
    G = np.eye(n, dtype=complex)
    fits = []
    for face in faces:
        ks = [kn[e] for e in face]
        deg = len(face) > 1 and all(abs(k.real - ks[0].real) <= 1e-3 * abs(ks[0].real) for k in ks)
        if len(face) < 2 or gram == "identity" or (not deg and gram == "fit"):
            fits.append((deg, 0.0, 0.0)); continue
        a, b = face
        g, res = _fit_real_g(M[a, a], M[a, b], M[b, a], M[b, b], zpv[a], zpv[b])
        G[a, b] = G[b, a] = g
        fits.append((deg, g, res))
    K = np.ones(n, complex)
    if robin != "none":
        for face in faces:
            for e in face:
                K[e] = kn[face[0]].real / (kn[e] if robin == "complex" else kn[e].real)
    C = np.linalg.inv(G.T) @ P
    # robin 'source' (EMerge): every mode launched AND absorbed at the face's first beta, d = K (2 - C)
    D = K[:, None] * (2 * np.eye(n) - C) if robin == "source" else 2 * np.eye(n) - K[:, None] * C
    I = np.linalg.inv(M).conj().T @ D
    U = M @ P
    St = _s_from(U, I, z0)
    fit = np.zeros((n, n), complex)
    for face in faces:
        fit[np.ix_(face, face)] = _fit_face(V, P, face)
    off = np.abs(V - fit @ P).max() / np.abs(V).max()
    TV = fit @ G.T
    tv = np.abs(np.abs(np.diag(TV)) ** 2 - zpv) / zpv
    return St, dict(G=G, K=K, fits=fits, M_off=off, tv_mismatch=tv)


def _s_from(U, I, z0):
    z0 = np.asarray(z0, float) * np.ones(U.shape[0])
    A = U + z0[:, None] * I
    B = U - z0[:, None] * I
    BA = B @ np.linalg.inv(A)
    return BA * np.sqrt(z0[None, :] / z0[:, None])


# -- the transform from the mode fields ----------------------------------------------------------------------------------

def exact(S, V, faces_data, faces, z0, robin="emerge", use="measured"):
    """faces_data[f]: dict(W, TV, Gp, beta, beta_robin) for face f, whose entries are faces[f] (indices into S's order)."""
    n = S.shape[0]
    P = np.eye(n) + S
    C = np.zeros_like(P); U = np.zeros_like(P); I = np.zeros_like(P)
    resid = 0.0
    for fd, face in zip(faces_data, faces):
        W, TV, beta = fd["W"], fd["TV"], fd["beta"]
        Gp = fd["Gp"] if fd["Gp"] is not None else np.eye(len(face))
        Cf = np.linalg.solve(W, P[face, :])
        C[face, :] = Cf
        K = (fd["beta_robin"].real / beta.real)[:, None]
        delta = np.zeros_like(Cf)
        for a, e in enumerate(face):
            delta[a, e] = 1.0
        if robin == "palace":
            D = 2 * delta - K * Cf
        elif robin == "emerge":
            D = K * (2 * delta - Cf)
        else:
            D = 2 * delta - Cf
        TI = 2 * np.conj(np.linalg.solve(TV.T, Gp))
        U[face, :] = V[face, :] if use == "measured" else TV @ Cf
        I[face, :] = TI @ D
        resid = max(resid, np.abs(V[face, :] - TV @ Cf).max() / np.abs(V[face, :]).max())
    return _s_from(U, I, z0), dict(C=C, V_resid=resid)


# -- references ----------------------------------------------------------------------------------------------------------

def tl2(zm, theta, z0):
    D = 2 * zm * z0 * np.cos(theta) + 1j * (zm ** 2 + z0 ** 2) * np.sin(theta)
    s11 = 1j * (zm ** 2 - z0 ** 2) * np.sin(theta) / D
    s21 = 2 * zm * z0 / D
    return np.array([[s11, s21], [s21, s11]])


def coupled4(ze, zo, theta_e, theta_o, z0):
    """Ideal coupled line, ports 1 a near, 2 b near, 3 a far, 4 b far (the example's numbering)."""
    Se, So = tl2(ze, theta_e, z0), tl2(zo, theta_o, z0)
    S = np.zeros((4, 4), complex)
    near, far = (0, 1), (2, 3)            # (a, b) on each end
    ends = (near, far)
    for p in range(2):
        for q in range(2):
            same = (Se[p, q] + So[p, q]) / 2; cross = (Se[p, q] - So[p, q]) / 2
            a_p, b_p = ends[p]; a_q, b_q = ends[q]
            S[a_p, a_q] = S[b_p, b_q] = same
            S[a_p, b_q] = S[b_p, a_q] = cross
    return S


def read_snp(path):
    import re
    n = int(re.search(r"\.s(\d+)p$", path, re.I).group(1))
    unit, fmt, vals = 1e9, "MA", []
    scale = {"HZ": 1.0, "KHZ": 1e3, "MHZ": 1e6, "GHZ": 1e9}
    for line in open(path):
        line = line.split("!")[0].strip()
        if not line:
            continue
        if line.startswith("#"):
            for t in line[1:].upper().split():
                if t in scale: unit = scale[t]
                if t in ("RI", "MA", "DB"): fmt = t
            continue
        vals += [float(v) for v in line.split()]
    per = 1 + 2 * n * n
    out = {}
    for k in range(0, len(vals), per):
        rec = vals[k:k + per]; S = np.zeros((n, n), complex)
        for idx in range(n * n):
            a, b = rec[1 + 2 * idx], rec[2 + 2 * idx]
            i, j = (idx % n, idx // n) if n == 2 else (idx // n, idx % n)
            S[i, j] = a + 1j * b if fmt == "RI" else (a if fmt == "MA" else 10 ** (a / 20)) * np.exp(1j * np.radians(b))
        out[round(rec[0] * unit / 1e9, 6)] = S
    return out


def db(x):
    return 20 * np.log10(np.abs(x))
