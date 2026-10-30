# gdsfactory kits as PCell generators

A gdsfactory kit is an ordinary Python package that defines a `Pdk`: a table of cell functions, a
layer map, and (in some kits) a stack and material cards. circuitRF runs its cells through the same
PCell worker road a `cni` cell library already takes. This page is how, and why each choice was made.
Series: `docs/sonnet-briefs/brief-gdf-0-overview.md`; this page covers brief 2.

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
site-packages directories, a `-I -c` script importing only `sys` and `sysconfig` — and then reads
files only: every `*.dist-info/METADATA` whose `Requires-Dist` names gdsfactory (unconditionally; a
requirement only under an extra is an optional integration, not a kit). For each it reports the
distribution, version, `Requires-Python`, top-level module (`top_level.txt`, else `RECORD`) and
installed directory. **No kit module is imported to list kits.** A kit **source tree** is recognised
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

## 5. Generate

`gf.get_component(name, **params)` with only the parameters that differ from the default, so
gdsfactory's own cell cache stays effective. Flattened with `get_polygons(by="tuple")`; holes are kept.
Coordinates are scaled once: kit DBU × the kit's `dbu` gives µm, and `crf.dbu(µm × tech.dbu_per_micron)`
gives circuitRF DBU — never `round()`, never `int()`.

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

**A cell named like a circuitRF built-in is not offered.** Built-ins resolve first, case-insensitively,
so the kit's cell of that name is unreachable; the report says so under its own reason. gdsfactory's
generic kit has two: `spiral` and `via`.

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
- `tools/pcell-python/verify.py` checks the bridge imports, and answers, with gdsfactory hidden.
