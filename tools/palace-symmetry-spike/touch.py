"""Minimal Touchstone v1 reader for circuitRF's .sNp (RI/MA/DB, any N): {f_GHz: NxN complex}."""
import re
import numpy as np


def read_snp(path):
    n = int(re.search(r"\.s(\d+)p$", path, re.I).group(1))
    unit, fmt, vals = 1e9, "MA", []
    scale = {"HZ": 1.0, "KHZ": 1e3, "MHZ": 1e6, "GHZ": 1e9}
    for line in open(path):
        line = line.split("!")[0].strip()
        if not line:
            continue
        if line.startswith("#"):
            tok = line[1:].upper().split()
            for t in tok:
                if t in scale: unit = scale[t]
                if t in ("RI", "MA", "DB"): fmt = t
            continue
        vals += [float(v) for v in line.split()]
    per = 1 + 2 * n * n
    out = {}
    for k in range(0, len(vals), per):
        rec = vals[k:k + per]; f = rec[0] * unit / 1e9; S = np.zeros((n, n), complex)
        for idx in range(n * n):
            a, b = rec[1 + 2 * idx], rec[2 + 2 * idx]
            # v1: for N >= 3 the matrix is written row by row (S11 S12 ... S1N / S21 ...); for N = 2, S11 S21 S12 S22
            i, j = (idx % n, idx // n) if n == 2 else (idx // n, idx % n)
            S[i, j] = a + 1j * b if fmt == "RI" else (a if fmt == "MA" else 10 ** (a / 20)) * np.exp(1j * np.radians(b))
        out[round(f, 6)] = S
    return out
