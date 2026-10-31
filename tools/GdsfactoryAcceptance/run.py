"""The gdsfactory acceptance matrix: every cell of one kit, through circuitRF headlessly, against gdsfactory's own GDS.

    <kit-python> -I tools/GdsfactoryAcceptance/run.py --module <m> --circuitrf <path-to-cli> --out <dir>
                 [--variations 2] [--cells <glob>] [--max-cells N] [--chunk 400] [--build-timeout 60]

Runs UNDER THE KIT'S OWN INTERPRETER, so the reference GDS is built in this process by the same gdsfactory
the kit runs on. circuitRF is driven only as a process, exactly as a user would drive it:

1. ``new workspace`` (no technology), then ``import pdk --module <m> --python <this interpreter>
   --allow-code --technology``. The import's own report is saved and parsed.
2. Per offered cell, the parameter sets: the defaults, then ONE parameter at a time — each real at ×0.5 and
   ×2 (``--variations 1``: ×2 only; ``0``: defaults only), each integer at default + 1, each flag flipped.
   Never a cross product. A set gdsfactory itself refuses is the kit's refusal and is not sent.
3. The reference: ``gf.get_component(name, **params).write_gds(…)`` here.
4. circuitRF's version: one ``.clay`` per cell placing one instance per set (a PCell snapshot each), a top
   ``.clay`` per chunk placing those, and ``convert <top>.clay -o <chunk>.gds`` — which rebuilds every
   placed kit cell through the worker before it writes. A chunk circuitRF refuses is split until the
   refusal names one set, so one bad cell costs nothing else.
5. The comparison, per layer, with gdsfactory's own boolean API: pass when the XOR area is at most
   (edge count × 1 DBU × 1 DBU). Where it is not, circuitRF's geometry is also compared with gdsfactory's
   in-process flattening: circuitRF equal to that, and that different from gdsfactory's own GDS, is
   gdsfactory disagreeing with itself. Pins: every port except ``placement`` has a pin of the same name,
   at the same position within 1 DBU, of the same width and outward angle.

Every failure is attributed to one of: ``gdsfactory or the kit`` (it fails in gdsfactory alone too),
``circuitRF`` (it builds in gdsfactory and fails or differs through circuitRF; any pin that differs is
circuitRF's), ``gdsfactory process state`` (see below), or ``out of scope`` (a decision of the series
excludes it).

**Order dependence is a column, not a guess.** The reference is built in a clean child process; circuitRF's
worker is one long-lived process that has built other cells first. For every set attributed to circuitRF, the
harness builds it once more in a child that first built every other offered cell at its defaults. When that
build alone reproduces the difference (it raises, its ports or geometry change), the set is attributed to
``gdsfactory process state`` — gdsfactory's own process-wide layout — and counted on its own line, so a
genuine circuitRF difference is never confused with it. A reference build whose process dies
without a result is neither side's: it is counted as not compared. Output: ``<out>/report.md`` and ``<out>/report.json``. Nothing is
written anywhere else. The harness names no kit: the module is an argument.
"""

from __future__ import annotations

import argparse
import fnmatch
import importlib
import inspect
import json
import os
import re
import shutil
import signal
import statistics
import subprocess
import sys
import time
from typing import Any

KIT = "gdsfactory or the kit"
CRF = "circuitRF"
SCOPE = "out of scope"
#: gdsfactory's process-wide layout: the set differs only once other cells were built in the same process.
STATE = "gdsfactory process state"
#: Not a verdict on either side: the harness's own reference build died (a fork-unsafe native library, say), so
#: nothing was compared. Counted on its own, never as the kit's refusal.
HARNESS = "unattributed (the harness)"

#: Positions of one cell's sets in its ``.clay``: set k sits at x = k mm. Only identity matters — each
#: placement is compared as its own structure — so overlap is harmless.
PITCH_DBU = 1_000_000

#: The marker that makes ``--out`` this harness's own directory, so a re-run may clear it.
MARKER = ".gdsfactory-acceptance"

NOT_OFFERED = re.compile(r"^\[circuitRF\] not offered \((?P<reason>.+?)\): (?P<cell>[^:]+): (?P<detail>.*)$")
UNBUILDABLE = re.compile(r"The generated cell '(?P<name>[^']+)' \(generator '[^']*'\) is not on disk and could not "
                         r"be rebuilt: (?P<why>.*?)(?: It is placed in |$)", re.M)
