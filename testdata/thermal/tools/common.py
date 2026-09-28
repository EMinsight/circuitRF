"""Shared helpers for the thermal reference scripts (brief-em3d-72). Spike material.

Nothing under src/ or tests/ may import, copy or call this file. It writes the reference data; the
test suite only ever READS what it wrote.

Conventions (R-em3d72-1c): SI in every file, temperatures in degrees Celsius, every CSV column headed
with its unit in square brackets, e.g. "x [m]" or "T [degC]".
"""
from __future__ import annotations

import csv
import json
import os
import platform
import sys

import numpy as np

KELVIN = 273.15
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)  # testdata/thermal


def versions() -> dict:
    """Tool versions, written into every JSON so a file says what produced it."""
    out = {"python": platform.python_version(), "numpy": np.__version__}
    try:
        import scipy
        out["scipy"] = scipy.__version__
    except ImportError:
        pass
    for mod in ("skfem", "meshio", "pyamg"):
        if mod in sys.modules:
            out[mod] = sys.modules[mod].__version__
    return out


def write_csv(path: str, header: list[str], rows, fmt: str = "%.12g", comment: str | None = None) -> None:
    """One CSV, header row with units, numbers in round-trippable short form. Strings pass through."""
    with open(path, "w", newline="\n") as f:
        if comment:
            for line in comment.strip().splitlines():
                f.write("# " + line + "\n")
        w = csv.writer(f, lineterminator="\n")
        w.writerow(header)
        for r in rows:
            w.writerow([(fmt % v) if isinstance(v, (float, np.floating)) else v for v in r])


def write_json(path: str, obj) -> None:
    def conv(o):
        if isinstance(o, np.floating):
            return float(o)
        if isinstance(o, np.integer):
            return int(o)
        if isinstance(o, np.ndarray):
            return o.tolist()
        raise TypeError(type(o))

    with open(path, "w", newline="\n") as f:
        json.dump(obj, f, indent=1, default=conv)
        f.write("\n")


# ---- the four wire metals (testdata/thermal/metals) ----------------------------------------------

METALS = ("gold", "copper", "aluminium", "silver")


def read_metal(name: str) -> dict:
    """The metal's table (T [degC], rho [Ohm m], k [W/(m K)]) and its constants, as metals/ wrote them."""
    base = os.path.join(ROOT, "metals")
    rows = []
    with open(os.path.join(base, f"{name}.csv")) as f:
        r = csv.reader(line for line in f if not line.startswith("#"))
        header = next(r)
        for row in r:
            rows.append([float(row[0]), float(row[1]), float(row[2])])
    a = np.array(rows)
    with open(os.path.join(base, "constants.json")) as f:
        const = json.load(f)[name]
    return {"T": a[:, 0], "rho": a[:, 1], "k": a[:, 2], **const}


def interp(xs: np.ndarray, ys: np.ndarray, x):
    """Piecewise-LINEAR interpolation in the table, clamped at the ends. This is the interpolation
    every table-driven reference here uses, and the one the solver must use to reproduce them."""
    return np.interp(x, xs, ys)


def interp_slope(xs: np.ndarray, ys: np.ndarray, x):
    """d/dx of interp() — the slope of the segment x lies in (the right segment at a knot)."""
    x = np.asarray(x, dtype=float)
    i = np.clip(np.searchsorted(xs, x, side="right") - 1, 0, len(xs) - 2)
    return (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i])
