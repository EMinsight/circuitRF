# Brief 1 — the open kits on the importer circuitRF already has

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf1-n` · **Phase:** measure, fix, CLI
**Area:** `src/Core/Pdk/` (`PdkImporter`, `PdkFormatRegistry`, `KitSymbolFileReader`), `src/Design/Schematic/`
(`PdkPartInstaller`), `src/Ui/ViewModels/WorkspaceViewModel.cs` (the import sequence at the two
`PdkImporter.Import` call sites), `src/Cli/Authoring.cs`, `src/Cli/Serve/ToolCatalog.cs`, `src/Core/RESOLVED.md`
**Depends on:** — · **Blocks:** 5
**Rule:** overview §0. No kit, foundry, process name, device name or model name in the repo. Fixtures
are synthetic. Results are recorded by process ("kit C: 130 nm CMOS").

---

## 0. Why this comes first

The question "will an open kit's parts appear in the Library palette with symbols?" is answered by
the **upstream** kit trees, not by the gdsfactory packages (overview §3). Kit A's upstream tree is
already proven. Kits C and D have the same shape (text symbol files, SPICE-dialect libraries with
corners, Verilog-A sources), but **nobody has run them through File ▸ Import PDK**. This brief does
that before anything new is built. It measures first and fixes what breaks **in circuitRF**. It never
patches a kit.

## 1. `R-gdf1-1` — get the upstream trees

- Each gdsfactory kit's README links its upstream repository. Clone kits A, C and D shallow, and
  sparse where a tree is large. You need the symbol, model and technology folders, not the
  standard-cell libraries.
- **Kit C keeps its symbols and its models in separate repositories.** Measure whether the importer
  pairs them as they stand: one tree beside the other, the delivery shape `pdk-external-devices.md`
  §1 already describes. Do not invent a merge step before measuring.
- Clones live **outside the repo**. Nothing in the repo (code, tests, comments, RESOLVED) names
  where they came from.

## 2. `R-gdf1-2` — measure, per kit, in a scratch harness

Drive `PdkImporter.Import` from a throwaway harness, which is the established fastest way to see what
a kit holds. Do not use a committed test, because it would name the kit. Record a table per kit:

| Measure | Meaning |
|---|---|
| assets by kind and support | the import report's own classification |
| parts | how many reach the palette |
| parts with a symbol | the answer to the owner's question |
| parts with a model that resolves | the model card or subcircuit found, with corners listed |
| parts whose model needs a compiled `.osdi` | and whether one is present. Upstream trees ship Verilog-A **sources**, and the kit's own install step compiles them. circuitRF compiles nothing on a kit's behalf, so report it |
| unrecognised files | each one by extension, counted |
| layout artwork | which parts get artwork through `cni`, and which upstream cells are written for a different layout tool's macro system that circuitRF cannot run (expected for kit D, and for kit C's layout half) |
| import time | wall clock, cold and warm |

Then, in a scratch workspace, for **one part of each device family** (MOSFET, HBT where present,
resistor, MIM/MOM capacitor, diode, varactor):

1. Place it from the palette.
2. Extract it (`circuitrf netlist`).
3. Run a DC operating point (`circuitrf dc`) with the kit's typical corner.
4. Run `circuitrf explain --analysis` to confirm the model card used is the kit's own and not a
   fallback.

**This is not a numerical validation.** It needs an externally generated reference, which this brief
does not create, and the completion note says so in those words.

## 3. `R-gdf1-3` — fix what breaks in circuitRF

Every failure gets attributed to one of three causes:

- **circuitRF defect.** Fix it, with a synthetic fixture reproducing the file shape, never the kit's
  file.
- **The kit's own defect.** Report it in the import report's own words and change nothing.
- **Out of scope.** For example, layout PCells written for another tool. Report it and change
  nothing.

The kit A bring-up found nine traps, listed in `src/Ui/CLAUDE.md` and the PDK `RESOLVED.md` files.
Read them first. **Several of them are invisible unless a part is placed the way placement places
it** (blank-not-null `ModelLibrary`, text-kind parameters ignored silently, `TYPE` swallowed by the
selector). The harness must place parts through the real placement path, with every declared
parameter present.

Record the findings in `src/Core/RESOLVED.md` or `src/Ui/Schematic/RESOLVED.md`, wherever the fixed code lives (never `CLAUDE.md`).

## 4. `R-gdf1-4` — `circuitrf import pdk`

Brief 5's acceptance harness has to import a kit with no display, and a PDK import is currently
reachable only from a view model. That is not a capability (`CLAUDE.md`, the `new` / `import part`
rule).

- **`R-gdf1-4a`** Extract the import sequence both `WorkspaceViewModel` call sites perform into
  **one** framework-free function in `src/Design`. It covers: `PdkImporter.Import`, the `.cws` PDK
  reference, `KitPCellLibrary.EnsureDeclared`, the technology report and the palette composition
  inputs. The view model calls it. A comment-stripped source scan proves the view model kept no second
  copy (the `AuthoringCliVerbTests` pattern).
- **`R-gdf1-4b`** `circuitrf import pdk <kit-dir> --into <workspace> [--name n] [--allow-code] [--json]`.
  - Prints the report's counts and every refusal sentence.
  - Kit code (`cni` PyCells) runs only with a **recorded** consent or `--allow-code`, which grants
    consent for this one process. It is never recorded (overview D2).
  - Without either, the import still succeeds, and it says which generators were not started and why.
  - `import` stays one verb with nouns.
- **`R-gdf1-4c`** Add it to `ToolCatalog` (the `import` tool's `what=pdk`).

## 5. Tests (one per claim, synthetic)

1. One test per circuitRF defect fixed in §3, each with a synthetic file of the failing shape.
2. `import pdk` run as a process gives the same `.cws` reference and manifest **bytes** as the
   in-process function. The source scan finds no second copy in the view model.
3. Without consent, `import pdk` starts no interpreter. A counting seam on the worker resolver
   proves it.

Run only the classes you touch.

## 6. Completion note (in the brief, by process only)

- The §2 table for kits A, C and D.
- The answer for each kit: **how many parts land in the Library palette with a symbol**.
- The list of kit defects and out-of-scope items.
- The overview §3 paragraph rewritten from "expected" to measured.