BUILT_IN = "named like a circuitRF built-in"

# ── circuitRF as a process ─────────────────────────────────────────────────────────────────────────


def run(cmd: list[str]) -> tuple[int, str, str, float]:
    t0 = time.perf_counter()
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, p.stdout, p.stderr, time.perf_counter() - t0


def cli(args: argparse.Namespace, *rest: str) -> list[str]:
    exe = args.circuitrf
    head = ["dotnet", exe] if exe.endswith(".dll") else [exe]
    return head + list(rest)


# ── the kit, in this process ───────────────────────────────────────────────────────────────────────


def find_pdk(module: Any, pdk_type: type) -> Any:
    named = getattr(module, "PDK", None)
    if isinstance(named, pdk_type):
        return named
    found = [v for v in vars(module).values() if isinstance(v, pdk_type)]
    if len(found) != 1:
        raise SystemExit(f"'{module.__name__}' has no PDK attribute and {len(found)} Pdk objects at module level.")
    return found[0]


def kind_of(default: Any) -> str | None:
    """bool before int — a bool is an int in Python."""
    if isinstance(default, bool):
        return "bool"
    if isinstance(default, int):
        return "int"
    if isinstance(default, float):
        return "real"
    if isinstance(default, str):
        return "string"
    return None


def signature_of(fn: Any) -> tuple[list[tuple[str, str, Any]], list[str]] | None:
    """(scalar parameters as (name, kind, default), required parameters), or None when unreadable."""
    try:
        sig = inspect.signature(fn)
    except (TypeError, ValueError):
        return None
    scalars, required = [], []
    for p in sig.parameters.values():
        if p.kind in (p.VAR_POSITIONAL, p.VAR_KEYWORD):
            continue
        if p.default is p.empty:
            required.append(p.name)
        elif (k := kind_of(p.default)) is not None:
            scalars.append((p.name, k, p.default))
    return scalars, required


def parameter_sets(scalars: list[tuple[str, str, Any]], variations: int) -> list[tuple[str, dict[str, Any]]]:
    """(label, the parameters that differ from the defaults). One at a time, deduplicated."""
    factors = {0: (), 1: (2.0,), 2: (0.5, 2.0)}[variations]
    sets: list[tuple[str, dict[str, Any]]] = [("defaults", {})]
    if variations == 0:
        return sets
    for name, kind, default in scalars:
        if kind == "real":
            for f in factors:
                v = default * f
                if v != default:
                    sets.append((f"{name}×{f:g}", {name: v}))
        elif kind == "int":
            sets.append((f"{name}+1", {name: default + 1}))
        elif kind == "bool":
            sets.append((f"{name}={not default}", {name: not default}))
    return sets


def snapshot_value(kind: str, value: Any) -> Any:
    """A PCell value as a ``.clay`` stores it: an integer is the one tagged kind."""
    if kind == "int":
        return {"int": int(value)}
    if kind == "real":
        return float(value)
    return value


#: Every comparison is made on circuitRF's grid: 1 DBU = 1 nm.
UM_PER_DBU = 0.001


def on_grid(kdb: Any, region: Any, dbu: float) -> Any:
    return region if abs(dbu - UM_PER_DBU) < 1e-15 else region.transformed(kdb.ICplxTrans(dbu / UM_PER_DBU))


def regions_of_gds(kdb: Any, path: str) -> dict[tuple[int, int], Any]:
    layout = kdb.Layout()
    layout.read(path)
    return regions_of_cell(kdb, layout, layout.top_cells()[0])


def regions_of_flat(kdb: Any, path: str) -> dict[tuple[int, int], Any]:
    """The in-process flattening :func:`build_isolated` wrote as text, holes and all, on circuitRF's grid."""
    regions: dict[tuple[int, int], Any] = {}
    with open(path, encoding="utf-8") as f:
        dbu = float(f.readline())
        for line in f:
            layer, datatype, text = line.rstrip("\n").split(" ", 2)
            regions.setdefault((int(layer), int(datatype)), kdb.Region()).insert(kdb.Polygon.from_s(text))
    return {k: on_grid(kdb, r, dbu) for k, r in regions.items()}


