# Brief 5 — the acceptance matrix, and the page a user reads

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf5-n` · **Phase:** acceptance, docs
**Area:** new `tools/GdsfactoryAcceptance/` (`run.py`, `README.md`), `tests/Ui.Tests/PackagingScriptTests.cs`,
`docs/design/pcell-gdsfactory-bridge.md`, `docs/user/src/reference/pdk-authoring.md` (or a new page beside it
in `_nav.txt`), `src/Design/RESOLVED.md`
**Depends on:** 1, 2, 3 · **Blocks:** —
**Rule:** overview §0. The harness contains **no kit name**: it takes a module name as an argument.
Results are recorded by process.

---

## 0. What "extensive" means here

Every cell of every kit in overview §2 (A–E) goes through circuitRF, **headless and end to end**:

1. import
2. consent
3. describe
4. generate
5. export

Each result is compared against **gdsfactory's own GDS** for the same cell at the same parameters.
This is done at the defaults, and again with each numeric parameter varied one at a time. Pins are
checked against ports. A cell failing anywhere is attributed to one of three causes:

- **gdsfactory or the kit:** it fails in gdsfactory alone too.
- **circuitRF:** it builds in gdsfactory and fails, or differs, through circuitRF.
- **Out of scope:** a decision in the overview excludes it.

## 1. `R-gdf5-1` — the harness

`tools/GdsfactoryAcceptance/run.py` runs **under the kit's own interpreter**, so it can build the
reference GDS in-process:

```
<kit-python> -I tools/GdsfactoryAcceptance/run.py --module <m> --circuitrf <path-to-cli> --out <dir>
                                                  [--variations 2] [--cells <glob>] [--max-cells N]
```

1. `circuitrf new workspace <dir>/ws`, then
   `circuitrf import pdk --module <m> --python <sys.executable> --into … --allow-code --technology`.
   The import report is saved.
2. **The parameter sets.** For each offered cell:
   - the defaults
   - then each `real` at ×0.5 and ×2
   - each `integer` at default + 1
   - each `flag` flipped

   One parameter at a time, **never a cross product**. A set gdsfactory itself refuses is recorded as
   the kit's refusal and is not sent to circuitRF.
3. **The reference.** `gf.get_component(name, **params).write_gds(…)`, in the harness process.
4. **circuitRF's version.**
   - Author one `.clay` per cell, placing one instance per parameter set, using the instance syntax
     `circuitrf reference topic=layout` documents.
   - Then run `circuitrf convert <cell>.clay -o <cell>.gds`.
   - **First, verify that `convert` regenerates kit cells headlessly through the worker.** If it
     exports a generated instance as empty, or as a cached stale cell, that is a circuitRF defect.
     Fix it in this brief, because a headless export that silently drops generated geometry is the
     worst outcome this series could produce.
5. **The comparison**, per layer:
   - XOR the two polygon sets with gdsfactory's own boolean API.
   - Pass when the XOR area is ≤ (edge count × 1 DBU × 1 DBU), which allows for rounding and nothing
     more.
   - Pins: every port except `placement` has a pin with the same name, position within 1 DBU, the
     same width and the same outward angle.
6. **Timing:** describe wall-clock, generate p50/p95 per cell, and the total.
7. **Output:** `<dir>/report.md` and `report.json`. One row per (cell, set) giving outcome, attribution,
   XOR area and the pin diff. Nothing is written inside the repo.

The README states the environment recipe in generic terms:

- Python 3.12.
- Install gdsfactory at the kit's pin.
- Install the kit `--no-deps` and then its open dependencies, because some kits list a proprietary
  package their cells do not use (overview D6).

## 2. `R-gdf5-2` — the gates

| Kit | Gate |
|---|---|
| A, B, C, E | **Every (cell, set) gdsfactory builds matches** (XOR within tolerance, pins equal). Zero failures attributed to circuitRF |
| D | Every cell gdsfactory builds matches. **The set of failures equals gdsfactory's own failures**, so circuitRF adds no failure. Its broken fixed-GDS cells and port-less FETs are recorded as the kit's |
| all | Every **not offered** cell has a reason in the import report that matches the harness's own attribution |
| all | Brief 3's technology builds, and the NOTE/WARNING counts are recorded |
| A, B | Brief 3 §5's EM gates, run once each, short |

A failure attributed to circuitRF is fixed in the brief that owns the code (2 or 3). The matrix is
re-run **for that kit only**. Do not re-run the whole matrix to find out what broke: the report says.

## 3. `R-gdf5-3` — never bundled (D10)

Extend `PackagingScriptTests` so every publish tree a packaging script produces contains **no
`site-packages` directory, no `*.dist-info` directory and no `.whl`**. The assertion names no package,
because it does not need to.

## 4. `R-gdf5-4` — docs

- **`docs/design/pcell-gdsfactory-bridge.md`** (brief 2 starts it, this brief completes it):
  - the mapping table (overview §1)
  - D2–D7 with their reasons
  - the classification and gap-filling rules (brief 3 §2)
  - the attribution rule
  - the measured kit table **by process**
- **The user page.** "Using a gdsfactory kit": what to install, choosing the environment, the consent
  prompt, what you get, and what you do not get. Cover:
  - **What you get:** layout cells in the Library palette (merged onto a kit part's tile when they
    pair, otherwise layout-only), a technology built from the kit's stack, and every filled value
    listed.
  - **What you do not get:** circuit models from a gdsfactory package (the upstream kit provides
    those, through Import PDK), and drag handles on gdsfactory cells. Parameters are in the kit's own
    units: "µm for dimensions".
  - **Kits that list a proprietary dependency** can be installed without it, worded generically (D6).
  - Edit the source page only. **DocGen is the owner's, at the end of the series**, never per
    brief.
- **`src/Design/RESOLVED.md`:** the attribution findings that were circuitRF's, once each.

## 5. Completion note (here, by process)

- The §2 table, filled in.
- Per kit: cells offered / matched / not offered (by reason) / failing (by attribution), and timings.
- **The answer to the owner's palette question**, combining brief 1's symbol counts with this brief's
  layout tiles, per kit: parts with a symbol and artwork, parts with a symbol only, and layout-only
  tiles.
