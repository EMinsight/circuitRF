# gdsfactory kits as PCell generators

A gdsfactory kit is an ordinary Python package that defines a `Pdk`: a table of cell functions, a
layer map, and (in some kits) a stack and material cards. circuitRF runs its cells through the same
PCell worker road a `cni` cell library already takes. This page is how, and why each choice was made.
Series: `docs/sonnet-briefs/brief-gdf-0-overview.md`; this page covers brief 2.

## 0. What a kit holds, and where each part lands

| A gdsfactory `Pdk` holds | circuitRF counterpart |
|---|---|
| `cells`: name → function returning polygons on `(layer, datatype)` plus ports (name, centre, width, outward degrees, layer, type) | a PCell generator; every port but a `placement` one is a `Pin(name, x, y, layer, width, outward_deg)` (§5) |
| `layers`: name → `(layer, datatype)` | `LayerKey`, and the name a generated shape falls back to (§5) |
| `layer_views`: colours, visibility, written as a layer-properties file | the layer table `LayerPropertiesReader` reads (§6a) |
| `layer_stack`: per patterned layer, thickness, `zmin`, material name | the `.ctech` stackup, through `ProcessTechnologyBuilder` (§6a) |
| material cards (some kits): conductivity, permittivity | `StackupLayer.SigmaSm` / `Epsr`, and the technology's materials (§6a) |
| circuit models (S-parameter functions) | not used — circuitRF's engine is the simulator |

Lengths are µm floats; parameters are plain function arguments with no declared dimension.

## 0a. The decisions, and why

- **D2 — consent is unchanged, and nothing runs before it.** Kits are listed by reading METADATA files;
  only the interpreter's own version is asked (that runs the interpreter, not the kit). Consent keys on the
  installed directory with links resolved, so another environment asks again. A headless run honours a
  recorded decision or a one-run `--allow-code`/`--trust-kit`, and never records one. *Why:* a kit is
  somebody else's Python; a list that ran it to describe it would run it before the user agreed to.
- **D3 — floats pass through exactly, in the kit's units.** Every float is a `real`; the adapter scales
  only the geometry that comes back. *Why:* not every float is a length — an inductance of 3.33e-11 read as
  one rounds to 0 DBU — and "every float is a length, with an override list" is silently wrong on every
  non-length nobody listed. *Cost:* no drag handles, and the editor says "kit units (µm for dimensions)".
- **D4 — the interpreter is per user, never in the workspace.** The workspace records the module and the
  version last seen; a different version is a NOTE. *Why:* a workspace travels, and a path from another
  machine is at best wrong and at worst someone else's environment.
- **D5 — circuitRF installs nothing.** It states the requirement (≥ 3.12 and the kit's own
  `Requires-Python`) and refuses an interpreter that misses it, naming the version. *Why:* an application
  running `pip` in a user's environment changes it in ways nobody asked for.
- **D6 — the proprietary companion package is never needed.** Only gdsfactory and the kit are imported; a
  missing module is reported by the name its error gives. *Why:* measured — the kits that list it build
  every cell without it.
- **D7 — no new pairing rule.** A generator reports its declared model (the `model` default, else
  `info["model"]`) and `KitPaletteMerge`'s existing steps decide; a refused pairing is reported with the
  parameters that did not cover. *Why:* a layout cell and a symbol naming the same quantity differently is
  a mapping problem with no structural answer; guessing pairs the wrong device silently.

## 1. The road — one process kind, one wire, one consent

`KitPCellLibrary.EnsureDeclaredGdsfactory` writes into the workspace exactly what a `cni` library
gets — a `<module>-pcells/` folder holding `pcell-generators.json` and `kit_entry.py` — and the
entry script is:

```python
import circuitrf_pcell as crf
from gdsfactory_bridge import register_pdk

result = register_pdk("<module>")
for problem in result.problems:
    print(problem, file=__import__("sys").stderr)
crf.run()
```

`gdsfactory_bridge` (in `tools/pcell-python/`, shipped beside the application with `circuitrf_pcell`
and `cni`) is to a gdsfactory `Pdk` what `cni.bridge` is to a `cni` library. There is no new process
kind, no new wire message, no second consent mechanism and no second palette path. **Importing the
bridge never imports gdsfactory**: `register_pdk` does, so an environment without it gets one sentence
(`GDSFACTORY_MISSING`), never a traceback — `tools/pcell-python/verify.py` holds that.

## 2. Finding kits without running them

