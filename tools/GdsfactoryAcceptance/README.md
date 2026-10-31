# gdsfactory acceptance matrix

Every cell of one gdsfactory kit, through circuitRF **headless and end to end** — import, consent,
describe, generate, export — compared with **gdsfactory's own GDS** of the same cell at the same
parameters. A harness, not a test project: it needs a real kit in a real Python environment, and it
names no kit (the module is an argument). Brief: `docs/sonnet-briefs/brief-gdf-5-acceptance-matrix.md`.

```
<kit-python> -I tools/GdsfactoryAcceptance/run.py --module <m> --circuitrf <path-to-cli> --out <dir>
                                                  [--variations 2] [--cells <glob>] [--max-cells N] [--chunk 400] [--build-timeout 60]
```

- `<kit-python>` is the interpreter of the environment the kit is installed in. The harness builds the
  reference GDS in that process, and hands the same interpreter to circuitRF with `--python`.
- `<path-to-cli>` is a built circuitRF command line (`src/Cli/bin/Debug/net10.0/CircuitRF.Cli`, or the
  installed `circuitRF`; a `.dll` is run with `dotnet`).
- `--out` must be empty or a previous run's output. Everything is written there and nowhere else:
  `ws/` (the workspace), `ref/` (gdsfactory's GDS), `crf/` (circuitRF's), `import.txt`, `report.md`,
  `report.json`.
- `--variations`: 2 (the default) varies each real at ×0.5 and ×2; 1 at ×2 only; 0 runs the defaults
  alone. Integers go to default + 1 and flags flip whenever it is not 0. One parameter at a time, never
  a cross product.
- `--build-timeout`: seconds gdsfactory may spend building one set (default 60). A set that does not
  finish is the kit's refusal and is not sent — a gdsfactory build can loop without end on one parameter
  value, inside compiled code. Each set is built in a forked child killed at the limit; where there is no
  fork, builds run in the harness's own process with no limit.
- Exit 0 when nothing is attributed to circuitRF, 1 otherwise.

## The environment

1. **Python 3.12** — the version every kit tested accepts (one accepts no other).
2. A fresh virtual environment per kit pin: `python3.12 -m venv <env>`.
3. **gdsfactory at the kit's own pin** (its `Requires-Dist: gdsfactory…` line).
4. **The kit with `--no-deps`, then its open dependencies.** Some kits list a proprietary package
   that none of their cells uses; installing without it is the point of this step. Install what the kit's
   cells then fail to import, one at a time — the harness reports a missing module by the name the error
   gives.

Several kits pinning the same gdsfactory can share one environment.

## What it checks, and how a failure is attributed

1. `circuitrf new workspace --tech none`, then `circuitrf import pdk --module <m> --python <interp>
   --allow-code --technology`. The import's report is saved as `import.txt`.
2. Every cell of the kit is classified by the harness on its own (no readable signature, required
   arguments, raises when gdsfactory builds and flattens it at its defaults) and compared with the
   reason the import report gives. A cell named like a circuitRF built-in is out of scope.
3. Per offered cell, the parameter sets. A set gdsfactory refuses is the kit's refusal and is not sent.
4. One `.clay` per cell, one PCell snapshot and placement per set, a top `.clay` per chunk, and
   `circuitrf convert <top>.clay -o <chunk>.gds` — which rebuilds every placed cell through the kit's
   worker before writing. A refused chunk is split until the refusal names a single set.
5. Per layer, the XOR of circuitRF's structure against gdsfactory's GDS. **Pass: XOR area ≤ edge count ×
   1 DBU²** (edges of both regions). Pins: every port except `placement` has a pin of that name, within
   1 DBU, of the same width and outward angle.

| Attribution | When |
|---|---|
| gdsfactory or the kit | it fails in gdsfactory alone: a set gdsfactory refuses; a cell it cannot build at its defaults; circuitRF matching gdsfactory's in-process flattening while gdsfactory's own GDS differs from it; a cell whose two gdsfactory builds of one set differ (it draws a clock, not its parameters) |
| circuitRF | it builds in gdsfactory and fails, or differs, through circuitRF — including a not-offered reason the harness disagrees with |
| out of scope | a decision of the series excludes it (a cell named like a built-in) |

A failure attributed to circuitRF is fixed in the code that owns it and the matrix is re-run **for that
kit only** — with `--cells` for the cell in question first. The report says what broke; do not re-run
the whole matrix to find out.

## Timing

`report.md` gives the import (which describes the kit), the total `convert` time, and generate p50/p95
per cell — read from the order the convert wrote the generated cells, the gap between one cell's layout
and the next, with the first of each run (which starts the worker) left out.
