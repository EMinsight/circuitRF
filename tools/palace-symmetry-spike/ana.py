"""Read Palace port-S / port-Z, build modal 2-ports, renormalise, combine by brief §3, compare."""
import numpy as np, csv, os
C0 = 299792458.0
RUNS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "runs")

def read(run):
    d = os.path.join(RUNS, run, "postpro")
    rows = list(csv.reader(open(os.path.join(d, "port-S.csv"))))
    hdr = [h.strip() for h in rows[0]]
    out = {}
    for r in rows[1:]:
        v = [float(x) for x in r]; f = v[0]; S = {}
        for k, h in enumerate(hdr):
            if h.startswith("|S["):
                i, j = int(h[3]), int(h[6])
                S[(i, j)] = 10 ** (v[k] / 20) * np.exp(1j * np.radians(v[k + 1]))
        out[f] = S
    zr = list(csv.reader(open(os.path.join(d, "port-Z.csv"))))
    zh = [h.strip() for h in zr[0]]
    zpv = {}
    for r in zr[1:]:
        v = [float(x) for x in r]
        zpv[v[0]] = {int(h[8]): v[k] for k, h in enumerate(zh) if h.startswith("Re{Z_PV[")}
    return out, zpv

def renorm2(S, zm, z0):
    """2-port at real reference zm (both ports) -> z0 (power waves, real refs; RfCore's formula)."""
    I = np.eye(2); Z = zm * (I + S) @ np.linalg.inv(I - S)
    return (Z - z0 * I) @ np.linalg.inv(Z + z0 * I)

def tl2(zm, theta, z0):
    D = 2 * zm * z0 * np.cos(theta) + 1j * (zm ** 2 + z0 ** 2) * np.sin(theta)
    s11 = 1j * (zm ** 2 - z0 ** 2) * np.sin(theta) / D; s21 = 2 * zm * z0 / D
    return np.array([[s11, s21], [s21, s11]])

def combine(Se, So):
    """Even/odd 2-ports (ports near, far) -> 4-port, ports 1,2 line 1 near/far, 3,4 line 2 near/far."""
    S = np.zeros((4, 4), complex)
    line = {0: (0, 2), 1: (1, 3)}   # modal port index -> (terminal on line 1, terminal on line 2)
    for a in range(2):
        for b in range(2):
            p1, p3 = line[a]; q1, q3 = line[b]
            same = (Se[a, b] + So[a, b]) / 2; cross = (Se[a, b] - So[a, b]) / 2
            S[p1, q1] = S[p3, q3] = same; S[p1, q3] = S[p3, q1] = cross
    return S

def fill4(c):
    """Column 1 (S11,S21,S31,S41) of a doubly symmetric pair -> full 4-port (ports 1,2 line1; 3,4 line2)."""
    s11, s21, s31, s41 = c
    return np.array([[s11, s21, s31, s41], [s21, s11, s41, s31], [s31, s41, s11, s21], [s41, s31, s21, s11]])

def modal_from_pair(S4, sign):
    """Even (+1) or odd (-1) 2-port from a 4-port of a symmetric pair."""
    return np.array([[S4[0, 0] + sign * S4[0, 2], S4[0, 1] + sign * S4[0, 3]],
                     [S4[1, 0] + sign * S4[1, 2], S4[1, 1] + sign * S4[1, 3]]])

def ref4(ze, zo, f, ell, z0, eeff_e=1.0, eeff_o=1.0):
    k0 = 2 * np.pi * f * 1e9 / C0
    return combine(tl2(ze, k0 * np.sqrt(eeff_e) * ell, z0), tl2(zo, k0 * np.sqrt(eeff_o) * ell, z0))

NAMES = {"S11": (0, 0), "thru S21": (1, 0), "near S31": (2, 0), "far S41": (3, 0)}

def compare(S, R, label=""):
    lines = []; worst = 0
    for n, (i, j) in NAMES.items():
        a, r = S[i, j], R[i, j]
        ddb = 20 * np.log10(abs(a)) - 20 * np.log10(abs(r))
        dph = (np.degrees(np.angle(a / r)) + 180) % 360 - 180
        dS = abs(a - r); worst = max(worst, dS)
        lines.append(f"  {n:9s} {20*np.log10(abs(a)):8.3f} dB {np.degrees(np.angle(a)):8.2f}°  ref {20*np.log10(abs(r)):8.3f} dB {np.degrees(np.angle(r)):8.2f}°  "
                     f"Δ {ddb:+.3f} dB {dph:+.2f}°  |ΔS| {dS:.4f}")
    sv = np.linalg.svd(S, compute_uv=False)
    lines.append(f"  max|ΔS| {worst:.4f}  σ {sv.max():.4f}/{sv.min():.4f}  {label}")
    return "\n".join(lines), worst, sv

def line_of(M, zref, guess=None):
    """Characteristic impedance and electrical length (deg) of a symmetric 2-port at real reference zref."""
    I = np.eye(2); Z = zref * (I + M) @ np.linalg.inv(I - M)
    zc = np.sqrt(Z[0, 0] ** 2 - Z[1, 0] ** 2)
    th = np.degrees(np.arccos(complex(Z[0, 0] / Z[1, 0])).real)
    if guess is not None:   # arccos folds at 180°: take the branch nearest the expected length
        th = min((s * th + 360 * n for s in (1, -1) for n in range(-2, 3)), key=lambda v: abs(v - guess))
    return zc, th
