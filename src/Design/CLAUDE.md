# `src/Design` — the design-layer document artifacts

Standing instructions for `CircuitRF.Design`. Created 2026-08-26 by
`docs/sonnet-briefs/brief-cli-em-verb.md`, which carved it out of `CircuitRF.Ui` so `src/Cli` could
run an EM setup. Detail of that move is in `src/Ui/Layout/Em/RESOLVED.md`.

## What this project is

**The files a design is stored in, and the code that reads, writes and CREATES them.** Concretely:

| Folder | Holds |
|---|---|
| `Layout/` | the layout model + `.clay` reader, geometry/units/angles, flatten, booleans, the spatial index, the cell-layout resolver, and the technology model + `.ctech` reader/validator |
| `Layout/Em/` | the `.cem` model + reader, the cross-section and planar extractors, port extraction, `EmRunService`, `EmSetupResolver`, SnP provenance |
| `Layout/Drc/` | the whole design-rule check — the engine, the region and predicate evaluators, the wire-to-artwork check, and the `.clay`/`.ctech` format's own waiver record and layer-expression parser. It crossed in AUT-4 so `circuitrf check` runs design rules with no display; `DrcRunReport` and `WBondWireClearance` stayed in `src/Ui` |
| `Layout/Assembly/` | the `.wasm` assembly rule-file model, its reader, its resolver and the built-in rule set the DRC engine falls back to when a design references no `.wasm` (AUT-4) |
| `Layout/PCells/` | `PCellValue`, the generators, registry, generated-cell store/lifecycle and the kit-script client (`Wire/`); `GeneratedCellsRun` rebuilds a layout's cells headlessly. The handle solver stayed in `src/Ui` |
| `Cells/` | the `.ccell` cell-folder format, the atomic write behind every save, `CellCreate` — the writer of an empty `.csch`/`.csym`/`.clay`, which the GUI's New Cell / New Schematic / New Symbol / New Layout call — and `CellViewFileValidator`, which answers whether a file would survive being adopted as a view (AUT-4) |
| `Workspace/` | the `.cws` reader, R-fgn-3's ancestor-workspace walk, and `WorkspaceCreate` — the four operations File ▸ New Workspace performs after its dialog returns |
| `Schematic/` | the `.csch` model + persistence, schematic geometry, net extraction, the cell/kit resolvers and the SPICE-import builders — the editors and sessions stayed in `src/Ui` (AUT-2) |
| `Symbol/` | the `.csym` model + persistence, symbol geometry, pin names and sides (AUT-2) |
| `Theming/` | `Rgba`, because a technology's layers carry colours |
| `Results/` | where a run's grouped `.npy` lands and what it is called |

## What must NOT come in here

**Anything that draws, docks, undoes, or observes a canvas.** This project is on the far side of the
UI firewall and `tests/Firewall.Tests` asserts it references no Avalonia, transitively. That
assertion is not decoration: every file in here sat next to Avalonia until the day it moved, so the
gate is what keeps `circuitrf em` buildable at the later date when someone reaches for a `Dispatcher`
in the layout reader.

Things that deliberately stayed in `src/Ui` and should not follow: `LayoutEditorViewModel` and its
sixteen partials, the DRC engine's two UI-side companions — `DrcRunReport`, which posts a run to
the Messages panel, and `WBondWireClearance`, which reads a per-USER preference — the PCell handle
solver and the per-user trust preferences, `EmSetupEditorViewModel`, `EmBackAnnotation`,
`TechEditorViewModel`, the technology importers, and `AppPreferences`.

## Two rules that are easy to break by accident

**Diagnostics are RETURNED, never posted.** `TechnologyResolver`, `EmSetupResolver` and
`ResultsWriter` all hand back what they have to say and let the caller decide — the GUI raises
Messages, the CLI writes to stderr. A parameter of type `IMessageSink` in here is the shape of the
mistake.

**A preference is an ARGUMENT.** `EmRunService.Run` takes its core cap rather than reading
`AppPreferencesIo`, because headless there is no preferences file and no user to have set one. The
same applies to anything else the GUI stores per-machine.

## Who consumes it

`src/Ui` and `src/Cli`, one way only. `src/Ui/GlobalUsings.cs` lists every namespace that moved here,
in one place, rather than in ~300 `using` lines — that file is the map when a type seems to appear
from nowhere in `src/Ui`.

**And that direction is now load-bearing for CREATION, not just for reading.** `circuitrf new
workspace` / `new cell` / `import part` exist because `WorkspaceCreate`, `CellCreate` and
`ComponentImport` are here and the GUI calls them — an operation that lives only in a view model is
not a capability, and a verb that re-implements one diverges from it silently
(`brief-automation-3-authoring-verbs.md`). So a new "make an artifact" operation belongs HERE with a
shell wrapper in `src/Ui`, never in a view model with a copy in `src/Cli`.
`tests/Ui.Tests/AuthoringCliVerbTests` holds that shut both ways: byte identity against the
in-process call, and a comment-stripped source scan proving the view model kept no second copy.

## Gate

Plain `dotnet test` at the repo root. `tests/Firewall.Tests` is the one that must never be skipped
when touching this project's references; `tests/Ui.Tests` holds every layout, technology and EM test.
`tests/Ui.Tests/Em/EmCliVerbTests.cs` is the end-to-end proof that a headless run and a Simulate
write the same bytes to the same path. It launches the real CLI and still runs in well under a
second, so it is in the routine gate rather than the `Benchmark` tier — see its own header for why
it launches the BUILT `CircuitRF.Cli.dll` rather than `dotnet run`.