`GdsfactoryKitDiscovery` asks the chosen interpreter one thing — `sys.version_info` and its
site-packages directories, a `-I -S -c` script importing only `os`, `sys` and `sysconfig` (`-S`
because `site` executes every `.pth` file in site-packages; the directories are the ones the interpreter
would search, in its order — a virtual environment's, then the user scheme only when it is used, then
the base interpreter's when the environment includes it) — and then reads
files only: every `*.dist-info/METADATA` whose `Requires-Dist` names gdsfactory (unconditionally; a
requirement only under an extra is an optional integration, not a kit). For each it reports the
distribution, version, `Requires-Python`, top-level module (`top_level.txt`, else `RECORD`) and
installed directory. A distribution that requires gdsfactory but installs no package directory — an
editable install, above all — is **listed as refused**, naming its source folder to import instead.
**No kit module is imported to list kits.** A kit **source tree** is recognised
structurally: a package whose `__init__.py` head imports gdsfactory and constructs `Pdk(`.

Refusals name the fact: an interpreter that is not Python; below 3.12 or below the kit's own
`Requires-Python`; an environment with no gdsfactory kit; **gdsfactory itself missing** although a kit
requires it — what a `--no-deps` install leaves behind. circuitRF installs nothing.

## 3. Consent, the interpreter, the reference

- **Consent is keyed on the kit's installed directory with every link resolved**
  (`PCellTrustStore.ResolvedDirectory`) inside the existing `PCellTrustStore`. A link and its target
  are one decision; two environments are two. A link target is itself resolved again — macOS reaches
  temporary directories through `/var` → `/private/var`, and resolving only the last hop left two
  spellings of one directory. `PCellKit.TrustKey` carries the key, so the prompt, the resolver and a
  headless run agree. `cni` kits keep their manifest-folder key: re-keying them would turn every
  recorded answer back into a question.
- **…only while the declaration adds no code of its own.** An entry script that is not circuitRF's
  generated one (code lines compared; comments ignored), or a `pythonPath` reaching anywhere but the
  source tree's own root, keys the kit on its declaration folder instead — a workspace from someone
  else is asked about, not run on permission given to the kit. The worker runs with `-P`, so the entry
  script's folder is not on `sys.path`, and the chosen environment must hold the module at the very
  directory the declaration names: another environment's copy is refused, never run.
- **The consent prompt** shows Package, Version, Location and Interpreter rows for a gdsfactory kit,
  and says "Running this kit runs its Python code with your user account."
- **The manifest records `Interpreter = null`**, the module (`Module`) and the version seen
  (`ModuleVersion`) — optional fields, absent on every existing manifest, no `SchemaVersion` bump.
- **The interpreter is a per-user preference keyed by module name** (`AppPreferences.GdsfactoryInterpreters`,
  key `gdsfactory_interpreters`, beside the trust decisions). A workspace that travels never hands
  anyone a path from somebody else's machine. With none recorded the application asks once (the
  interpreters on `PATH`, plus "Choose…"); a headless run uses `--python` first, then the recorded
  choice, and with neither refuses with a sentence naming `--python`.
- **`KitRoot` is the kit's installed directory and `Sources = ["${kit}"]`**, so consent, the cache key
  and repair all anchor on the kit. The cache key also folds in the **gdsfactory version** the
  interpreter's environment reports — a gdsfactory upgrade can change geometry with the kit untouched.
  Never site-packages. (A `${kit}` source used to be combined with the manifest's folder, named a
  directory that does not exist, and dropped out of the key silently; `PCellGeneratorContentHash` now
  resolves it through `ResolveRef`.)
- **A recorded version different from the installed one is one NOTE per kit per open**, never a refusal.
  The consent prompt states the installed version; re-importing the kit updates the recorded one.

## 4. Describe

`register_pdk(module)` imports the module, finds its `Pdk` (`module.PDK`, else the only `Pdk` at module
level, else a problem naming the module), activates it, and for each `pdk.cells` entry reads
`inspect.signature` in declaration order:

| Default | Declared as |
|---|---|
| `bool` | `flag` |
| `int` | `integer` |
| `float` | **`real`, passed through exactly** — never `length` |
| `str` | `text` |
| `None`, sequence, callable, spec object | not offered; the default always applies; counted per cell |
| no default | the **cell** is not offered; the problem names the parameters |

**Floats are the kit's own numbers.** Not every float is a length (an inductance of `3.33e-11` treated
as one rounds to zero DBU), so none is converted. Effects: no drag handles (handles need `length`
parameters), and the parameter editor states whose units the number is in — the host adds the hint
"kit units (µm for dimensions)" to every unitless real of a gdsfactory kit (`PCellParameterInfo.Hint`;
nothing on the wire carries it). The editor shows a unitless number far from 1 in full round-trip form:
five decimals displayed that inductance as "0", and an edit commits what the row shows.