def regions_of_cell(kdb: Any, layout: Any, cell: Any) -> dict[tuple[int, int], Any]:
    out = {}
    for li in layout.layer_indexes():
        info = layout.get_info(li)
        r = kdb.Region(cell.begin_shapes_rec(li))
        if not r.is_empty():
            out[(info.layer, info.datatype)] = on_grid(kdb, r.merged(), layout.dbu)
    return out


def xor(kdb: Any, a: dict, b: dict) -> dict[str, dict[str, int]]:
    """Per layer: XOR area and the tolerance (edge count × 1 DBU²), only for layers that differ at all."""
    diffs = {}
    for key in sorted(set(a) | set(b)):
        ra, rb = a.get(key, kdb.Region()), b.get(key, kdb.Region())
        area = (ra ^ rb).area()
        if area:
            edges = ra.edges().count() + rb.edges().count()
            diffs[f"{key[0]}/{key[1]}"] = {"area": int(area), "tolerance": int(edges)}
    return diffs


def within(diffs: dict) -> bool:
    return all(d["area"] <= d["tolerance"] for d in diffs.values())


def gds_name(name: str) -> str:
    """circuitRF's GDSII structure-name spelling of a cell folder (GdsiiStructureNaming)."""
    return "".join(c if (c.isascii() and (c.isalnum() or c in "_?$")) else "_" for c in name[:200]) or "_"


