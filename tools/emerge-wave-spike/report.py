"""brief-emw-1: one run's terminal S, by both transforms, against its case's references.

    <emerge python> report.py <run name> [more runs...]
"""
import json, os, sys
import numpy as np
from xform import csharp, exact, tl2, coupled4, read_snp, db, C0

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
np.set_printoptions(precision=4, suppress=True, linewidth=160)


def load(name):
    d = os.path.join(HERE, "runs", name)
    return np.load(os.path.join(d, "data.npy"), allow_pickle=True), json.load(open(os.path.join(d, "meta.json")))


def qs_line(h, er, strips, Y, zhi):
    """Quasi-static Z and eps_eff of each mode of the strips in the housing (2D Laplace, the 119/124 harness's solver)."""
    sys.path.insert(0, os.path.join(ROOT, "tools", "palace-symmetry-spike"))
    import qs2d
    C, Ca, _ = qs2d.solve(Y, zhi, h, er, 1e-4, strips, "PEC", "PEC", h0=0.01, hmax=0.1, ratio=1.1)
    if len(strips) == 1:
        c, ca = C[0, 0], Ca[0, 0]
        return 1 / (C0 * np.sqrt(c * ca)), c / ca
    return C, Ca


def faces_of(meta):
    out, k = [], 0
    for f in meta["faces"]:
        out.append(list(range(k, k + len(f["paths"]))))
        k += len(f["paths"])
    return out


def terminal_inputs(r, faces):
    """zpv (each entry's own mode on its own path, EMerge's 1 W normalisation: |T_V|^2 / 2) and kn, in entry order."""
    zpv, kn = [], []
    for fd, face in zip(r["faces"], faces):
        for a, _ in enumerate(face):
            zpv.append(abs(fd["TV"][a, a]) ** 2 / 2)
            kn.append(fd["beta"][a])
    return np.array(zpv), np.array(kn)


def both(r, faces, z0=50.0):
    S = r["S"] / r["p"][None, :]                  # undo EMerge's column power constants (1.0 when its modes carry 1 W)
    zpv, kn = terminal_inputs(r, faces)
    out = {}
    for gram in ("fit", "identity"):
        for robin in ("real", "source", "none"):
            out[f"C#[{gram},{robin}]"] = csharp(S, r["V"] / np.sqrt(2), zpv, kn, faces, z0, gram, robin)
    for robin in ("palace", "emerge", "none"):
        out[f"fields[{robin}]"] = exact(S, r["V"], r["faces"], faces, z0, robin)
    out["fields[emerge,no V]"] = exact(S, r["V"], r["faces"], faces, z0, "emerge", use="tv")
    return out, zpv, kn


def show(label, St, R, entries):
    cells = []
    for name, (i, j) in entries:
        a, b = St[i, j], R[i, j]
        cells.append(f"{name} {db(a):7.2f}/{db(b):7.2f} dB |dS| {abs(a - b):.4f}")
    worst = max(abs(St[i, j] - R[i, j]) for _, (i, j) in entries)
    print(f"    {label:22s} max|dS| {worst:.4f}  " + "  ".join(cells))
    return worst


