"""brief-em3d-72 Q5 — write S5's condensed P2 stiffness matrices, at growing refinement, as raw binary
CSR for the C# harness (../harness). SPIKE MATERIAL. The matrices are large and are NOT committed: they
go to $THERMAL_SPIKE_MATRICES (default ~/opt/thermal-spike/matrices).

File layout, little-endian: int64 n, int64 nnz, int32 indptr[n+1], int32 indices[nnz], float64 data[nnz],
float64 b[n]. Both triangles are stored (the matrix is symmetric).

    python export_matrices.py      # writes s5-<rung>.bin and index.json there
"""
import json
import os
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))
sys.path.insert(0, os.path.join(HERE, "..", "..", "spreading"))
import fem  # noqa: E402
import make_spreading as S5  # noqa: E402

OUT = os.environ.get("THERMAL_SPIKE_MATRICES", os.path.expanduser("~/opt/thermal-spike/matrices"))
S5.SCRATCH = os.path.join(OUT, "mesh")
RUNGS = [(40, 250, 600), (20, 200, 500), (10, 160, 400), (5, 130, 330), (2.5, 110, 280), (1.25, 80, 220), (0.8, 60, 180), (0.6, 45, 150),
         (0.45, 36, 130), (0.35, 30, 110)]


def write_bin(path, A, b):
    A = A.tocsr()
    A.sort_indices()
    with open(path, "wb") as f:
        np.array([A.shape[0], A.nnz], dtype="<i8").tofile(f)
        A.indptr.astype("<i4").tofile(f)
        A.indices.astype("<i4").tofile(f)
        A.data.astype("<f8").tofile(f)
        np.asarray(b, dtype="<f8").tofile(f)


def export(tag, K, F, D, xD, extra):
    A, b, _, _ = fem.condensed(K, F[:, 0], D, xD)
    path = os.path.join(OUT, f"{tag}.bin")
    write_bin(path, A, b)
    return {"file": os.path.basename(path), "n": int(A.shape[0]), "nnz": int(A.nnz), **extra}


def main():
    os.makedirs(OUT, exist_ok=True)
    index = []
    for i, (hs, hm, d) in enumerate(RUNGS):
        t = time.time()
        _, (mesh, basis, u) = S5.fem_run(2, hs, hm, d, f"q5-{i}")
        # re-assemble the same system (fem_run solved it; the matrix is what is exported)
        sx, sy = S5.CASE["source_size_m"]
        ks = {f"layer{j + 1}": L["k_W_mK"] for j, L in enumerate(S5.CASE["layers_top_down"])}
        _, K, F, D, xD = fem.assemble(mesh, 2, ks, {"source": S5.CASE["power_W"] / (sx * sy)}, {"bottom": 0.0})
        e = export(f"s5-{i}", K, F, D, xD, {"case": "S5", "h_source_um": hs, "h_max_um": hm,
                                             "tetrahedra": int(mesh.t.shape[1])})
        index.append(e)
        print(e, "%.1fs" % (time.time() - t))
        json.dump(index, open(os.path.join(OUT, "index-s5.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
