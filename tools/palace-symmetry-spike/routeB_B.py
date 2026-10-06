import sys
from ana import *
Z0 = 50.0; ELL = 15e-3
import os; ZE, EE, ZO, EO = [float(v) for v in os.environ.get("QSREF", "58.62,2.910,40.72,2.4285").split(",")]
ev, od = sys.argv[1], sys.argv[2]
for f in (2.0, 6.0):
    out = []
    for run in (ev, od):
        Sd, z = read(run); S = Sd[f]
        out.append((np.array([[S[(1, 1)], S[(2, 1)]], [S[(2, 1)], S[(1, 1)]]]), z[f][1]))
    (Me, ze), (Mo, zo) = out
    S4 = combine(renorm2(Me, ze, Z0), renorm2(Mo, zo, Z0))
    print(f"{f} GHz  Z_PV even {ze:.3f} (QS {ZE}), odd {zo:.3f} (QS {ZO})")
    print(compare(S4, ref4(ZE, ZO, f, ELL, Z0, EE, EO), "vs 2D quasi-static at 50 Ω")[0])
    k0l = np.degrees(2 * np.pi * f * 1e9 / C0 * ELL)
    for nm, M, zp, ee in (("even", Me, ze, EE), ("odd", Mo, zo, EO)):
        zc, th = line_of(M, zp, k0l * np.sqrt(ee))
        print(f"  {nm} line: Zc {zc.real:.3f} Ω  ε_eff {(th / k0l) ** 2:.4f} (QS {ee}; port kn from log)")
