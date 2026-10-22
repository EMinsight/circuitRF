"""brief-emw-1 §4: a run's transform inputs as a committed fixture, so EMW-3's tests run the transform on EMerge-shaped data
with nothing installed.

    <emerge python> fixtures.py <run name> <fixture name>   ->  testdata/em3d/terminal/emerge-modal/<fixture name>.json

JSON, full precision (repr of every double), complex numbers as [re, im]. Per frequency, in EMerge's port-mode order
(face by face, mode by mode, which is terminal order: path k of a face carries mode k):
  S       the raw modal S (EMerge's own S corrections off; its column power constants were 1.0 and are not applied)
  V       each terminal path's line integral of the TOTAL E for each excitation, in EMerge's normalisation (modes carry 1 W,
          so |T_V|^2 = 2 Z_PV): ModalTerminalTransform takes V / sqrt(2)
  ZPv     each entry's own mode on its own path, |T_V,kk|^2 / 2
  Kn      each entry's mode wavenumber beta (rad/m); the face's first entry's is the one EMerge's port BC uses
  faces   per face, from the mode fields: TV (path k, mode n), W (EMerge's E-overlap projection), Gp (power Gram)
  expect  the terminal S of ModalTerminalTransform's arithmetic with G = Fit and the Robin form EMerge needs, K (2 - C)
"""
import json, os, sys
import numpy as np
from report import load, faces_of, terminal_inputs
from xform import csharp

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.abspath(os.path.join(HERE, "..", "..", "testdata", "em3d", "terminal", "emerge-modal"))


def c(a):
    a = np.asarray(a)
    if a.ndim == 0:
        return [float(a.real), float(a.imag)]
    return [c(x) for x in a]


def main(run, name):
    rows, meta = load(run)
    faces = faces_of(meta)
    o = meta["options"]
    samples = []
    for r in rows:
        S = r["S"] / r["p"][None, :]
        zpv, kn = terminal_inputs(r, faces)
        St, _ = csharp(S, r["V"] / np.sqrt(2), zpv, kn, faces, 50.0, "fit", "source")
        samples.append(dict(frequencyHz=float(r["f"]), S=c(S), V=c(r["V"]), ZPv=[float(z) for z in zpv], Kn=c(kn),
                            faces=[dict(TV=c(fd["TV"]), W=c(fd["W"]), Gp=c(fd["Gp"]) if fd["Gp"] is not None else None,
                                        betaRobin=c(fd["beta_robin"])) for fd in r["faces"]],
                            expect=c(St)))
    doc = dict(about="brief-emw-1 fixture; see README.md beside it", run=run, case=o["case"], emerge=meta["emerge"],
               options={k: v for k, v in o.items() if not k.startswith("_")}, tets=meta["tets"], unknowns=meta["unknowns"],
               faces=faces, z0=[50.0] * sum(len(f) for f in faces), normalisation="EMerge modes carry 1 W: pass V / sqrt(2)",
               robin="K (2 - C), K = Re k_first / Re k_n per face", samples=samples)
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, name + ".json"), "w") as f:
        json.dump(doc, f, indent=None, separators=(",", ":"))
    print(os.path.join(OUT, name + ".json"), os.path.getsize(os.path.join(OUT, name + ".json")), "bytes")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
