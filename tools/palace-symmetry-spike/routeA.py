import sys
from ana import *
Z0 = 50.0; ELL = 15e-3; ZE, ZO = 101.95, 70.885
even_run, odd_run = sys.argv[1], sys.argv[2]
f = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0

def modal(run, sign, half=False):
    Sd, z = read(run); S = Sd[f]
    zp = z[f][1]
    if half:
        M = np.array([[S[(1, 1)], S[(2, 1)]], [S[(2, 1)], S[(1, 1)]]])
    else:
        M = modal_from_pair(fill4([S[(i, 1)] for i in (1, 2, 3, 4)]), sign)
    return M, zp

half = len(sys.argv) > 4 and sys.argv[4] == "half"
Me, ze = modal(even_run, +1, half); Mo, zo = modal(odd_run, -1, half)
print(f"{even_run} + {odd_run} @ {f} GHz: Z_PV even {ze:.3f} (2D {ZE}), odd {zo:.3f} (2D {ZO})")
S = combine(renorm2(Me, ze, Z0), renorm2(Mo, zo, Z0))
txt, w, sv = compare(S, ref4(ZE, ZO, f, ELL, Z0), "vs 2D Z0e/Z0o at 50 Ω")
print(txt)
txt, w, sv = compare(S, ref4(ze, zo, f, ELL, Z0), "vs ideal line at Palace's own Z_PV (exact-port reference)")
print(txt)
k0l = np.degrees(2 * np.pi * f * 1e9 / C0 * ELL)
for nm, M, zp in (("even", Me, ze), ("odd", Mo, zo)):
    zc, th = line_of(M, zp)
    print(f"  {nm} line: Zc {zc.real:.3f}{zc.imag:+.3f}j Ω  θ {th:.3f}° (k0ℓ {k0l:.3f}°)")
