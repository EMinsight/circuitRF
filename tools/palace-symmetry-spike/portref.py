"""Combined S vs an ideal coupled line built from Palace's OWN 2D port solution (Z_PV and kn of each
half-model's port 1): isolates the route from the reference's physics (quasi-static, mesh)."""
import sys, re, os
from ana import *
Z0 = 50.0; ELL = 15e-3
ev, od = sys.argv[1], sys.argv[2]
def kn(run):
    out = {}; f = None
    for line in open(os.path.join(RUNS, run, "palace.log")):
        m = re.search(r"Port 1, mode 1: kₙ = ([0-9.]+e[+-][0-9]+)", line)
        if m: out.setdefault(len(out), float(m.group(1)))
    return out
for idx, f in enumerate(sorted(read(ev)[0])):
    mods = []
    for run in (ev, od):
        Sd, z = read(run); S = Sd[f]
        M = np.array([[S[(1, 1)], S[(2, 1)]], [S[(2, 1)], S[(1, 1)]]])
        k0 = 2 * np.pi * f * 1e9 / C0
        mods.append((renorm2(M, z[f][1], Z0), z[f][1], (kn(run)[idx] / k0) ** 2))
    Sc = combine(mods[0][0], mods[1][0])
    R = ref4(mods[0][1], mods[1][1], f, ELL, Z0, mods[0][2], mods[1][2])
    print(f"{f} GHz: port even {mods[0][1]:.3f} Ω / ε_eff {mods[0][2]:.4f}, odd {mods[1][1]:.3f} / {mods[1][2]:.4f}")
    print(compare(Sc, R, "vs ideal line from Palace's own port Z_PV and kn")[0])