# ── the matrix ─────────────────────────────────────────────────────────────────────────────────────


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--module", required=True)
    ap.add_argument("--circuitrf", required=True, help="the circuitRF CLI executable (or its .dll)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--variations", type=int, default=2, choices=(0, 1, 2))
    ap.add_argument("--cells", default="*", help="glob over cell names")
    ap.add_argument("--max-cells", type=int, default=0)
    ap.add_argument("--chunk", type=int, default=400, help="parameter sets per convert")
    ap.add_argument("--build-timeout", type=float, default=60,
                    help="seconds gdsfactory may take to build one set before it counts as the kit's refusal")
    args = ap.parse_args(argv)

    out = os.path.abspath(args.out)
    if os.path.isdir(out) and os.listdir(out) and not os.path.exists(os.path.join(out, MARKER)):
        print(f"'{out}' is not empty and is not this harness's output; choose another --out.", file=sys.stderr)
        return 2
    for sub in ("ws", "ref", "crf"):
        shutil.rmtree(os.path.join(out, sub), ignore_errors=True)
    os.makedirs(os.path.join(out, "ref"), exist_ok=True)
    os.makedirs(os.path.join(out, "crf"), exist_ok=True)
    open(os.path.join(out, MARKER), "w").close()
    ws = os.path.join(out, "ws")
    t_start = time.perf_counter()

    # 1. Import, consent, describe — circuitRF's.
    rc, so, se, _ = run(cli(args, "new", "workspace", ws, "--tech", "none"))
    if rc:
        print(se, file=sys.stderr)
        return 1
    rc, so, se, t_import = run(cli(args, "import", "pdk", "--module", args.module, "--python", sys.executable,
                                   "--into", ws, "--allow-code", "--technology"))
    with open(os.path.join(out, "import.txt"), "w", encoding="utf-8") as f:
        f.write(f"exit {rc}\n--- stdout\n{so}\n--- stderr\n{se}")
    if rc:
        print(f"import pdk failed (exit {rc}); see {out}/import.txt", file=sys.stderr)
        return 1
    report_reasons: dict[str, tuple[str, str]] = {}
    for line in se.splitlines():
        if m := NOT_OFFERED.match(line):
            report_reasons[m["cell"]] = (m["reason"], m["detail"])
    tally = next((l for l in so.splitlines() if "cell(s) offered" in l), "")
    tech_line = next((l for l in so.splitlines() if l.startswith("Technology built")), "")
    tech_warnings = sum(1 for l in se.splitlines() if l.startswith("warning:"))
    cell_lines = ("not offered (", "parameter(s) not offered", "placement port(s)", " is declared in ")
    tech_notes = sum(1 for l in se.splitlines() if l.startswith("[circuitRF]") and not any(c in l for c in cell_lines))
    with open(os.path.join(ws, f"{args.module}-pcells", "pcell-generators.json"), encoding="utf-8") as f:
        kit_root = json.load(f)["kitRoot"]

    # 2-3. The kit, here.
    import warnings
    warnings.filterwarnings("ignore")  # a kit's deprecation chatter, thousands of lines of it
    try:
        from loguru import logger
        logger.remove()
    except Exception:  # noqa: BLE001 - quieter is a convenience
        pass
    import gdsfactory as gf
    from gdsfactory.pdk import Pdk
    kdb = gf.kdb
    pdk = find_pdk(importlib.import_module(args.module), Pdk)
    pdk.activate()

    cells_all = sorted(pdk.cells)
    attribution_rows = []          # every cell not offered: the report's reason against the harness's
    offered: list[tuple[str, list[tuple[str, str, Any]]]] = []
    for cell in cells_all:
        sig = signature_of(pdk.cells[cell])
        if sig is None:
            mine, why = "no readable signature", ""
        elif sig[1]:
            mine, why = "required arguments", ", ".join(sig[1])
        else:
            # In a forked child under the same timeout as every set: a cell that never finishes at its defaults must
            # not stop the matrix before it has started.
            probe = build_isolated(gf, kdb, cell, {}, os.path.join(out, "ref", f"d{len(attribution_rows) + len(offered)}"),
                                   args.build_timeout)
            if "crash" in probe:
                mine, why = HARNESS, probe["crash"]
            elif "error" in probe:
                mine, why = ("did not finish building at its defaults" if probe.get("timeout")
                             else "raised while being built at its defaults"), probe["error"]
            else:
                mine, why = None, ""
        theirs = report_reasons.get(cell)
        if mine is None and theirs is None:
            offered.append((cell, sig[0]))
            continue
        if mine == HARNESS:
            agree, attribution = False, HARNESS
        elif theirs and theirs[0].startswith(BUILT_IN) and mine is None:
            agree, attribution = True, SCOPE
        else:
            agree = theirs is not None and mine is not None and theirs[0] == mine
            attribution = KIT if agree else CRF
        attribution_rows.append({"cell": cell, "report": theirs[0] if theirs else None,
                                 "reportDetail": theirs[1] if theirs else None, "harness": mine,
                                 "harnessDetail": why, "agree": agree, "attribution": attribution})

    chosen = [c for c in offered if fnmatch.fnmatchcase(c[0], args.cells)]
    if args.max_cells:
        chosen = chosen[: args.max_cells]

    rows: list[dict[str, Any]] = []
    units: list[dict[str, Any]] = []   # the sets sent to circuitRF
    for ci, (cell, scalars) in enumerate(chosen):
        kinds = {n: k for n, k, _ in scalars}
        defaults = {n: d for n, _, d in scalars}
        for k, (label, delta) in enumerate(parameter_sets(scalars, args.variations)):
            row = {"cell": cell, "set": label, "params": delta}
            rows.append(row)
            ref_path = os.path.join(out, "ref", f"c{ci}s{k}")
            built = build_isolated(gf, kdb, cell, delta, ref_path, args.build_timeout)
            if "crash" in built:
                row.update(outcome="not compared", attribution=HARNESS, detail=built["crash"][:300])
                continue
            if "error" in built:
                # The kit's refusal, recorded and not sent.
                row.update(outcome="kit refused", attribution=KIT, detail=built["error"][:300])
                continue
            row["gfMs"] = built["ms"]
            ports = built["ports"]
            values = {n: snapshot_value(kinds[n], delta.get(n, defaults[n])) for n in kinds}
            units.append({"row": row, "ci": ci, "k": k, "cell": cell, "values": values,
                          "ref": ref_path + ".gds", "flat": regions_of_flat(kdb, ref_path + ".flat.txt"),
                          "ports": ports, "snap": f"c{ci}s{k}",
                          "rebuild": lambda cell=cell, delta=delta, at=ref_path + ".again": rebuilt(
                              gf, kdb, cell, delta, at, args.build_timeout)})

    # 4. circuitRF's version, a chunk at a time; a refused chunk is split until the refusal names one set.
    gen_root = os.path.join(ws, ".generated-cells")
    pending = [units[i:i + args.chunk] for i in range(0, len(units), args.chunk)]
    attempt = 0
    gen_ms: list[float] = []
    convert_s = 0.0
    while pending:
        chunk = pending.pop(0)
        attempt += 1
        top = f"acc_top{attempt}"
        by_cell: dict[int, list[dict]] = {}
        for u in chunk:
            by_cell.setdefault(u["ci"], []).append(u)
        for ci, us in by_cell.items():
            write_clay(ws, f"acc{ci}", {u["snap"]: {"GeneratorId": u["cell"], "Parameters": u["values"]} for u in us},
                       [(f"../../.generated-cells/{u['snap']}", u["k"] * PITCH_DBU) for u in us])
        write_clay(ws, top, {}, [(f"../../acc{ci}", 0) for ci in by_cell])
        gds = os.path.join(out, "crf", f"{top}.gds")
        before = set(os.listdir(gen_root)) if os.path.isdir(gen_root) else set()
        # The verb FIRST: the installed circuitRF executable hands its arguments to the command line only when the
        # first one is a verb, and otherwise starts the application — which would wait here for ever.
        rc, so, se, secs = run(cli(args, "convert", os.path.join(ws, top, "layout", f"{top}.clay"), "-o", gds,
                                   "--python", sys.executable, "--trust-kit", kit_root))
        convert_s += secs
        if rc != 0:
            named = {m["name"]: m["why"] for m in UNBUILDABLE.finditer(se)}
            hit = [u for u in chunk if u["snap"] in named]
            for u in hit:
                u["row"].update(outcome="circuitRF refused", attribution=CRF, detail=named[u["snap"]][:300])
            rest = [u for u in chunk if u["snap"] not in named]
            if hit and rest:
                pending.insert(0, rest)
            elif not hit and len(chunk) > 1:
                pending[0:0] = [chunk[: len(chunk) // 2], chunk[len(chunk) // 2:]]
            elif not hit:
                why = " ".join(l for l in se.splitlines() if l.startswith("error:"))[:300] or f"convert exited {rc}"
                chunk[0]["row"].update(outcome="circuitRF refused", attribution=CRF, detail=why)
            continue
        gen_ms += generate_times(gen_root, before)
        unresolved = [l for l in se.splitlines() if "unresolved instance reference" in l]
        compare_chunk(kdb, gds, gen_root, chunk, unresolved)

    # 4b. Order dependence, for every set still charged to circuitRF.
    offered_names = [c for c, _ in offered]
    by_row = {id(u["row"]): u for u in units}
    for row in rows:
        if row.get("attribution") != CRF or id(row) not in by_row:
            continue
        u = by_row[id(row)]
        warm = build_isolated(gf, kdb, row["cell"], row["params"], os.path.join(out, "ref", u["snap"] + ".warm"),
                              max(args.build_timeout, 900), warm=offered_names)
        if "crash" in warm:
            row["afterOthers"] = "not built"
        elif "error" in warm:
            row["afterOthers"] = "gdsfactory refuses it too"
        elif warm["ports"] != u["ports"] or not within(xor(kdb, u["flat"], regions_of_flat(kdb, os.path.join(
                out, "ref", u["snap"] + ".warm.flat.txt")))):
            row["afterOthers"] = "gdsfactory's own build differs too"
        else:
            row["afterOthers"] = "same as alone"
        if row["afterOthers"] in ("gdsfactory refuses it too", "gdsfactory's own build differs too"):
            row["attribution"] = STATE

    # 5. The report.
    write_report(args, out, rows, attribution_rows, {
        "cellsInKit": len(cells_all), "offered": len(offered), "tested": len(chosen), "tally": tally,
        "technology": tech_line, "technologyNotes": tech_notes, "technologyWarnings": tech_warnings,
        "importSeconds": round(t_import, 2), "convertSeconds": round(convert_s, 2), "converts": attempt,
        "generateMsP50": round(statistics.median(gen_ms), 1) if gen_ms else None,
        "generateMsP95": round(sorted(gen_ms)[int(0.95 * (len(gen_ms) - 1))], 1) if gen_ms else None,
        "totalSeconds": round(time.perf_counter() - t_start, 1),
        "gdsfactory": getattr(gf, "__version__", ""), "python": sys.version.split()[0]})
    failing = sum(1 for r in rows if r.get("attribution") == CRF) + sum(1 for a in attribution_rows if a["attribution"] == CRF)
    print(f"{out}/report.md — {failing} failure(s) attributed to circuitRF")
    return 1 if failing else 0


def build_isolated(gf: Any, kdb: Any, cell: str, delta: dict, prefix: str, timeout: float,
                   fresh: bool = False, warm: list[str] | None = None) -> dict:
    """Build one set in gdsfactory and write ``<prefix>.gds`` (gdsfactory's own GDS) and ``<prefix>.flat.txt``
    (its in-process flattening), returning ``{"ms", "ports"}`` or ``{"error"}``.

    **The flattening is kept as polygon text, not GDS.** GDS cannot hold a polygon with a hole: writing one
    cuts it into a keyhole, and the cut's end point is rounded onto the grid, which moves the outline by a
    sliver. Measured on one kit's ring-shaped cells: gdsfactory's own GDS differs from its own polygons by
    just over the XOR tolerance, and circuitRF's export matches the polygons exactly. Written as GDS, the
    flattening carried the same sliver and could not tell the two apart.

    **In a forked child, killed at ``timeout`` seconds.** A gdsfactory build can loop without end on one
    parameter value inside compiled code (measured on one kit: a ring width halved), where no Python signal
    reaches it; a set that never builds is the kit's refusal, not a reason for the matrix to stop. Forking
    after the kit is imported costs milliseconds. Where there is no fork, the build runs here, unlimited.
    ``fresh`` clears gdsfactory's cell cache first, so a second build is a second build. ``warm`` names cells
    built at their defaults first, in the same child — the state circuitRF's worker generates from.
    """
    result_path = prefix + ".json"
    try:
        os.remove(result_path)  # a result left by an earlier build must never stand in for this one's
    except OSError:
        pass

    def build() -> dict:
        try:
            if fresh:
                gf.clear_cache()
            for other in warm or []:
                try:
                    gf.get_component(other)
                except Exception:  # noqa: BLE001 - a cell that will not build leaves its trace or none
                    pass
            t0 = time.perf_counter()
            comp = gf.get_component(cell, **delta)
            lines = [repr(float(comp.kcl.dbu))]
            for key, polys in comp.get_polygons(by="tuple").items():
                lines += [f"{int(key[0])} {int(key[1])} {poly.to_s()}" for poly in polys]
            ms = round((time.perf_counter() - t0) * 1000, 1)
            with open(prefix + ".flat.txt", "w", encoding="utf-8") as f:
                f.write("\n".join(lines) + "\n")
            comp.write_gds(prefix + ".gds")
            ports = [{"name": str(p.name), "x": p.center[0], "y": p.center[1], "width": float(p.width),
                      "deg": float(p.orientation if p.orientation is not None else 0.0) % 360}
                     for p in comp.ports if str(p.port_type) != "placement"]
            return {"ms": ms, "ports": ports}
        except Exception as exc:  # noqa: BLE001 - the kit's own message is the report
            return {"error": f"{type(exc).__name__}: {exc}".splitlines()[0]}

    if not hasattr(os, "fork"):
        return build()
    pid = os.fork()
    if pid == 0:
        try:
            with open(result_path, "w", encoding="utf-8") as f:
                json.dump(build(), f)
        finally:
            os._exit(0)
    deadline = time.monotonic() + timeout
    while True:
        done, status = os.waitpid(pid, os.WNOHANG)
        if done:
            break
        if time.monotonic() > deadline:
            os.kill(pid, signal.SIGKILL)
            os.waitpid(pid, 0)
            return {"error": f"gdsfactory did not finish building it within {timeout:g} s", "timeout": True}
        time.sleep(0.002)
    try:
        with open(result_path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        # The child died without writing: a crash of the build PROCESS, which says nothing about the kit — a
        # library that is unsafe to fork dies exactly like this. Recorded apart from the kit's refusals.
        how = f"signal {os.WTERMSIG(status)}" if os.WIFSIGNALED(status) else f"exit {os.WEXITSTATUS(status)}"
        return {"crash": f"the reference build process ended without a result ({how})"}


def rebuilt(gf: Any, kdb: Any, cell: str, delta: dict, prefix: str, timeout: float) -> dict:
    """The set built afresh, as flattened regions — gdsfactory's cell cache cleared first, or it would hand
    back the first build. A second build that fails counts as different."""
    again = build_isolated(gf, kdb, cell, delta, prefix, timeout, fresh=True)
    return {} if "error" in again or "crash" in again else regions_of_flat(kdb, prefix + ".flat.txt")


def write_clay(ws: str, name: str, snapshots: dict, instances: list[tuple[str, int]]) -> None:
    layout = os.path.join(ws, name, "layout")
    os.makedirs(layout, exist_ok=True)
    doc = {"FormatVersion": 1, "DbuPerMicron": 1000, "PCellSnapshots": snapshots or None, "Shapes": [],
           "Instances": [{"CellRef": ref, "X": x, "Y": 0} for ref, x in instances]}
    if not snapshots:
        del doc["PCellSnapshots"]
    with open(os.path.join(layout, f"{name}.clay"), "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)


def generate_times(gen_root: str, before: set[str]) -> list[float]:
    """Per-cell generate time, from the order the convert wrote the new cells: the gap between one cell's
    layout and the next. The first gap of a run includes starting the worker and is left out."""
    stamps = []
    for name in set(os.listdir(gen_root)) - before if os.path.isdir(gen_root) else []:
        layout = os.path.join(gen_root, name, "layout", f"{name}.clay")
        if os.path.exists(layout):
            stamps.append(os.path.getmtime(layout))
    stamps.sort()
    return [(b - a) * 1000 for a, b in zip(stamps, stamps[1:])]


def compare_chunk(kdb: Any, gds: str, gen_root: str, chunk: list[dict], unresolved: list[str]) -> None:
    layout = kdb.Layout()
    layout.read(gds)
    folders = {gds_name(n): n for n in os.listdir(gen_root)} if os.path.isdir(gen_root) else {}
    placed: dict[tuple[int, int], Any] = {}
    for ci in {u["ci"] for u in chunk}:
        acc = layout.cell(gds_name(f"acc{ci}"))
        for inst in acc.each_inst() if acc else []:
            placed[(ci, round(inst.trans.disp.x / PITCH_DBU))] = layout.cell(inst.cell_index)

    for u in chunk:
        row = u["row"]
        cell = placed.get((u["ci"], u["k"]))
        if cell is None:
            row.update(outcome="missing", attribution=CRF,
                       detail=("; ".join(unresolved) or "the exported file does not place this set")[:300])
            continue
        ours = regions_of_cell(kdb, layout, cell)
        ref = regions_of_gds(kdb, u["ref"])
        diffs = xor(kdb, ref, ours)
        row["xor"] = diffs
        row["xorArea"] = sum(d["area"] for d in diffs.values())
        row["pins"] = pin_diff(u["ports"], os.path.join(gen_root, folders.get(cell.name, cell.name)))
        if within(diffs) and not row["pins"]:
            row.update(outcome="match", attribution=None)
        elif row["pins"]:
            # Pins come from circuitRF's reading of the ports gdsfactory reported for this very build, so a pin
            # that differs is circuitRF's whatever the geometry did — the geometry branches below never excuse it.
            row.update(outcome="differs", attribution=CRF)
        elif not within(diffs) and within(xor(kdb, u["flat"], ours)) and not within(xor(kdb, u["flat"], ref)):
            # circuitRF drew what gdsfactory's own flattening returns; gdsfactory's own GDS is different.
            row.update(outcome="differs", attribution=KIT,
                       detail="gdsfactory's flattening and gdsfactory's own GDS disagree; circuitRF matches the flattening")
        elif not within(diffs) and not within(xor(kdb, u["flat"], u["rebuild"]())):
            # The same set, built again by gdsfactory now, is not what it built then: the cell draws something
            # other than its parameters (a date, a clock), so no second build of it can be compared.
            row.update(outcome="differs", attribution=KIT,
                       detail="not a function of its parameters: gdsfactory's own two builds of this set differ")
        else:
            row.update(outcome="differs", attribution=CRF)


def pin_diff(ports: list[dict], folder: str) -> list[str]:
    try:
        clay = next(os.path.join(folder, "layout", f) for f in os.listdir(os.path.join(folder, "layout")) if f.endswith(".clay"))
        with open(clay, encoding="utf-8") as f:
            pins = {p["Name"]: p for p in json.load(f).get("Pins") or []}
    except (OSError, StopIteration, ValueError):
        return [f"no generated layout in {os.path.basename(folder)}"]
    problems = []
    for port in ports:
        pin = pins.pop(port["name"], None)
        if pin is None:
            problems.append(f"{port['name']}: no pin")
            continue
        dx, dy = pin["X"] - port["x"] * 1000, pin["Y"] - port["y"] * 1000
        if abs(dx) > 1 or abs(dy) > 1:
            problems.append(f"{port['name']}: at ({pin['X']}, {pin['Y']}), port at ({port['x']*1000:g}, {port['y']*1000:g})")
        if abs(pin["WidthDbu"] - port["width"] * 1000) > 1:
            problems.append(f"{port['name']}: width {pin['WidthDbu']}, port {port['width']*1000:g}")
        if abs((pin["OutwardDeg"] - port["deg"] + 180) % 360 - 180) > 1e-6:
            problems.append(f"{port['name']}: outward {pin['OutwardDeg']}°, port {port['deg']:g}°")
    problems += [f"{name}: a pin with no port" for name in pins]
    return problems


def write_report(args: argparse.Namespace, out: str, rows: list[dict], attribution_rows: list[dict], summary: dict) -> None:
    def count(pred) -> int:
        return sum(1 for r in rows if pred(r))
    summary.update(
        sets=len(rows), matched=count(lambda r: r.get("outcome") == "match"),
        kitRefused=count(lambda r: r.get("outcome") == "kit refused"),
        notCompared=count(lambda r: r.get("attribution") == HARNESS),
        failingKit=count(lambda r: r.get("outcome") not in (None, "match", "kit refused") and r.get("attribution") == KIT),
        failingCircuitRF=count(lambda r: r.get("attribution") == CRF),
        failingProcessState=count(lambda r: r.get("attribution") == STATE),
        notOffered={}, notOfferedDisagreements=sum(1 for a in attribution_rows if not a["agree"]))
    for a in attribution_rows:
        key = a["report"] or "(not in the report)"
        summary["notOffered"][key] = summary["notOffered"].get(key, 0) + 1
    with open(os.path.join(out, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"module": args.module, "summary": summary, "notOffered": attribution_rows, "rows": rows}, f, indent=1)

    s = summary
    md = [f"# gdsfactory acceptance — `{args.module}`", "",
          f"gdsfactory {s['gdsfactory']}, Python {s['python']}, variations {args.variations}.", "",
          "| | |", "|---|---|",
          f"| Cells in the kit | {s['cellsInKit']} |", f"| Offered by circuitRF | {s['offered']} ({s['tally']}) |",
          f"| Cells tested | {s['tested']} |", f"| Parameter sets | {s['sets']} |",
          f"| Matched | {s['matched']} |", f"| Refused by gdsfactory (not sent) | {s['kitRefused']} |",
          f"| Failing, gdsfactory or the kit | {s['failingKit']} |",
          f"| **Failing, circuitRF** | **{s['failingCircuitRF']}** |",
          f"| Failing only after other cells were built in one process (gdsfactory process state) | {s['failingProcessState']} |",
          f"| Not compared: the reference build process crashed | {s['notCompared']} |",
          f"| Not-offered reasons disagreeing with the harness | {s['notOfferedDisagreements']} |",
          f"| Technology | {s['technology'] or '—'} ({s['technologyNotes']} notes, {s['technologyWarnings']} warnings) |",
          f"| Import (describe) | {s['importSeconds']} s |",
          f"| Generate per cell, p50 / p95 | {s['generateMsP50']} / {s['generateMsP95']} ms |",
          f"| convert, {s['converts']} run(s) | {s['convertSeconds']} s |", f"| Total | {s['totalSeconds']} s |", "",
          "## Not offered", "", "| Cell | Import report | Harness | Agree | Attribution |", "|---|---|---|---|---|"]
    md += [f"| {a['cell']} | {a['report'] or '—'} | {a['harness'] or 'builds'} | {'yes' if a['agree'] else '**no**'} "
           f"| {a['attribution']} |" for a in attribution_rows]
    md += ["", "## Every set that did not match", "",
           "| Cell | Set | Outcome | Attribution | After other cells | XOR area (DBU²) | Detail |",
           "|---|---|---|---|---|---|---|"]
    for r in rows:
        if r.get("outcome") == "match":
            continue
        detail = r.get("detail") or "; ".join(r.get("pins") or []) or ", ".join(
            f"{k}: {v['area']} > {v['tolerance']}" for k, v in (r.get("xor") or {}).items() if v["area"] > v["tolerance"])
        md.append(f"| {r['cell']} | {r['set']} | {r.get('outcome')} | {r.get('attribution') or '—'} "
                  f"| {r.get('afterOthers', '—')} | {r.get('xorArea', '—')} | {detail.replace('|', '/')} |")
    with open(os.path.join(out, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
