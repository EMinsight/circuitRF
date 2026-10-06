"""brief-em3d-124: a shared-face run through modal.py's transform, against a reference.

  shared.py <run> A                      symmetric air pair vs the 2D reference (101.95 / 70.885 Ω)
  shared.py <run> A <route-B even> <route-B odd>   ... and vs route B (119) on another run pair
  shared.py <run> asym <wb>              asymmetric air pair vs the exact multiconductor line (asym.py)
  shared.py <run> B <file.s4p>           microstrip pair vs a Touchstone 4-port (openEMS, ana.py port order)
  shared.py <run> B portref              ... vs an ideal line from Palace's own k_n / Z_PV (symmetric B only)
Options (env): GRAM=real|complex|none, ROBIN=re|complex|0, Z0=50. Terminal order is ana.py's: 1 line-1 near, 2 line-1
far, 3 line-2 near, 4 line-2 far. A run's entries are 1/2 near (line 1, line 2), 3/4 far.
"""
import os, sys
import numpy as np
from ana import ref4, combine, renorm2, compare, C0, RUNS
from modal import read_run, read_kn, transform, perm_to_ana, svd_balance

Z0 = float(os.environ.get("Z0", 50.0)); ELL = 15e-3
GRAM = os.environ.get("GRAM", "real"); ROBIN = os.environ.get("ROBIN", "re")
ORDER = [(1, 0), (2, 0), (1, 1), (2, 1)]
NAMES16 = {"S11": (0, 0), "S21": (1, 0), "S31": (2, 0), "S41": (3, 0), "S33": (2, 2), "S43": (3, 2),
           "S13": (0, 2), "S23": (1, 2)}


def full_compare(S, R, label):
    lines = [f"  -- {label}"]
    for n, (i, j) in NAMES16.items():
        a, r = S[i, j], R[i, j]
        ddb = 20 * np.log10(abs(a) / abs(r)); dph = (np.degrees(np.angle(a / r)) + 180) % 360 - 180
        lines.append(f"  {n} {20*np.log10(abs(a)):8.3f} dB {np.degrees(np.angle(a)):8.2f}°  ref {20*np.log10(abs(r)):8.3f} "
                     f"dB {np.degrees(np.angle(r)):8.2f}°  Δ {ddb:+.3f} dB {dph:+.2f}°  |ΔS| {abs(a-r):.4f}")
    dS = np.abs(S - R)
    lines.append(f"  max|ΔS| over all 16 {dS.max():.4f}")
    return "\n".join(lines), dS.max()


def terminal(run, f_sel=None):
    d = run if os.path.isdir(run) else os.path.join(RUNS, run)
    data = read_run(d); kns = read_kn(d); out = {}
    for fi, f in enumerate(sorted(data)):
        r = data[f]
        St, info = transform(r["S"], r["V"], r["Z"], [[0, 1], [2, 3]], kns[fi] if fi < len(kns) else None,
                             gram=GRAM, robin=ROBIN, Z0=Z0)
        p = perm_to_ana(ORDER)
        out[f] = (St[np.ix_(p, p)], info, r, kns[fi] if fi < len(kns) else None)
    return out


def routeB(ev, od, f):
    """119's route B: two half-model runs (port 1 excited, port 2 by the end-to-end mirror), each renormalised
    from its own Z_PV to Z0, combined by §3."""
    mods = []
    for rr in (ev, od):
        d = rr if os.path.isdir(rr) else os.path.join(RUNS, rr)
        r = read_run(d)[f]; S2 = r["S"]
        mods.append(renorm2(np.array([[S2[0, 0], S2[1, 0]], [S2[1, 0], S2[0, 0]]]), r["Z"][0], Z0))
    return combine(*mods)


def report(run, refs):
    for f, (S, info, r, kn) in terminal(run).items():
        sv_m = np.linalg.svd(r["S"], compute_uv=False)
        sv, bal = svd_balance(S)
        bal_m = np.sum(np.abs(r["S"]) ** 2, axis=0)
        print(f"{run} @ {f} GHz  Z_PV {np.round(r['Z'], 3).tolist()}")
        print(f"  k_n {kn}")
        print(f"  modal: σ {sv_m.max():.4f}/{sv_m.min():.4f}  column power {np.round(bal_m, 4).tolist()}")
        for k in ("face0", "face1"):
            if k in info:
                print(f"  {k}: g {info[k]['g']:.5f}  resid {info[k]['resid']:.1e}  roots {np.round(info[k]['roots'], 4).tolist()}")
        print(f"  K {np.round(info['K'], 5).tolist()}  M off-block {info['M_offblock_rel']:.1e}")
        print(f"  terminal: σ {sv.max():.5f}/{sv.min():.5f}  per-port power {np.round(bal, 5).tolist()}  "
              f"recip {np.linalg.norm(S - S.T) / np.linalg.norm(S):.1e}")
        for label, fn in refs:
            R = fn(f)
            if R is None: continue
            print(full_compare(S, R, label)[0])


if __name__ == "__main__":
    run, kind = sys.argv[1], sys.argv[2]
    refs = []
    if kind == "A":
        refs.append(("vs 2D Z0e/Z0o 101.95 / 70.885", lambda f: ref4(101.95, 70.885, f, ELL, Z0)))
        if len(sys.argv) > 4:
            ev, od = sys.argv[3], sys.argv[4]
            refs.append(("vs route B (119) " + os.path.basename(ev) + " + " + os.path.basename(od), lambda f: routeB(ev, od, f)))
    elif kind == "asym":
        import asym
        wb = float(sys.argv[3]); C = asym.exact(wb); Zc = np.linalg.inv(C) / C0
        print(f"exact Zc {np.round(Zc, 3).tolist()}")
        refs.append((f"vs exact asymmetric MTL (W2 {wb})", lambda f: asym.mtl4(Zc, f, ELL, Z0)))
    elif kind == "B":
        src = sys.argv[3]
        if src == "portref":
            pass
        else:
            from touch import read_snp
            T = read_snp(src)
            refs.append((f"vs {os.path.basename(src)}", lambda f: T.get(round(f, 6))))
    report(run, refs)
