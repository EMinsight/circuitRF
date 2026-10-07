# Tuning and optimization

**Status:** TO-1 built (model, file format, catalog, in-memory overrides, `check`/`explain`/`reference`).
TO-2 … TO-12 briefed (`docs/sonnet-briefs/brief-tuneopt-*.md`). The overview brief
(`brief-tuneopt-0-overview.md`) holds the decisions D1–D17 in full and is binding; this note is the
standing reference for what is built, and restates the decisions only as far as the code depends on them.

---

## 1. What exists

| Piece | Where | What it is |
|---|---|---|
| The model | `src/Core/Design/TuningSetup.cs` | `TuningSetup` = `TunableEntry` list + `TuningPreset` list + `OptimizationGoal` list + `OptimizerSettings`. `TunableKey` splits a key. `OptimizerAlgorithms.Ids` fixes the algorithm ids. |
| `.cnl` grammar | `src/Core/Netlist/AnalysisDirectiveSchema.Tuning.cs` | The four directives, their keys and bare words — the table the reader checks keys against and the `reference` topics print. |
| `.cnl` read/write | `src/Core/Netlist/TuningDirectiveText.cs` | Reader and writer of `tune`, `preset`, `goal`, `optimize`. Called by `CnlReader` and `CnlWriter`. |
| `.csch` block | `SchematicPersistence` (`CschTuning`) | The `"Tuning"` object; see `project-file-formats.md`. |
| Extraction | `NetExtractor.Extract` / `NetlistSchematic.Build` | Copy the setup across in both directions, so every run verb's `.cnl` carries it with no extra code. |
| Tunable catalog | `src/Design/Optimization/TunableCatalog.cs` | Every tunable of a schematic at any depth, and the setup keys that name nothing. |
| In-memory overrides | `src/Design/Optimization/TunableOverrides.cs` | The design to elaborate with tuned values substituted. Writes nothing. |
| Rules | `src/Design/Optimization/TuningValidator.cs` + `TuningDiagnostics.cs` | What `check` reports and the windows refuse on. |
| CLI | `check`, `explain --tunables`, `reference tuning`, `reference goals` | See `cli.md`. |

**Why the model is in `src/Core` and not `src/Design/Optimization`.** The brief placed the model classes
in `src/Design/Optimization`. A `TestBench` carries the setup and the `.cnl` reader and writer live in
`src/Core`, which cannot reference `src/Design` — so the plain data sits beside `Analysis` and
`Measurement`, and everything that needs a schematic, a resolver or a workspace is in
`src/Design/Optimization`, as the overview's D12 has it.

## 2. What is tunable (D1, D2)

A tunable is a **component parameter** whose stored value is a plain number with an optional unit
(`47`, `47 pF`, `47pF`, `1.2e-9`), or a **VAR row** whose expression is one. Anything else — an
expression, a reference to a cell parameter, a string, an enum, a bool — is **not offered at all**. The
way to tune an expression is to tune the variable it reads.

- **A sub-circuit instance's declared cell parameters are offered per instance** (`X1.Rbias`,
  `X2.Rbias`), whether the instance overrides them or inherits the default. An inherited one is reported
  `IsDefault = true` at the cell's default, so Push knows to add an override.
- **A value inside a cell is identified by the cell DEFINITION** (`DUT:R3.R`): tuning it moves it in every
  instance, and the catalog reports how many (`DUT · ×2`).
- **Only values the drawing holds.** Extraction adds parameters a drawing never stated — a microstrip's
  substrate (`H`, `T`, `Er`, …) comes from the workspace technology. Those are not values of the design and
  Push has no row to write them into, so the catalog offers an instance parameter only when the component
  in the drawing holds it. A `.cnl` holds every value it states.
- **Integer parameters** (`m`, `Nf`, `NumFingers`, `Fingers`) are offered and flagged `IsInteger`.
  Identity numbers (`Num`, `NumPorts`, `NumFreqs`) are not offered.
- A VAR that an **enabled parametric sweep** sweeps is offered with `DisabledReason = "swept by SW1"`.
- **Read-only** (`ReadOnlyReason`): a cell from a kit's netlist rather than a schematic, a cell outside
  the workspace, or a cell whose schematic file is read-only. Such a value can still be tuned and
  optimized; Push skips it.

**The catalog walks the extracted netlist, not the drawing.** `TunableCatalog.Discover(schematic,
resolver, workspaceRoot)` extracts once with the caller's resolver — the one Simulate uses — so cell names
in keys are spelled exactly as the `.cnl` spells instance types (a second `Amp` from another workspace is
`Amp_2` in both), and there is no second resolver to disagree with the first.
`NetExtractor.ExtractionResult.CellKeys` maps each library name back to its cell folder for the read-only
test.

**Default range on first activation (D4):** a positive value v → [v/2, 2v]; a negative one → [1.5v, 0.5v];
zero → [0, 1] in the parameter's unit, flagged `RangeGuessed`. `auto` scale is log when min > 0 and
max/min ≥ 10.

## 3. Keys (D3)

