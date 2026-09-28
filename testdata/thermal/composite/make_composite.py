"""brief-em3d-72 S2 — three-layer composite slab with ONE interface resistance. Closed form.
Spike material: run by hand, never by the test suite.

    python make_composite.py   # writes composite.json, composite-S2a.csv, composite-S2b.csv
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from common import versions, write_csv, write_json  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# Layers listed BOTTOM to TOP (z up). The bottom face of layer 1 is fixed; the flux enters the top of
# layer 3. Lateral faces insulated, lateral size 1 mm x 1 mm.
LAYERS = [
    {"name": "base", "thickness_m": 1e-3, "k_W_mK": 390.0},     # copper-like
    {"name": "attach", "thickness_m": 50e-6, "k_W_mK": 57.0},   # solder-like
    {"name": "die", "thickness_m": 100e-6, "k_W_mK": 150.0},    # silicon-like
]
BASE = {"lateral_size_m": [1e-3, 1e-3], "T_bottom_degC": 25.0, "flux_top_W_m2": 1e6, "layers": LAYERS}
CASES = {
    # the resistance sits between "attach" (below) and "die" (above)
    "S2a": {**BASE, "interface": {"between": ["attach", "die"], "resistance_m2K_W": 1e-5}},
    # the same stack with no interface resistance: the jump must vanish, nothing else changes
    "S2b": {**BASE, "interface": {"between": ["attach", "die"], "resistance_m2K_W": 0.0}},
}


def profile(c):
    q = c["flux_top_W_m2"]
    z0, t0 = 0.0, c["T_bottom_degC"]
    pts = []  # (z, T, side) — side 'below'/'above' at an interface so the jump is two rows at one z
    faces = {}
    for i, layer in enumerate(c["layers"]):
        th, k = layer["thickness_m"], layer["k_W_mK"]
        if i > 0 and c["interface"]["between"] == [c["layers"][i - 1]["name"], layer["name"]]:
            jump = q * c["interface"]["resistance_m2K_W"]
            faces["interface_T_below_degC"] = t0
            faces["interface_T_above_degC"] = t0 + jump
            faces["interface_jump_K"] = jump
            t0 += jump
        z = np.linspace(z0, z0 + th, 41)
        t = t0 + q * (z - z0) / k
        pts += [(zz, tt, layer["name"]) for zz, tt in zip(z, t)]
        z0, t0 = z0 + th, t[-1]
    faces["T_top_degC"] = t0
    return pts, faces


def main():
    out = {"versions": versions(), "cases": {}}
    for name, c in CASES.items():
        pts, faces = profile(c)
        write_csv(os.path.join(HERE, f"composite-{name}.csv"), ["z [m]", "T [degC]", "layer"], pts,
                  comment="Each layer's end rows repeat the interface z: across a resistance the two rows differ by the jump.")
        out["cases"][name] = {**c, **faces}
    write_json(os.path.join(HERE, "composite.json"), out)
    print({k: {kk: v for kk, v in c.items() if kk.startswith(("T_", "interface_"))} for k, c in out["cases"].items()})


if __name__ == "__main__":
    main()
