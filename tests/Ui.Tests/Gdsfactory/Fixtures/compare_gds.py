"""The independent half of brief-gdf-2's Tier B check: gdsfactory against circuitRF, per layer.

    python compare_gds.py <module> <gds-dir> <cell> [<cell> ...]

For each cell, gdsfactory builds it at its defaults and writes its OWN GDS; circuitRF's GDS for the
same cell is ``<gds-dir>/<cell>.gds`` (written by ``GdsiiExport``). Both are read back through
gdsfactory's own layout-engine dependency (``gf.kdb``) and compared per ``(layer, datatype)`` as polygon SETS — an XOR of the two flattened regions must be
empty — never as bytes. Prints one JSON object: cell -> {"ok", "differs": [layer...], "ports": [...]}.
Ports are listed in circuitRF's database units (1 nm), so the caller can compare them with its pins.
"""

from __future__ import annotations

import importlib
import json
import os
import sys
import tempfile

import gdsfactory as gf

kdb = gf.kdb


def regions(path: str) -> tuple[dict[tuple[int, int], "kdb.Region"], float]:
    layout = kdb.Layout()
    layout.read(path)
    top = layout.top_cells()[0]
    out: dict[tuple[int, int], kdb.Region] = {}
    for index in layout.layer_indexes():
        info = layout.get_info(index)
        region = kdb.Region(top.begin_shapes_rec(index))
        if not region.is_empty():
            out[(info.layer, info.datatype)] = region.merged()
    return out, layout.dbu


def main(argv: list[str]) -> int:
    module, gds_dir, cells = argv[0], argv[1], argv[2:]
    pdk = getattr(importlib.import_module(module), "PDK")
    pdk.activate()

    result = {}
    with tempfile.TemporaryDirectory() as scratch:
        for cell in cells:
            component = gf.get_component(cell)
            ours = os.path.join(scratch, cell + ".gds")
            component.write_gds(ours)
            expected, dbu_a = regions(ours)
            actual, dbu_b = regions(os.path.join(gds_dir, cell + ".gds"))
            if abs(dbu_a - dbu_b) > 1e-12:
                scale = kdb.ICplxTrans(dbu_b / dbu_a)
                actual = {k: r.transformed(scale) for k, r in actual.items()}
            # Normalised: a difference that vanishes when shrunk by one database unit is a sliver of
            # rounding, not a shape. gdsfactory's OWN flattening and its OWN GDS of a ring disagree by
            # exactly such slivers (an off-grid reference rounded two ways), with circuitRF nowhere near.
            differs = [f"{l}/{d}" for (l, d) in sorted(set(expected) | set(actual))
                       if not (expected.get((l, d), kdb.Region()) ^ actual.get((l, d), kdb.Region()))
                              .sized(-1).is_empty()]
            ports = [{"name": str(p.name), "x": round(p.center[0] * 1000), "y": round(p.center[1] * 1000),
                      "width": round(p.width * 1000), "orientation": float(p.orientation or 0.0),
                      "type": str(p.port_type), "layer": [p.layer_info.layer, p.layer_info.datatype]}
                     for p in component.ports]
            result[cell] = {"ok": not differs, "differs": differs, "ports": ports}

    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
