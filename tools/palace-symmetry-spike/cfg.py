"""Palace configs for brief-em3d-119, from 113-a's pair-a-per-line / pair-b-per-line configs.
Changes per run: which ports exist, which are excited, and the PMC / PEC attribute lists."""
import json, sys

def port(idx, attr, x, yc, zs, zg, exc):
    p = {"Index": idx, "Attributes": [attr], "Mode": 1, "Offset": 0.0, "MaxIts": 30,
         "KSPTol": 1e-08, "EigenTol": 1e-06,
         "VoltagePath": [[x, yc, zs], [x, yc, zg]], "NSamples": 100}
    if exc: p["Excitation"] = idx
    return p

def write(path, geom="A", mode="touch", freqs=(5.0,), excite=(1,), cut=None, wa=None, wb=None, shield=False, fullport=False):
    L = 15.0
    if geom == "A":
        S, W = 0.4, 1.2; wa = wa or W; wb = wb or W; zs, zg = -0.01, -1.0
    else:
        S, W = 0.3, 1.1; wa = wa or W; wb = wb or W; zs, zg = 0.508, 0.0
    ya, yb = -(S / 2 + wa / 2), S / 2 + wb / 2
    ports = [port(1, 4, 0.0, ya, zs, zg, 1 in excite), port(2, 5, L, ya, zs, zg, 2 in excite)]
    if mode != "half":
        ports += [port(3, 6, 0.0, yb, zs, zg, 3 in excite), port(4, 7, L, yb, zs, zg, 4 in excite)]
    bnd = {"WavePort": ports}
    pec = [10, 11] if geom == "A" else ([8, 11] if fullport else [8, 9, 11])
    pmc = []
    if mode == "gap": pmc.append(12)
    if mode == "half":
        (pmc if cut == "PMC" else pec).append(13)
    if geom == "B" and shield: pass
    bnd["PEC"] = {"Attributes": pec}
    if pmc: bnd["PMC"] = {"Attributes": pmc}
    if geom == "B" and not shield: bnd["Absorbing"] = {"Attributes": [10], "Order": 1}
    if geom == "B" and shield: pec.append(10)
    mats = [{"Attributes": [1], "Permeability": 1.0, "Permittivity": 1.0, "LossTan": 0.0}]
    if geom == "B": mats.append({"Attributes": [2], "Permeability": 1.0, "Permittivity": 3.5, "LossTan": 0.0})
    cfg = {
        "Problem": {"Type": "Driven", "Verbose": 2, "Output": "postpro"},
        "Model": {"Mesh": "mesh.msh", "L0": 0.001},
        "Domains": {"Materials": mats},
        "Boundaries": bnd,
        "Solver": {"Order": 2, "Device": "CPU",
                   "Driven": {"Samples": [{"Type": "Point", "Freq": list(freqs)}], "Save": []},
                   "Linear": {"Type": "Default", "KSPType": "GMRES", "Tol": 1e-08, "MaxIts": 200}}}
    json.dump(cfg, open(path, "w"), indent=1)

if __name__ == "__main__":
    write(sys.argv[1], **json.loads(sys.argv[2]))