Layer-name text parameters (a `str` default that names a layer in `pdk.layers`) stay ordinary text
parameters, ordered **after** every other parameter. The **declared model** is the `model` parameter's
default, else `info["model"]` from a default build; one only in `info` is declared as a computed text
parameter `model`, so `KitPaletteMerge`'s existing model step can pair it and nothing is ever passed
back to the cell.

Every cell is built — and flattened — once at its defaults while describing. A cell that raises is
listed with its exception's last line and offered no further: gdsfactory's own all-angle components
build and then fail to flatten (an API mismatch inside gdsfactory), and they are reported with that
message rather than offered and failing on placement. Nothing is worked around.

**Describing runs in a forked child** where the platform can fork (brief-gdf-6 R-gdf6-6 D5b): the child
builds every cell and returns plain data through a pipe; the worker registers the generators and builds
nothing until it is asked to generate. gdsfactory keeps every cell of a process in one layout, by name, so
building the whole kit in the worker left fixed-GDS cells behind whose names a later import reused —
skipping the kit's own add-ports step, so a standard cell came back with its geometry and no pins, and no
cache clear restores them. Windows has no fork: there the describe stays in-process, and a fixed-GDS cell
that shares a name with a cell another one imported first may still come back without its ports.

## 5. Generate

`gf.get_component(name, **params)` with only the parameters that differ from the default, so
gdsfactory's own cell cache stays effective. **A cell built again with other parameters may raise "Cellname … already
exists"** from a fixed-name helper cell an earlier build left in the layout (D5a): the named cells are
deleted together with every cell containing them, gdsfactory's cache is cleared so nothing cached still
points at one, the cell is built once more, and the result's diagnostics say so. A second refusal is
gdsfactory's. Flattened with `get_polygons(by="tuple")`; holes are kept.
Coordinates are scaled once: kit DBU × the kit's `dbu` gives µm, and `crf.dbu(µm × tech.dbu_per_micron)`
gives circuitRF DBU — never `round()`, never `int()`.

**A cell that prints is harmless.** stdout is the wire, and gdsfactory's own components `print()`; the
worker's `run()` claims it (`circuitrf_pcell.claim_stdout`: the wire is a duplicate of descriptor 1, and
descriptor 1 points at stderr), and `register_pdk` redirects Python-level output while it describes. Before
brief 5 one such print desynchronised the stream and the cell was refused.

Layers map by `(layer, datatype)` to the technology's layer, else by the kit's layer name; **a layer the
technology lacks is kept on the kit's own number and reported once per generate**. **Every port is a
pin, whatever its `port_type`, except `placement`** — port type is gdsfactory's routing vocabulary, and
a coplanar line's ports may be typed `optical`. `placement` ports are alignment marks and are counted.

## 6. Import, and its report

