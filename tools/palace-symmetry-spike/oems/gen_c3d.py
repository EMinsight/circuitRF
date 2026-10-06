"""brief-em3d-124: geometry B (microstrip pair) as a .c3d workspace for `circuitrf em`, matching gen.py's Palace mesh:
substrate εr 3.5 (LossTan as given), h 0.508, strips t 0.017 mm as PEC boxes on the substrate, ℓ 15 mm, 119's shielded
box (PEC sides ±4.25 mm, PEC ground, PEC lid at 3.508 mm). One two-terminal wave port per end, reference the ground.
Terminals numbered in ana.py's order: 1 line-1 near, 2 line-1 far, 3 line-2 near, 4 line-2 far.
  gen_c3d.py <workspace dir> <wa mm> <wb mm> <tan d> <cells per wavelength> <min cell µm> [freqs GHz, comma]"""
import json, os, sys
ws, wa, wb, tand, cpw, mincell = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]), float(sys.argv[5]), float(sys.argv[6])
freqs = [float(v) for v in (sys.argv[7] if len(sys.argv) > 7 else "2,4,6").split(",")]
S, h, t, L, Y, LID = 0.3, 0.508, 0.017, 15.0, 4.25, 3.508
U = 1000 * 1000           # DBU per mm (DbuPerMicron 1000)
D = lambda mm: int(round(mm * U))
os.makedirs(os.path.join(ws, "PairB", "3d"), exist_ok=True)
json.dump({"FormatVersion": 1, "Name": "pairB",
           "Materials": [{"Name": "PEC", "Sigma20": 1e30}, {"Name": "Sub", "Epsr": 3.5, "TanD": tand},
                         {"Name": "Air", "Epsr": 1}]}, open(os.path.join(ws, "tech.ctech"), "w"), indent=1)
json.dump({"DefaultTechRef": "tech.ctech"}, open(os.path.join(ws, ".cws"), "w"), indent=1)
open(os.path.join(ws, "PairB", ".ccell"), "w").write("{}")
box = lambda name, mat, x0, y0, z0, dx, dy, dz: {"$type": "Box", "Name": name, "Material": mat,
                                                "Min": [D(x0), D(y0), D(z0)], "Size": [D(dx), D(dy), D(dz)]}
nf = len(freqs)
doc = {
    "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 1000,
    "Objects": [box("substrate", "Sub", 0, -Y, 0, L, 2 * Y, h),
                box("air", "Air", 0, -Y, h, L, 2 * Y, LID - h),
                box("strip_a", "PEC", 0, -S / 2 - wa, h, L, wa, t),
                box("strip_b", "PEC", 0, S / 2, h, L, wb, t)],
    "Variables": [],
    "Ports": [{"Name": n, "Kind": "Wave", "Plane": "YZ", "Offset": D(x), "Rect": {"Min": [D(-Y), 0], "Size": [D(2 * Y), D(LID)]},
               "Reference": "airbox/zmin",
               "Terminals": [{"Number": a, "Name": f"P{a}", "Conductor": "strip_a", "Z0": "50"},
                             {"Number": b, "Name": f"P{b}", "Conductor": "strip_b", "Z0": "50"}]}
              for n, x, a, b in (("near", 0.0, 1, 3), ("far", L, 2, 4))],
    "Setups": [{
        "FormatVersion": 1, "Name": "openEMS",
        "Frequency": {"StartExpr": str(freqs[0]), "StopExpr": str(freqs[-1]), "StepExpr": "", "NumPoints": nf,
                      "Mode": "PointCount", "Kind": "Linear", "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz"},
        "Solver3D": "OpenEms",
        "AirBox": {"XMin": {"PaddingUm": 0, "Boundary": "Absorbing"}, "XMax": {"PaddingUm": 0, "Boundary": "Absorbing"},
                   "YMin": {"PaddingUm": 0, "Boundary": "Pec"}, "YMax": {"PaddingUm": 0, "Boundary": "Pec"},
                   "ZMin": {"PaddingUm": 0, "Boundary": "Pec"}, "ZMax": {"PaddingUm": 0, "Boundary": "Pec"}},
        "OpenEms": {"CellsPerWavelength": cpw, "MinCellUm": mincell}}],
}
json.dump(doc, open(os.path.join(ws, "PairB", "3d", "PairB.c3d"), "w"), indent=1)
print(os.path.join(ws, "PairB", "3d", "PairB.c3d"))
