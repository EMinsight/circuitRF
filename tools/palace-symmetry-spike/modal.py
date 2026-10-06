"""brief-em3d-124: terminal S from Palace's modal S on a shared port face (brief 113's transform, rebuilt).

One port face per end; on it N WavePort entries, Mode k on entry k, the face's lowest-index entry Active, every
entry excited, every entry's VoltagePath from ITS terminal (signal) to ground. Inputs are only what Palace writes:
port-S.csv (S_m), port-V.csv (V_wp), port-Z.csv (Z_PV) and the log's k_n.

Per face, with modes (e_n, h_n) normalised to <e_n, h_n> = 1 (peak) and G[n, i] = <e_n, h_i> = ∫ e_n × h_i* · n:
  P   = 1 + S_m = Gᵀ C          C = modal amplitude of the total transverse E (a + b)
  V   = T_V C                    T_V[k, n] = ∫_path_k e_n · dl
  M   = V (1 + S_m)⁻¹ = T_V G⁻ᵀ
  T_I = M⁻ᴴ                      TEM: G = T_Vᵀ T_I*  (cross power = Σ_k V_k I_k*), so G cancels
  a − b = 2·s − K C              K = diag(k_active / k_m): the scalar Robin on a face uses the ACTIVE entry's k
                                 (Re k_n in a driven solve), 1D port model; K = 1 is brief 113's §1c "as written".
                                 Lossy: Palace builds h (source, projection) from Re k_m while the true H is k_m/Re k_m
                                 times it; the factors cancel and K = diag(Re k_active / Re k_m) (brief 124)
  U = V,  I = T_I (a − b),  S_t = (U − Z₀ I)(U + Z₀ I)⁻¹
G is fixed per face from Palace's own Z_PV: Z_PV[i] = |T_V[i, i]|² = |(M Gᵀ)[i, i]|², G symmetric with unit diagonal,
real off-diagonals (113) or complex (to show whether the complex fit lands on a wrong phase).
"""
import csv, os, re, sys, json
import numpy as np
from scipy.optimize import least_squares

RUNS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "runs")


def _rows(path):
    rows = list(csv.reader(open(path)))
    return [h.strip() for h in rows[0]], [[float(x) for x in r] for r in rows[1:] if r]


def _pairs(h):
    return [int(v) for v in re.findall(r"\[(\d+)\]", h)]


def read_run(d):
    """Return {f: dict(S=NxN, V=NxN, Z=N)}; d is a run dir (with postpro/) or a fixture dir."""
    p = os.path.join(d, "postpro") if os.path.isdir(os.path.join(d, "postpro")) else d
    if not os.path.exists(os.path.join(p, "port-S.csv")) and os.path.isdir(os.path.join(p, "run")):
        p = os.path.join(p, "run")
    out = {}
    hs, rs = _rows(os.path.join(p, "port-S.csv"))
    n = max(max(_pairs(h)) for h in hs if h.startswith("|S["))
    for r in rs:
        S = np.zeros((n, n), complex)
        for k, h in enumerate(hs):
            if h.startswith("|S["):
                i, j = _pairs(h)
                S[i - 1, j - 1] = 10 ** (r[k] / 20) * np.exp(1j * np.radians(r[k + 1]))
        out[r[0]] = {"S": S}
    hv, rv = _rows(os.path.join(p, "port-V.csv"))
    for r in rv:
        V = np.zeros((n, n), complex)
        for k, h in enumerate(hv):
            if h.startswith("Re{V_wp["):
                ij = _pairs(h); i, j = (ij[0], 1) if len(ij) == 1 else ij
                V[i - 1, j - 1] = r[k] + 1j * r[k + 1]
        out[r[0]]["V"] = V
    hz, rz = _rows(os.path.join(p, "port-Z.csv"))
    for r in rz:
        out[r[0]]["Z"] = np.array([r[k] for k, h in enumerate(hz) if h.startswith("Re{Z_PV[")])
    return out


def read_kn(d):
    """Log k_n per frequency (in solve order) per port index: list of {idx: complex}."""
    log = os.path.join(d, "palace.log")
    out, cur = [], {}
    for line in open(log):
        m = re.search(r"Port (\d+), mode (\d+): kₙ = ([-+]?[0-9.]+e[-+][0-9]+)([-+][0-9.]+e[-+][0-9]+)i", line)
        if m:
            i = int(m.group(1))
            if i in cur:
                out.append(cur); cur = {}
            cur[i] = complex(float(m.group(3)), float(m.group(4)))
    if cur: out.append(cur)
    return out