File ▸ Import PDK gains **"A Python package (gdsfactory)…"**: pick an interpreter, pick from the kits
discovery found (a kit below the interpreter's version is listed, dimmed, with the reason), consent,
import. "Kit folder…" on a folder holding a gdsfactory source tree takes the same flow. The headless
form is `circuitrf import pdk --module` (`docs/design/cli.md` §34.1). Both call `GdsfactoryKitImport`.

After consent the kit is described once more by `python -m gdsfactory_bridge.report <module>` — the same
`register_pdk`, into a private registry — and the report states: cells offered; cells not offered,
grouped by reason; parameters not offered (count, list on expand); pins dropped as `placement`; and every
model pairing `KitPaletteMerge` made, and every one it refused with the parameters that did not cover
(`KitPaletteMerge.ExplainModelPairings`, report only).

**Two kits offering one cell name: the gdsfactory kit's wins** (owner, 2026-10-30). A kit imported both as its
own tree and as its gdsfactory package offers many cells under one name in both (22 on the open 130 nm BiCMOS
kit); `PCellWorkerResolver` starts gdsfactory kits first, so the package's cell is the one drawn, and each
collision is still named. Between two kits of one kind the first found wins, as before.

**A cell named like a circuitRF built-in is not offered.** Built-ins resolve first, case-insensitively,
so the kit's cell of that name is unreachable; the report says so under its own reason. gdsfactory's
generic kit has two: `spiral` and `via`.

## 6a. The kit's technology (brief-gdf-3)

`python -m gdsfactory_bridge.technology <module>` READS a kit's process and decides nothing: its layer
map, the path of a layer-properties file its own layer views wrote (read with `LayerPropertiesReader`,
then deleted), each stack level's layer (or layer expression), z, thickness, material, sidewall angle and
info, and every material card the kit's own package exposes. **Cards are found by type, never by module
name**: any module-level object, or one inside a dict, list or tuple there, whose `rf` regime states a
conductivity or a permittivity. It runs the kit, so it runs only after consent, through the same
interpreter, path and `PYTHONPATH` as `report` (`GdsfactoryKitImport.RunBridge`). The brief's `-I` is not
used: it would drop the `PYTHONPATH` the bridge and a source-tree kit are found through.

`GdsfactoryStackConverter` (`src/Design/Layout/TechImport`) turns that into a `ProcessStackDescription` in
a stack file's embedded-conductor convention and hands it to **`ProcessTechnologyBuilder.Build`** — there
is no second builder. What a kit states that a stack file cannot (a conductivity on a card, a loss
tangent, an explicit drawn layer) was added to `ProcessStackEntry`, and a stack file never sets any of it.
The decisions, each stated in the import report:

- **Substrate**: the lowest level lying wholly below z = 0 and at least 10 µm thick. None stated → its top
  is taken at z = 0 (gdsfactory's wafer surface) and its thickness is the builder's
  `DefaultSubstrateThicknessUm` (200 µm), a WARNING.
- **Device layer**: a level that reaches below the substrate's top, or is made of what the substrate is
  made of (the substrate level's material, or — with none — what the levels below z = 0 are made of).
  **Device layers are never in the stack**: they overlap each other and the substrate, and a stack is an
  ordered pile of slabs, so carrying them would lift every metal by their summed thickness. A via landing
  on one is a contact to the front end and is reported, not carried.
- **Vias and conductors are decided together, top down.** In an alternating stack every metal also runs
  from one via's top to the next via's bottom, so geometry alone cannot tell them apart; nothing lies on
  the topmost level of a chain, so it cannot be a via. A level a via lands on is a conductor; a level
  whose top meets a conductor's bottom, with something conductor-capable beneath, is a via. A level with
  a card stating σ ≥ 1e5 S/m is a conductor; one with no conductivity stated becomes one only when a via
  lands on it — and a via whose OWN card states its conductivity may only land under a confirmed
  conductor (a card-less passivation on the top metal would otherwise turn the parity over). Two levels on
  one mask are never each other's via (a capacitor's dielectric and its plate).
- **Dielectric**: a card states εr and σ below the conductor threshold. Anything else is **not used**, and
  the z it occupies is filled.
- **Gap filling**: every stretch between the substrate's top and the highest level that no conductor and
  no stated dielectric covers is a slab, εr from the card the kit's stated dielectrics hold most of by
  thickness (a NOTE), else εr 1 (a WARNING: "εr unknown between z = … and … µm; set before running EM").
  Never matched on a material's name.
- **Materials**: the kit's cards become the technology's materials and each slab that took its numbers
  from one names it — after the builder, changing no number (the loader resolves a named material to the
  values already there). A card the stack did not use is then one pick away in the Stackup tab.

Measured against kits A–E (scratch environments, nothing committed): A 8 conductors / 7 vias / 13 filled /
2 warnings; B 2 / 1 / 3 / 3; C 6 / 5 / 9 / 8; D 6 / 5 / 8 / 8; E 3 / 2 / 4 / 4.

File ▸ Import PDK's report offers **"Also build the kit's technology"**, checked when the kit states a
stack. It writes `tech/<module>.ctech`, asks before touching an existing file with the merge question
File ▸ Import ▸ Technology asks (the two share `WriteImportedTechnologyAsync`), and becomes the default
only when the workspace has none (`KitTechnologyInstall`). `circuitrf import pdk … --technology` is the
headless form (`docs/design/cli.md` §34.1).

## 6b. Symbols from a kit's own drawings (brief-gdf-4)

circuitRF draws no symbol on a kit's behalf (`PCellKitSchematicParts`). A kit that ships its **own** drawing
of a cell is another matter: the drawing is the kit's, and circuitRF adds only where the pins go.

- **Found by name.** `register_pdk` looks, per offered cell, for a file whose stem IS the cell's name in the
  package's `symbols/` directory, the cell module's `symbols/` directory, or beside the cell's module —
  compared against the directory listing, so a case-insensitive file system cannot answer for a different
  spelling. A `.csym` wins over an `.svg` of the same stem. The report carries `symbols` (cell → `.csym`),
  `glyphs` (cell → `.svg`) and, for a glyph cell, `ports` (its non-`placement` ports at the default build:
  name, µm, outward degrees).
- **Installed at import, as files.** `KitGlyphSymbol.Install` (`src/Design/Schematic`) writes
  `symbols/<cell>.csym` beside the kit's declaration — copied from the kit's `.csym`, else built from the
  glyph — which is exactly where `PCellKitSchematicParts.FindSymbolFile` looks, so the palette rebuild that
  follows an import mounts them with no other change. An existing file is never overwritten (it may be one
  somebody edited); the report says it was kept. Import PDK and `circuitrf import pdk` both call it.
- **The glyph.** Lines, polylines, polygons, rects, circles, ellipses, paths (curves kept as curves; arcs,
  and every curve of a FILLED outline, flattened) and text become `.csym` primitives, through `g`
  transforms. The box is the `viewBox` (else width/height, else what is drawn), scaled uniformly onto the
  connection grid and centred. No colour is carried and the kit's `@media` (dark-mode) rules are not read,
  but its plain class and element rules ARE, for what is filled and how text is set — a kit may say
  `fill:none` only through a class. A fill keeps the kit's own opacity as the shape's fill strength
  (`SymbolFill`: 0 the outline alone, 1 solid, between the outline over the symbol colour at that
  strength), so a kit shading a region at 25 % stays 25 %; a near-white fill with no stroke is the
  glyph's background and is not drawn. Anything else — a filter, an image, a `use`, a gradient — is
  dropped and **counted** in the report.
- **The pins.** Outward 180° → left edge, 0° → right, 90° → top, 270° → bottom. On an edge the pins keep the
  ports' order across it and are spread evenly on the grid; a lead line ending on that edge takes the
  pin, and among the order-keeping assignments the one that moves the pins least wins, ties to the most
  centred. More ports than lead ends puts the rest on the grid and the report says which. A pin is never
  dropped. Where the glyph's edge or lead end is not on the grid, the lead is carried out to the pin.
- **No parameters, and no model.** The parameters are the generator's, as for every synthesised part. A
  symbol makes a cell placeable and visible to Update Layout from Schematic and LVS; it gives the cell no
  circuit behaviour — an EM run's SnP does that. The report says so beside the list.

## 6c. Export

A placed gdsfactory cell is an ordinary generated cell, so GDSII export is circuitRF's and needs no Python.
Two things the acceptance matrix found had to hold for that to be true (`src/Design/RESOLVED.md`, brief-gdf-5):
the hierarchical writers follow a headless run's generated-cell redirects (a cell whose snapshot no longer
hashes to today's name — every placed kit cell after a gdsfactory upgrade — used to export as nothing), and
a polygon over 8191 points is written as abutting pieces covering exactly the same area
(`GdsiiPolygonSplit`; one GDSII element holds no more).

## 7. Tests

- Tier A (default gate): `tests/Ui.Tests/Gdsfactory/GdsfactoryAdapterTests.cs` — discovery from synthetic
  site-packages trees with a counting probe seam, the refusals, consent keying through a link, the
  manifest round trip, the per-user interpreter, the `${kit}` content key, and float pass-through.
- Tier B (`[GdsfactoryFact]`, `CRF_GDSFACTORY_PYTHON`): `tests/Ui.Tests/Gdsfactory/GdsfactoryKitTests.cs` —
  the synthetic kit in `Fixtures/synthkit/` and gdsfactory's generic kit, generated through circuitRF's
  worker, exported with `GdsiiExport`, and compared per layer against gdsfactory's own GDS by
  `Fixtures/compare_gds.py`, through gdsfactory's own layout-engine dependency. Normalised: a difference that vanishes when shrunk by one DBU is
  rounding, not a shape (gdsfactory's own flattening and its own GDS of a ring differ by exactly such
  slivers).
- Symbols from glyphs (brief-gdf-4), Tier A: `tests/Ui.Tests/Gdsfactory/KitGlyphSymbolTests.cs` — pins on a
  two-lead glyph's lead ends, four ports on an edge with two lead ends, a dropped and counted `<filter>`,
  class rules deciding the fill while dark-mode rules do not, and the bridge's own lookup preferring a `.csym` (plain Python, no gdsfactory). The synthetic kit ships
  `symbols/` glyphs and one `.csym` for the UiSnapshot `--gdsfactory symbols` picture.
- `tools/pcell-python/verify.py` checks the bridge imports, and answers, with gdsfactory hidden — `report`
  and `technology` both.
- The technology (brief-gdf-3): Tier A `tests/Ui.Tests/Layout/TechImport/GdsfactoryStackConverterTests.cs`
  (classification, gap filling with and without a card, the via-span rule, and the same stack as a stack
  file and as JSON building byte-identical `.ctech` files); Tier B
  `tests/Ui.Tests/Gdsfactory/GdsfactoryTechnologyTests.cs` (the synthetic kit's `process.py` through the
  real reader, and the generic kit with zero refusals).