def report(name):
    rows, meta = load(name)
    o = meta["options"]
    case = o["case"]
    faces = faces_of(meta)
    print(f"== {name}: case {case}, port {o['port']}, order {o['order']}, res {o['res']}, edge {o['edge']} mm, hmax {o.get('hmax')}; "
          f"{meta['tets']} tets, {meta['unknowns']} unknowns, mesh {meta['meshSeconds']} s, solve {meta['solveSeconds']} s")
    ref_snp = {}
    if case == "B":
        ref_snp["Palace"] = read_snp(os.path.join(ROOT, "testdata/em3d/terminal/coupled-microstrip/palace/CoupledMicrostrip.s4p"))
    if case == "A":
        zq, eq = qs_line(0.508, 2.2, [(-0.7875, 0.7875)], 4.5, 3.508)
        print(f"  quasi-static reference: Z {zq:.3f} ohm, eps_eff {eq:.4f}")
    for r in rows:
        f = r["f"]; fg = f / 1e9; k0 = 2 * np.pi * f / C0
        out, zpv, kn = both(r, faces)
        print(f"  {fg:g} GHz  beta/k0 {np.round(kn.real / k0, 4)}  Z_PV {np.round(zpv, 3)}  EMerge Z0 {[np.round(fd['z0'], 3).tolist() for fd in r['faces']]}  "
              f"W12 {[np.round(fd['W'][0, 1], 4) if fd['W'].shape[0] > 1 else None for fd in r['faces']]}  "
              f"Gp12 {[np.round(fd['Gp'][0, 1], 4) if fd['Gp'] is not None and fd['Gp'].shape[0] > 1 else None for fd in r['faces']]}  "
              f"beta_robin/k0 {[round(fd['beta_robin'].real / k0, 4) for fd in r['faces']]}")
        S = r["S"]
        sv = np.linalg.svd(S, compute_uv=False)
        print(f"    raw modal S: sigma {sv.max():.4f}/{sv.min():.4f}  |S - S^T| {np.abs(S - S.T).max():.2e}")
        if case in "AD":
            ell = (15e-3 if case == "A" else 10e-3)
            zr, er = (zq, eq) if case == "A" else (ETA0_COAX(), 2.1)
            refs = {"closed/qs line": tl2(zr, k0 * np.sqrt(er) * ell, 50.0),
                    "own Z_PV, beta": tl2(zpv[0], kn[0].real * ell, 50.0)}
            print(f"    modal thru: |S11| {db(S[0, 0]):.1f} dB, angle S21 {np.degrees(np.angle(S[1, 0])):.3f} deg vs -beta*l "
                  f"{-np.degrees(kn[0].real * ell) % 360 - 360:.3f}, closed-form {-np.degrees(k0 * np.sqrt(er) * ell) % 360 - 360:.3f}")
            for rn, R in refs.items():
                print(f"   against {rn}:")
                for k, (St, info) in out.items():
                    extra = f"tv {np.round(info['tv_mismatch'], 5)}" if "tv_mismatch" in info else f"V resid {info['V_resid']:.1e}"
                    show(k, St, R, [("S11", (0, 0)), ("S21", (1, 0)), ("S22", (1, 1))]); print(f"      {extra}")
        else:
            refs = {}
            if case == "C":
                th = k0 * np.sqrt(2.1) * 15e-3
                refs["Cohn"] = coupled4(57.511, 43.10, th, th, 50.0)
            for nm, snp in ref_snp.items():
                key = min(snp, key=lambda q: abs(q - fg))
                if abs(key - fg) < 1e-6:
                    refs[nm] = snp[key]
            ent = [("S11", (0, 0)), ("S22", (1, 1)), ("S21", (1, 0)), ("S31", (2, 0)), ("S41", (3, 0)), ("S42", (3, 1))]
            for rn, R in refs.items():
                print(f"   against {rn}:")
                for k, (St, info) in out.items():
                    show(k, St, R, ent)
                    if "fits" in info:
                        print(f"      G fits {[(d, round(float(np.real(g)), 4), round(res, 6)) for d, g, res in info['fits']]}  K {np.round(info['K'].real, 4)}  "
                              f"M off {info['M_off']:.1e}  tv {np.round(info['tv_mismatch'], 5)}")
                    else:
                        print(f"      V resid {info['V_resid']:.1e}")
            for k, (St, info) in out.items():
                sv = np.linalg.svd(St, compute_uv=False)
                print(f"    {k:22s} sigma {sv.max():.4f}/{sv.min():.4f}  |S-S^T| {np.abs(St - St.T).max():.1e}  "
                      f"col power {np.round(np.sum(np.abs(St) ** 2, 0), 4)}")


def ETA0_COAX():
    return 376.730313668 / (2 * np.pi * np.sqrt(2.1)) * np.log(1.34 / 0.4)


if __name__ == "__main__":
    for n in sys.argv[1:]:
        report(n)