def fit_g(M, z, idx, complex_g=False):
    """G for one face (entries idx, 0-based), symmetric, unit diagonal, from |(M Gᵀ)_ii|² = Z_PV[i]."""
    n = len(idx); pairs = [(a, b) for a in range(n) for b in range(a + 1, n)]
    Mf = M[np.ix_(idx, idx)]; zf = z[idx]

    def G_of(p):
        G = np.eye(n, dtype=complex)
        for k, (a, b) in enumerate(pairs):
            g = p[k] + (1j * p[len(pairs) + k] if complex_g else 0)
            G[a, b] = G[b, a] = g
        return G

    def res(p):
        T = Mf @ G_of(p).T
        return (np.abs(np.diag(T)) ** 2 - zf) / zf

    best = None
    for s in np.linspace(-0.9, 0.9, 7):       # several starts: the equations can have two roots
        p0 = np.full(len(pairs) * (2 if complex_g else 1), 0.0); p0[:len(pairs)] = s
        r = least_squares(res, p0, xtol=1e-14, ftol=1e-14, gtol=1e-14)
        if best is None or r.cost < best.cost - 1e-18:
            best = r
    roots = []
    for s in np.linspace(-0.95, 0.95, 39):
        p0 = np.full(len(pairs) * (2 if complex_g else 1), 0.0); p0[:len(pairs)] = s
        r = least_squares(res, p0, xtol=1e-14, ftol=1e-14, gtol=1e-14)
        if r.cost < 1e-12 and not any(np.allclose(r.x, q, atol=1e-6) for q in roots):
            roots.append(r.x)
    return G_of(best.x), np.max(np.abs(res(best.x))), [G_of(q)[0, 1] for q in roots]


def transform(S, V, Z, faces, kn=None, gram="real", robin="re", Z0=50.0):
    """faces: list of index lists (0-based entries), the first entry of each the Active one.
    gram: 'real' | 'complex' | 'none'.  Returns S_t (rows/cols in entry = terminal order) and diagnostics."""
    N = S.shape[0]; P = np.eye(N) + S
    M = V @ np.linalg.inv(P)
    G = np.eye(N, dtype=complex); info = {}
    for fi, idx in enumerate(faces):
        if gram != "none" and len(idx) > 1:
            Gf, resid, roots = fit_g(M, Z, idx, complex_g=(gram == "complex"))
            G[np.ix_(idx, idx)] = Gf
            info[f"face{fi}"] = {"g": Gf[0, 1] if len(idx) == 2 else Gf, "resid": resid, "roots": roots}
    C = np.linalg.inv(G.T) @ P
    K = np.eye(N, dtype=complex)
    if robin not in (False, "0", None) and kn is not None:
        for idx in faces:
            k1 = kn[idx[0] + 1].real                       # driven Robin uses Re k_n of the Active entry
            for i in idx:
                # 're' (default): Palace builds every mode's source/projection h from Re k_m, and the true H of a lossy
                # mode is (k_m / Re k_m) times it, so I = M⁻ᴴ [2 − (Re k1 / Re k_m) C]; 'complex': k1 / k_m (113's form)
                K[i, i] = k1 / (kn[i + 1].real if robin in (True, "1", "re") else kn[i + 1])
    D = 2 * np.eye(N) - K @ C
    TI = np.linalg.inv(M).conj().T
    I = TI @ D
    St = (V - Z0 * I) @ np.linalg.inv(V + Z0 * I)
    # self-checks: M block-diagonal (a terminal sees only its own face's modes), |T_V_ii|² vs Z_PV
    off = 0.0
    for a in faces:
        for b in faces:
            if a is not b:
                off = max(off, np.abs(M[np.ix_(a, b)]).max())
    info["M_offblock_rel"] = off / np.abs(M).max()
    info["TV_diag"] = np.diag(M @ G.T)
    info["G"] = G
    info["K"] = np.diag(K)
    info["colpower"] = None
    return St, info


def perm_to_ana(order):
    """order[k] = (line, end) of entry k, line 1/2, end 0 near / 1 far -> permutation to ana.py's port order
    (1 line-1 near, 2 line-1 far, 3 line-2 near, 4 line-2 far)."""
    pos = {(1, 0): 0, (1, 1): 1, (2, 0): 2, (2, 1): 3}
    p = [None] * len(order)
    for k, le in enumerate(order):
        p[pos[le]] = k
    return p


def svd_balance(S):
    sv = np.linalg.svd(S, compute_uv=False)
    bal = np.sum(np.abs(S) ** 2, axis=0)            # per excited port: |S_jj|² + Σ|S_ij|²
    return sv, bal


if __name__ == "__main__":
    d = sys.argv[1]
    data = read_run(d); kns = read_kn(d)
    faces = [[0, 1], [2, 3]]
    for fi, f in enumerate(sorted(data)):
        r = data[f]
        print(f"{f} GHz  Z_PV {np.round(r['Z'], 3).tolist()}  k_n {kns[fi] if fi < len(kns) else None}")
        sv = np.linalg.svd(r["S"], compute_uv=False)
        print(f"  raw modal σ {sv.max():.4f}/{sv.min():.4f}")
        for gram in ("none", "real", "complex"):
            St, info = transform(r["S"], r["V"], r["Z"], faces, kns[fi] if fi < len(kns) else None, gram=gram)
            sv, bal = svd_balance(St)
            gs = {k: v["g"] for k, v in info.items() if k.startswith("face")}
            print(f"  gram={gram:7s} σ {sv.max():.4f}/{sv.min():.4f} bal {np.round(bal, 4).tolist()} g {gs}")