| Key | Names |
|---|---|
| `R1.R` | parameter `R` of top-level instance `R1` |
| `Wline` | top-level VAR `Wline` (by name — it survives moving the row to another VAR block) |
| `X1.Rbias` | declared cell parameter `Rbias` of top-level sub-circuit instance `X1` |
| `DUT:R3.R` | parameter `R` of `R3` inside cell `DUT` — every instance of `DUT` |
| `DUT:Wline` | VAR `Wline` inside cell `DUT` |
| `DUT:X5.Wf` | declared parameter `Wf` of instance `X5` inside cell `DUT` |

The cell part is the cell **as the `.cnl` spells its instance type** — `CellScope.NameFor` in
`NetExtractor`: the cell folder's leaf name, or `name_2`, `name_3` … when two cells with one leaf name
meet in one design. **A key that names nothing is never an error**: a preset skips it, `check` warns, a
run notes it.

## 4. The `.cnl` grammar

The authoritative table is `AnalysisDirectiveSchema.TuningDirectives`; `circuitrf reference tuning` and
`reference goals` print it.

```
tune <key> [min=<v> [unit]] [max=<v> [unit]] [scale=auto|lin|log] [step=<v> [unit]]
           [discrete=none|integer|preferred] [tune=1] [opt=1]
preset "<name>" [created=<yyyy-MM-ddTHH:mm:ssZ>] [lasttuned=1] <key>=<v> [unit] ...
goal <Name> = <expression> [analysis=<A>] [over=<axis> lo=<v> [unit] hi=<v> [unit]]
     (le|ge|eq) <limit> [unit] [to <limit> [unit]]  |  (in|out) <a> [unit] <b> [unit]
     [weight=<w>] [enabled=false]
optimize [algorithm=<id>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> s|ms|min|h]
         [cost=lsq|minimax] [analyses=goals|all] [seed=<n>] [parallel=<n>] [alg.<option>=<v>] ...
```

Settled details:
- **Units** follow a value after a space, as `measure` and instance lines write them. A value that would
  not read back as itself is written quoted.
- **A goal's expression** runs from after `=` to the first token, at bracket depth zero, that the grammar
  owns (a `key=` or a goal type), searched from the second token. The writer quotes the expression when
  that rule would end it early (`goal X = "Lout - out" ge 0`). Its own spacing is kept verbatim.
- **Sloped limits** are `le -15 to -10`: the limit at `lo`, `to`, the limit at `hi`. Only `le`/`ge`/`eq`
  slope; `in`/`out` take a band of two limits.
- **Inside a goal line a length in inches is spelled `inch`**, because `in` is a goal type.
- **`timelimit`** takes `s`, `ms`, `min` or `h`. These are local to this grammar: the shared unit table
  has no time units, and adding a bare `s` there would change what a bare word after an instance-line value
  means.
- **`created`/`lasttuned` come before a preset's values**; once a value key is seen, every later
  `key=value` is a value.
- **Booleans** read `1/0/true/false/yes/no`; the writer writes `1` and omits defaults.
- **Unknown keys** are a warning naming the key (`cnl.tuning.unknown-key`) and are **kept** in the
  record's `Extra` map and written back — the room yield's tolerances will need (overview §5).
- **A malformed line is refused** with a `cnl.tuning.*` diagnostic (`TuningDirectiveDiagnostics`) and its
  line number.
- **Byte stability:** `CnlWriter → CnlReader → CnlWriter` and `.csch → .cnl → .csch → .cnl` reproduce the
  tuning lines exactly. `TuningPreset.Created` is held to the whole second, the precision the `.cnl`
  spelling carries.

## 5. Applying values in memory

`TunableOverrides.Apply(tb, lib, values, setVariables)` returns copies of the testbench and library with
each value substituted — a top-level instance parameter or VAR in the testbench, a cell's in its library
definition, an inherited cell parameter as an added override. Objects are replaced, never mutated, so the
input keeps its own. A value written without a unit keeps the row's existing unit (typing `47` into a row
whose unit column says pF means 47 pF). **The gate is equality with typing**: elaborating the result equals
elaborating a design in which the same text had been typed.

`--set` applies first; a tuned value naming a variable `--set` also sets is a refusal naming both.

## 6. What `check` reports

Errors: min ≥ max; `scale=log` with min ≤ 0; a bound, limit or range end that is not a number; a goal
whose analysis is not declared; a goal range that holds no point of the analysis's grid (computed for
`freq` of an S-parameter sweep, also through a wrapping parametric sweep, and for a sweep's own variable;
other axes are checked at run time); an expression that does not parse; an `in`/`out` goal without two
limits or with an inverted band; a sloped limit with no range; a key naming a value D1 does not offer;
an unknown algorithm id; a `timelimit` that is not a duration. **Warning:** a key — in a `tune` line or a
preset — that names nothing. An unknown function name in a goal expression parses, exactly as in a
`measure` line; it is reported where `measure` reports one.

## 7. Room left for yield

A variable entry, a goal and the optimizer settings each carry an `Extra` map, and both serializations
keep unknown keys, so a tolerance can be added to a `tune` line without a format break.
