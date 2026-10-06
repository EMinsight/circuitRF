"""brief-em3d-124: openEMS terminal S re-assembled from a `circuitrf em` run's probe files, as OpenEmsRun.ReadPort +
FdtdPortTransform.Solve do (U = the middle voltage plane, I = the mean of the two current planes, every probe DFT'd on
its own time column, S = (U − Z0 I)(U + Z0 I)⁻¹ over all runs), with the voltage taken as
  mean : the mean of the two halves (circuitRF's stripline terminal; must reproduce the .s4p)
  dn   : the strip -> ground half only (Palace's VoltagePath)
  up   : the strip -> lid half only
  probes.py <results dir ...openems | fixture probes.npz> <mean|dn|up> [f GHz ...]  -> prints S, and read() returns {f: S}"""
import os, sys
import numpy as np


def _dft(d, f):
    return np.sum(d[:, 1] * np.exp(-2j * np.pi * f * d[:, 0]))


def _source(run):
    """A run directory (p1..p4 of probe files) or a fixture's probes.npz (arrays p<j>_port<i>_<signal>)."""
    if run.endswith(".npz"):
        z = np.load(run)
        return lambda j, i, sig: z[f"p{j}_port{i}_{sig}"]
    return lambda j, i, sig: np.loadtxt(os.path.join(run, f"p{j}", f"port{i}_{sig}"), comments="%")


def read(run_dir, which="mean", freqs=(2e9, 4e9, 6e9), z0=50.0, n=4):
    out = {}; get = _source(run_dir)
    for f in freqs:
        U = np.zeros((n, n), complex); I = np.zeros((n, n), complex)
        for j in range(n):
            for i in range(n):
                up, dn = _dft(get(j + 1, i + 1, "u_up"), f), _dft(get(j + 1, i + 1, "u_dn"), f)
                U[i, j] = {"mean": (up + dn) / 2, "dn": dn, "up": up}[which]
                I[i, j] = (_dft(get(j + 1, i + 1, "ia"), f) + _dft(get(j + 1, i + 1, "ib"), f)) / 2
        out[round(f / 1e9, 6)] = (U - z0 * I) @ np.linalg.inv(U + z0 * I)
    return out


if __name__ == "__main__":
    fr = [float(v) * 1e9 for v in sys.argv[3:]] or [2e9, 6e9]
    for f, S in read(sys.argv[1], sys.argv[2], fr).items():
        print(f, np.round(20 * np.log10(np.abs(S[:, 0])), 3).tolist(), np.round(np.degrees(np.angle(S[:, 0])), 2).tolist())
